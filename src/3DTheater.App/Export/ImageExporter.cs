using System.Buffers;
using System.Collections.Concurrent;
using System.IO;
using System.Numerics;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Theater3D.App.Shell;
using Theater3D.App.Rendering;
using Theater3D.Domain;
using Theater3D.Editor;
using Theater3D.Engine.Interop;
using Theater3D.Media;

namespace Theater3D.App.Export;

/// <summary>書き出しの進み具合（何枚目まで・全部で何枚・今のフレーム）。</summary>
internal readonly record struct ExportProgress(int Done, int Total, int Frame);

/// <summary>動画に入れる音声（ファイル・動画の 0 秒が音声の何秒目か・音量）。</summary>
internal sealed record ExportAudio(string Path, double StartSeconds, float Volume);

/// <summary>
/// 画像・動画の書き出し（指示書 6.3）。シーンのカメラから、書き出し専用の画面外の描画先へ 1 フレームずつ描き、
/// PNG に保存するか、動画（MP4）に書く。
/// - 映すのはモデルだけ（格子・座標軸・ギズモ・ボーン・選択の枠・確認用の立方体は映さない）。照明・影・輪郭線はプレビューと同じ
/// - 物理は必ず焼き込みの結果を使い、足りなければその場で計算する（プレビューと同じ姿勢になるように）
/// - 品質はプレビューに関係なく <see cref="RenderQuality.Export"/>（4 倍のなめらかさ・細かい影）。
///   なめらかにするときは、さらに 2 倍の大きさで描いて縮める。透明にするときは、透明度をかけ合わせた色を割り戻してから保存する
/// - 書き出しの間はプレビューを描かない（フレームごとにプレビューまで描き直さないように）
/// - 映像効果（色調補正・ブルーム・周辺減光）とイラスト風の表現もプレビューと同じようにかける。ブルームの広がりは画像の大きさに
///   対する割合、線画の太さと紙の粒は画像の高さに合わせるので、大きさを変えても同じ見え方になる
/// - 描画と読み出しは UI スレッドで、縮小と保存（動画は符号化）は別のスレッドで行い、少し先まで並べて進める
/// 終わったら（やめた・失敗したときも）、元のフレームに戻す。動画は別の名前で書いてから置き換えるので、
/// やめた・失敗したときは書きかけを消し、同じ名前の元のファイルはそのまま残る。
/// </summary>
internal static class ImageExporter
{
    /// <summary>書き出して、保存できた枚数（動画はフレームの数）を返す。やめたときは <see cref="OperationCanceledException"/>。</summary>
    public static async Task<int> RunAsync(EditorContext context, ExportOptions options, ExportAudio? audio, IProgress<ExportProgress>? progress,
        CancellationToken token)
    {
        NativeEngine engine = context.Engine ?? throw new InvalidOperationException("描画エンジンが使えないため、書き出せません");
        CameraNode camera = context.MainCamera ?? throw new InvalidOperationException("シーンのカメラがありません");
        IReadOnlyList<int> frames = options.Frames;
        int originalFrame = context.Playback.CurrentFrame;
        NativeView? view = null;
        FrameSink? sink = null;

        context.Playback.Pause();
        context.Physics.ForceBaked = true;
        context.RenderLoop.IsSuspended = true;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(options.Paths[0]))!);
            view = engine.CreateOffscreenView(options.RenderWidth, options.RenderHeight);
            int samples = RenderQuality.FitSampleCount(RenderQuality.Export.SampleCount, options.RenderWidth, options.RenderHeight);
            try
            {
                view.SetSampleCount(samples);
            }
            catch (NativeEngineException ex) when (ex.Result == NativeResult.OutOfMemory)
            {
                Logging.AppLog.Warning("書き出しをなめらかに描くための描画先を作れなかったため、なめらかさなし（MSAA なし）で描きます", ex);
            }
            sink = options.IsVideo ? new VideoSink(options, context.Playback.FrameRate, audio) : new PngSink(options);
            for (int i = 0; i < frames.Count; i++)
            {
                token.ThrowIfCancellationRequested();
                sink.ThrowIfFailed(); // 保存・符号化の失敗は、その場で知らせて止める
                await sink.WaitForRoomAsync(token);
                byte[] pixels;
                try
                {
                    pixels = RenderFrame(context, view, camera, options, frames[i]);
                }
                catch
                {
                    sink.ReleaseRoom();
                    throw;
                }
                sink.Submit(pixels, i);
                progress?.Report(new ExportProgress(i + 1, frames.Count, frames[i]));
                await Dispatcher.Yield(DispatcherPriority.Background); // 画面の更新と「やめる」を受け付ける
            }
            int written = await sink.CompleteAsync();
            sink = null;
            return written;
        }
        finally
        {
            if (sink is not null)
            {
                await sink.AbortAsync(); // やめた・失敗した: 書きかけの保存を待って片付ける（書きかけの動画は消す）
            }
            view?.Dispose();
            context.RenderLoop.IsSuspended = false;
            context.Physics.ForceBaked = false;
            context.Playback.Seek(originalFrame);
            foreach (LoadedModel model in context.Models.Values)
            {
                model.IsPoseDirty = true;
            }
            context.RenderLoop.InvalidateAll();
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
        Vector4 clear = options.UsesTransparency ? Vector4.Zero : new Vector4(options.Background, 1f);
        var parameters = new FrameParameters(flags, matrices.View, matrices.Projection, Matrix4x4.Identity, Matrix4x4.Identity, items, default,
            lightDirection, lightColor, SceneLighting.EdgePixelScale(height), RenderSettings.GroundShadowColor,
            RenderQuality.Export.ShadowMapSize, clear, context.Rendering.ToNative(height));
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

    /// <summary>なめらかにするときは縮めた画素を、そうでなければ描いた画素をそのまま返す（<paramref name="scratch"/> を使い回す）。</summary>
    private static byte[] Shrink(byte[] pixels, ExportOptions options, ref byte[]? scratch)
    {
        if (options.Supersample == 1)
        {
            return pixels;
        }
        scratch ??= new byte[options.Width * options.Height * 4];
        ImageExport.Downsample(pixels.AsSpan(0, options.RenderWidth * options.RenderHeight * 4), options.RenderWidth, options.RenderHeight,
            options.Supersample, scratch);
        return scratch;
    }

    /// <summary>描いた画素を受け取って保存する側。先に進めすぎないよう、受け取れる数を決めておく。</summary>
    private abstract class FrameSink(int room)
    {
        private readonly SemaphoreSlim _room = new(room);

        public Task WaitForRoomAsync(CancellationToken token) => _room.WaitAsync(token);

        public void ReleaseRoom() => _room.Release();

        /// <summary>画素（借りた配列）を渡す。返すのは受け取った側。</summary>
        public abstract void Submit(byte[] pixels, int index);

        public abstract void ThrowIfFailed();

        /// <summary>全部保存し終えるのを待って、保存できた数を返す。</summary>
        public abstract Task<int> CompleteAsync();

        /// <summary>やめる（書きかけを待ってから片付ける）。</summary>
        public abstract Task AbortAsync();
    }

    /// <summary>PNG に 1 枚ずつ保存する（2 枚まで並べて保存する）。</summary>
    private sealed class PngSink(ExportOptions options) : FrameSink(2)
    {
        private readonly List<Task> _pending = [];
        private int _saved;

        public override void Submit(byte[] pixels, int index)
        {
            string path = options.Paths[index];
            _pending.Add(Task.Run(() =>
            {
                try
                {
                    Save(pixels, options, path);
                    Interlocked.Increment(ref _saved);
                }
                finally
                {
                    ArrayPool<byte>.Shared.Return(pixels);
                    ReleaseRoom();
                }
            }));
        }

        public override void ThrowIfFailed()
        {
            if (_pending.FirstOrDefault(t => t.IsFaulted) is { Exception: { } error })
            {
                throw error.InnerException ?? error;
            }
        }

        public override async Task<int> CompleteAsync()
        {
            await Task.WhenAll(_pending);
            return _saved;
        }

        public override async Task AbortAsync()
        {
            try
            {
                await Task.WhenAll(_pending);
            }
            catch (Exception)
            {
                // 最初の失敗は呼び出し側で知らせる
            }
        }

        /// <summary>縮めて（なめらかにするとき）、透明度を割り戻して（透明にするとき）、PNG に保存する。</summary>
        private static void Save(byte[] pixels, ExportOptions options, string path)
        {
            int width = options.Width;
            int height = options.Height;
            byte[]? scratch = null;
            byte[] image = Shrink(pixels, options, ref scratch);
            BitmapSource bitmap;
            if (options.UsesTransparency)
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

    /// <summary>
    /// 動画（MP4）に書く。符号化は専用のスレッドで行い（Media Foundation は作ったスレッドで使い続ける）、3 枚まで先に描いておける。
    /// 音声は、各フレームの終わりの時刻までの分を、そのフレームの前に書く（映像と音声を交互に入れる）。
    /// 「名前.書き出し中.mp4」に書き、書き終えてから「名前.mp4」に置き換える（拡張子で入れ物の形式が決まるので .mp4 のまま）。
    /// </summary>
    private sealed class VideoSink : FrameSink
    {
        private readonly ExportOptions _options;
        private readonly int _frameRate;
        private readonly ExportAudio? _audio;
        private readonly string _path;
        private readonly string _temporaryPath;
        private readonly BlockingCollection<(byte[] Pixels, int Index)> _queue = new(boundedCapacity: 3);
        private readonly TaskCompletionSource _done = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private volatile Exception? _failure;
        private volatile bool _aborted;
        private int _written;

        public VideoSink(ExportOptions options, int frameRate, ExportAudio? audio)
            : base(3)
        {
            _options = options;
            _frameRate = frameRate;
            _audio = audio;
            _path = Path.GetFullPath(options.Paths[0]);
            _temporaryPath = Path.ChangeExtension(_path, ".書き出し中.mp4");
            var thread = new Thread(Run) { IsBackground = true, Name = "動画の書き出し" };
            thread.SetApartmentState(ApartmentState.MTA);
            thread.Start();
        }

        public override void Submit(byte[] pixels, int index) => _queue.Add((pixels, index));

        public override void ThrowIfFailed()
        {
            if (_failure is { } failure)
            {
                throw failure;
            }
        }

        public override async Task<int> CompleteAsync()
        {
            _queue.CompleteAdding();
            await _done.Task;
            if (_failure is { } failure)
            {
                DeletePartialFile();
                throw failure;
            }
            try
            {
                File.Move(_temporaryPath, _path, overwrite: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                DeletePartialFile();
                throw new IOException($"書き出した動画を「{Path.GetFileName(_path)}」に置き換えられませんでした" +
                    "（同じ名前のファイルを、ほかのアプリで開いていないか確かめてください）。", ex);
            }
            return _written;
        }

        public override async Task AbortAsync()
        {
            _aborted = true;
            if (!_queue.IsAddingCompleted)
            {
                _queue.CompleteAdding();
            }
            await _done.Task;
            DeletePartialFile();
        }

        private void Run()
        {
            try
            {
                var settings = new VideoSettings(_options.Width, _options.Height, _frameRate, _options.VideoBitrate(_frameRate),
                    _audio is null ? 0 : AudioExportSource.SampleRate, AudioExportSource.Channels, ExportOptions.AudioBitrate);
                using VideoWriter writer = VideoWriter.Create(_temporaryPath, settings);
                using AudioExportSource? audio = _audio is null ? null : new AudioExportSource(_audio.Path, _audio.StartSeconds, _audio.Volume);
                short[] audioBuffer = [];
                long audioFrames = 0;
                byte[]? scratch = null;
                foreach (var (pixels, index) in _queue.GetConsumingEnumerable())
                {
                    try
                    {
                        if (_aborted || _failure is not null)
                        {
                            continue; // 残りは捨てる（借りた配列は返す）
                        }
                        if (audio is not null)
                        {
                            long target = (long)Math.Round((index + 1) * (double)AudioExportSource.SampleRate / _frameRate);
                            int count = (int)(target - audioFrames) * AudioExportSource.Channels;
                            if (audioBuffer.Length < count)
                            {
                                audioBuffer = new short[count];
                            }
                            audio.Read(audioBuffer.AsSpan(0, count));
                            writer.WriteAudio(audioBuffer.AsSpan(0, count));
                            audioFrames = target;
                        }
                        byte[] image = Shrink(pixels, _options, ref scratch);
                        writer.WriteVideo(image.AsSpan(0, _options.Width * _options.Height * 4), _options.Width * 4, index);
                        Interlocked.Increment(ref _written);
                    }
                    catch (Exception ex)
                    {
                        _failure = ex; // 専用のスレッドなので、どの失敗も受け止めて、描く側で知らせる
                    }
                    finally
                    {
                        ArrayPool<byte>.Shared.Return(pixels);
                        ReleaseRoom();
                    }
                }
                if (!_aborted && _failure is null)
                {
                    writer.Finish();
                }
            }
            catch (Exception ex)
            {
                _failure ??= ex;
                // 書き手を作れなかったときも、待っている側が止まらないよう、残りを受け取って返す
                foreach (var (pixels, _) in _queue.GetConsumingEnumerable())
                {
                    ArrayPool<byte>.Shared.Return(pixels);
                    ReleaseRoom();
                }
            }
            finally
            {
                _done.TrySetResult();
            }
        }

        private void DeletePartialFile()
        {
            try
            {
                File.Delete(_temporaryPath);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Logging.AppLog.Warning($"書きかけの動画のファイルを消せませんでした: {_temporaryPath}", ex);
            }
        }
    }
}
