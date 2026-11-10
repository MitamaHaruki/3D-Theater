using System.Buffers;
using System.IO;
using System.Numerics;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Theater3D.App.Shell;
using Theater3D.Domain;
using Theater3D.Editor;
using Theater3D.Engine.Interop;

namespace Theater3D.App.Export;

/// <summary>書き出しの進み具合（何枚目まで・全部で何枚・今のフレーム）。</summary>
internal readonly record struct ExportProgress(int Done, int Total, int Frame);

/// <summary>
/// PNG の書き出し（指示書 6.3）。シーンのカメラから、書き出し専用の画面外の描画先へ 1 フレームずつ描き、PNG に保存する。
/// - 映すのはモデルだけ（格子・座標軸・ギズモ・ボーン・選択の枠・確認用の立方体は映さない）。照明・影・輪郭線はプレビューと同じ
/// - 物理は必ず焼き込みの結果を使い、足りなければその場で計算する（プレビューと同じ姿勢になるように）
/// - なめらかにするときは 2 倍の大きさで描いて縮める。透明にするときは、透明度をかけ合わせた色を割り戻してから保存する
/// - 描画と読み出しは UI スレッドで、縮小と PNG への変換・保存は別のスレッドで行う（2 枚まで並べて進める）
/// 終わったら（やめた・失敗したときも）、元のフレームに戻す。
/// </summary>
internal static class ImageExporter
{
    /// <summary>書き出しのセルフシャドウの奥行きの画像（プレビューより細かくする）。</summary>
    private const int ShadowMapSize = 4096;

    private const int MaxPendingSaves = 2;

    /// <summary>書き出して、保存できた枚数を返す。やめたときは <see cref="OperationCanceledException"/>。</summary>
    public static async Task<int> RunAsync(EditorContext context, ExportOptions options, IProgress<ExportProgress>? progress, CancellationToken token)
    {
        NativeEngine engine = context.Engine ?? throw new InvalidOperationException("描画エンジンが使えないため、書き出せません");
        CameraNode camera = context.MainCamera ?? throw new InvalidOperationException("シーンのカメラがありません");
        IReadOnlyList<int> frames = options.Frames;
        IReadOnlyList<string> paths = options.Paths;
        int originalFrame = context.Playback.CurrentFrame;
        int saved = 0;
        var pending = new List<Task>();
        using var slots = new SemaphoreSlim(MaxPendingSaves);
        NativeView? view = null;

        context.Playback.Pause();
        context.Physics.ForceBaked = true;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(paths[0]))!);
            view = engine.CreateOffscreenView(options.RenderWidth, options.RenderHeight);
            for (int i = 0; i < frames.Count; i++)
            {
                token.ThrowIfCancellationRequested();
                ThrowIfAnyFailed(pending); // 保存の失敗は、その場で知らせて止める
                await slots.WaitAsync(token);
                byte[] pixels;
                try
                {
                    pixels = RenderFrame(context, view, camera, options, frames[i]);
                }
                catch
                {
                    slots.Release();
                    throw;
                }
                string path = paths[i];
                pending.Add(Task.Run(() =>
                {
                    try
                    {
                        Save(pixels, options, path);
                        Interlocked.Increment(ref saved);
                    }
                    finally
                    {
                        ArrayPool<byte>.Shared.Return(pixels);
                        slots.Release();
                    }
                }, CancellationToken.None));
                progress?.Report(new ExportProgress(i + 1, frames.Count, frames[i]));
                await Dispatcher.Yield(DispatcherPriority.Background); // 画面の更新と「やめる」を受け付ける
            }
            await Task.WhenAll(pending);
            return saved;
        }
        finally
        {
            try
            {
                await Task.WhenAll(pending); // やめた・失敗したときも、書きかけの保存を待ってから片付ける
            }
            catch (Exception)
            {
                // 最初の失敗は上で知らせる
            }
            view?.Dispose();
            context.Physics.ForceBaked = false;
            context.Playback.Seek(originalFrame);
            foreach (LoadedModel model in context.Models.Values)
            {
                model.IsPoseDirty = true;
            }
            context.RenderLoop.InvalidateAll();
        }
    }

    private static void ThrowIfAnyFailed(List<Task> pending)
    {
        if (pending.FirstOrDefault(t => t.IsFaulted) is { Exception: { } error })
        {
            throw error.InnerException ?? error;
        }
    }

    /// <summary>そのフレームの姿勢にして描き、読み出した画素（BGRA、借りた配列）を返す。</summary>
    private static byte[] RenderFrame(EditorContext context, NativeView view, CameraNode camera, ExportOptions options, int frame)
    {
        context.Playback.Seek(frame);
        foreach (LoadedModel model in context.Models.Values)
        {
            model.IsPoseDirty = true; // 物理の焼き込みの結果はキーが無くてもフレームごとに違う
        }
        context.Poses.UpdateDirtyModels();

        int width = options.RenderWidth;
        int height = options.RenderHeight;
        ViewMatrices matrices = ViewCamera.SceneCameraMatrices(camera, width / (float)height);
        DrawItem[] items = [.. context.VisibleModels.Where(x => x.Model.Mesh is not null)
            .Select(x => new DrawItem(x.Model.Mesh!, x.Node.GetLocalMatrix(), DrawFlags.None))];
        var (lightDirection, lightColor) = SceneLighting.From(context.MainLight);
        FrameFlags flags = FrameFlags.UseLight | FrameFlags.ClearColor;
        if (context.Rendering.SelfShadow)
        {
            flags |= FrameFlags.SelfShadow;
        }
        if (context.Rendering.GroundShadow)
        {
            flags |= FrameFlags.GroundShadow;
        }
        Vector4 clear = options.Transparent ? Vector4.Zero : new Vector4(options.Background, 1f);
        var parameters = new FrameParameters(flags, matrices.View, matrices.Projection, Matrix4x4.Identity, Matrix4x4.Identity, items, default,
            lightDirection, lightColor, SceneLighting.EdgePixelScale(height), RenderSettings.GroundShadowColor, ShadowMapSize, clear);
        view.Render(parameters);

        int bytes = width * height * 4;
        byte[] pixels = ArrayPool<byte>.Shared.Rent(bytes);
        try
        {
            view.ReadPixels(pixels.AsSpan(0, bytes), width * 4); // 描き終わるのを待ってから読む
        }
        catch
        {
            ArrayPool<byte>.Shared.Return(pixels);
            throw;
        }
        return pixels;
    }

    /// <summary>縮めて（なめらかにするとき）、透明度を割り戻して（透明にするとき）、PNG に保存する。</summary>
    private static void Save(byte[] pixels, ExportOptions options, string path)
    {
        int width = options.Width;
        int height = options.Height;
        byte[] image = pixels;
        if (options.Supersample > 1)
        {
            image = new byte[width * height * 4];
            ImageExport.Downsample(pixels.AsSpan(0, options.RenderWidth * options.RenderHeight * 4), options.RenderWidth, options.RenderHeight,
                options.Supersample, image);
        }
        BitmapSource bitmap;
        if (options.Transparent)
        {
            ImageExport.Unpremultiply(image.AsSpan(0, width * height * 4));
            bitmap = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null, image, width * 4);
        }
        else
        {
            // 背景が不透明なら透明度は要らないので、色だけで保存する（ファイルが小さくなる）
            bitmap = new FormatConvertedBitmap(BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null, image, width * 4),
                PixelFormats.Bgr24, null, 0);
        }
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        SafeFile.Write(path, temp =>
        {
            using FileStream stream = File.Create(temp);
            encoder.Save(stream);
        });
    }
}
