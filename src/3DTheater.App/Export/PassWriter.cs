using System.Buffers;
using System.IO;
using System.Numerics;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Theater3D.App.Rendering;
using Theater3D.App.Shell;
using Theater3D.Domain;
using Theater3D.Editor;
using Theater3D.Engine.Interop;

namespace Theater3D.App.Export;

/// <summary>
/// レンダーパスの書き出し（指示書 6.3。<see cref="ExportPasses"/>）。普通の絵を描いた直後に、同じ姿勢・カメラで、パスごとに描いて別の PNG に保存する。
/// - 奥行き・法線・物の ID・材質の ID・影・発光は、なめらかにしないで描く（縁で値が混ざると、ID や奥行きとして使えなくなるため）
/// - アルファは普通の絵と同じくなめらかに描く（縁の半透明が要るため）
/// - 映像効果・背景・床の映り込み・格子は描かない。モーションブラーはかけない
/// - 奥行きの範囲は最初のフレームで決め、全部のフレームで同じにする（連番で明るさがちらつかないように）。範囲と、物・材質の色の対応表は
///   「名前_passes.json」に書く
/// 描画は画面のスレッドで、PNG の保存は別のスレッドで行う。
/// </summary>
internal sealed class PassWriter : IDisposable
{
    private const int MaxPending = 6;

    private readonly EditorContext _context;
    private readonly ExportOptions _options;
    private readonly CameraNode _camera;
    private readonly NativeView _smoothView;
    private readonly NativeView _flatView;
    private readonly List<Task> _pending = [];
    private (float Near, float Far)? _depth;
    private int _saved;

    private PassWriter(EditorContext context, ExportOptions options, CameraNode camera, NativeView smoothView, NativeView flatView)
    {
        _context = context;
        _options = options;
        _camera = camera;
        _smoothView = smoothView;
        _flatView = flatView;
    }

    /// <summary>パスを書くなら作る（書かないなら null）。<paramref name="smoothView"/> は普通の絵の描画先（アルファに使う）。</summary>
    public static PassWriter? Create(NativeEngine engine, EditorContext context, ExportOptions options, CameraNode camera, NativeView smoothView)
    {
        if (options.PassList.Count == 0)
        {
            return null;
        }
        // なめらかにしない描画先（MSAA なし・書き出しの大きさのまま）
        return new PassWriter(context, options, camera, smoothView, engine.CreateOffscreenView(options.Width, options.Height));
    }

    /// <summary>保存したパスの画像の数。</summary>
    public int Saved => _saved;

    /// <summary>
    /// <paramref name="index"/> 枚目（<see cref="ExportOptions.Frames"/> の順）のパスを描いて保存に回す。普通の絵を描いた直後に呼ぶ（姿勢はそのフレーム）。
    /// </summary>
    public async Task WriteAsync(int index)
    {
        ThrowIfFailed();
        if (_depth is null)
        {
            _depth = DepthRangeNow();
            WriteInfo(_depth.Value);
        }
        foreach (ExportPasses pass in _options.PassList)
        {
            while (_pending.Count(t => !t.IsCompleted) >= MaxPending)
            {
                await Task.WhenAny(_pending.Where(t => !t.IsCompleted)); // 先に進めすぎない（画素の配列を持ちすぎないように）
            }
            byte[] pixels = Render(pass, _depth.Value);
            string path = _options.PassPaths(pass)[index];
            _pending.Add(Task.Run(() =>
            {
                try
                {
                    Save(pixels, pass, path);
                    Interlocked.Increment(ref _saved);
                }
                finally
                {
                    ArrayPool<byte>.Shared.Return(pixels);
                }
            }));
        }
    }

    /// <summary>保存の失敗があれば、その例外を投げる。</summary>
    public void ThrowIfFailed()
    {
        if (_pending.FirstOrDefault(t => t.IsFaulted) is { Exception: { } error })
        {
            throw error.InnerException ?? error;
        }
    }

    /// <summary>全部保存し終えるのを待つ。</summary>
    public async Task CompleteAsync()
    {
        await Task.WhenAll(_pending);
    }

    /// <summary>やめた・失敗した: 書きかけの保存を待つ（失敗は呼び出し側で知らせる）。</summary>
    public async Task AbortAsync()
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

    public void Dispose() => _flatView.Dispose();

    // 書く物（普通の絵と同じ並び。物の番号はこの並びの 1 から）
    private List<(ModelNode Node, LoadedModel Model)> Objects() => [.. _context.VisibleModels.Where(x => x.Model.Mesh is not null)];

    // 今の姿勢の物を囲む箱の角から、奥行きの範囲を決める
    private (float Near, float Far) DepthRangeNow()
    {
        ViewMatrices matrices = ViewCamera.SceneCameraMatrices(_camera, _options.Width / (float)_options.Height);
        var corners = new List<Vector3>();
        foreach (var (node, model) in Objects())
        {
            Matrix4x4 world = node.GetLocalMatrix();
            Vector3 min = model.DisplayBoundsMin;
            Vector3 max = model.DisplayBoundsMax;
            for (int c = 0; c < 8; c++)
            {
                var corner = new Vector3((c & 1) == 0 ? min.X : max.X, (c & 2) == 0 ? min.Y : max.Y, (c & 4) == 0 ? min.Z : max.Z);
                corners.Add(Vector3.Transform(corner, world));
            }
        }
        return ImageExport.DepthRange(corners, matrices.View);
    }

    // 1 つのパスを描いて読み出す（借りた配列。書き出しの大きさ、BGRA）
    private byte[] Render(ExportPasses pass, (float Near, float Far) depth)
    {
        foreach (LoadedModel model in _context.Models.Values)
        {
            model.IsPoseDirty = true; // モーションブラーで途中の時刻の形になっていても、そのフレームの形に戻す
        }
        _context.Poses.UpdateDirtyModels();

        bool smooth = pass == ExportPasses.Alpha;
        NativeView view = smooth ? _smoothView : _flatView;
        int width = smooth ? _options.RenderWidth : _options.Width;
        int height = smooth ? _options.RenderHeight : _options.Height;
        ViewMatrices matrices = ViewCamera.SceneCameraMatrices(_camera, width / (float)height);
        DrawItem[] items = [.. Objects().Select(x => new DrawItem(x.Model.Mesh!, x.Node.GetLocalMatrix(), DrawFlags.None))];
        var (lightDirection, lightColor) = SceneLighting.From(_context.MainLight);
        FrameFlags flags = FrameFlags.UseLight | FrameFlags.ClearColor;
        Vector4 groundShadow = default;
        if (pass == ExportPasses.Shadow)
        {
            if (_context.Rendering.SelfShadow)
            {
                flags |= FrameFlags.SelfShadow;
                if (_context.Rendering.ShadowStyle.RayTraced)
                {
                    flags |= FrameFlags.RayTracedShadows;
                }
            }
            if (_context.Rendering.GroundShadow)
            {
                flags |= FrameFlags.GroundShadow;
                groundShadow = new Vector4(1f, 1f, 1f, _context.Rendering.ShadowStyle.GroundColor.W); // 白で描くと、影の濃さがそのまま白さになる
            }
        }
        var parameters = new FrameParameters(flags, matrices.View, matrices.Projection, Matrix4x4.Identity, Matrix4x4.Identity, items, default,
            lightDirection, lightColor, smooth ? SceneLighting.EdgePixelScale(height) : 0f, groundShadow, RenderQuality.Export.ShadowMapSize,
            Vector4.Zero, ShadowSoftness: pass == ExportPasses.Shadow ? _context.Rendering.ShadowStyle.SoftnessTangent : 0f,
            Pass: ToNative(pass), PassDepthNear: depth.Near, PassDepthFar: depth.Far, RaySamples: RenderQuality.Export.RaySamples);
        view.Render(parameters);

        int bytes = width * height * 4;
        byte[] pixels = ArrayPool<byte>.Shared.Rent(bytes);
        try
        {
            view.ReadPixels(pixels.AsSpan(0, bytes), width * 4);
            if (!smooth || _options.Supersample == 1)
            {
                return pixels;
            }
            // なめらかにするときは縮める（別の配列に。保存は別のスレッドで行うので、使い回さない）
            byte[] shrunk = ArrayPool<byte>.Shared.Rent(_options.Width * _options.Height * 4);
            ImageExport.Downsample(pixels.AsSpan(0, bytes), width, height, _options.Supersample, shrunk.AsSpan(0, _options.Width * _options.Height * 4));
            ArrayPool<byte>.Shared.Return(pixels);
            return shrunk;
        }
        catch
        {
            ArrayPool<byte>.Shared.Return(pixels);
            throw;
        }
    }

    private static RenderPass ToNative(ExportPasses pass) => pass switch
    {
        ExportPasses.Alpha => RenderPass.Alpha,
        ExportPasses.Depth => RenderPass.Depth,
        ExportPasses.Normal => RenderPass.Normal,
        ExportPasses.ObjectId => RenderPass.ObjectId,
        ExportPasses.MaterialId => RenderPass.MaterialId,
        ExportPasses.Shadow => RenderPass.Shadow,
        ExportPasses.Emission => RenderPass.Emission,
        _ => RenderPass.None,
    };

    // 保存する: 奥行きは 16 ビットの白黒、アルファ（透明度）と影（赤）は 8 ビットの白黒、ほかは色
    private void Save(byte[] pixels, ExportPasses pass, string path)
    {
        int width = _options.Width;
        int height = _options.Height;
        ReadOnlySpan<byte> bgra = pixels.AsSpan(0, width * height * 4);
        BitmapSource bitmap;
        switch (pass)
        {
            case ExportPasses.Depth:
                var depth = new ushort[width * height];
                ImageExport.DepthToGray16(bgra, depth);
                bitmap = BitmapSource.Create(width, height, 96, 96, PixelFormats.Gray16, null, depth, width * 2);
                break;
            case ExportPasses.Alpha:
            case ExportPasses.Shadow:
                var gray = new byte[width * height];
                ImageExport.ChannelToGray8(bgra, pass == ExportPasses.Alpha ? 3 : 2, gray);
                bitmap = BitmapSource.Create(width, height, 96, 96, PixelFormats.Gray8, null, gray, width);
                break;
            default:
                bitmap = new FormatConvertedBitmap(BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null, pixels, width * 4),
                    PixelFormats.Bgr24, null, 0);
                break;
        }
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        SafeFile.Write(path, temp =>
        {
            using FileStream stream = File.Create(temp);
            encoder.Save(stream);
        });
    }

    // 奥行きの範囲と、物・材質の色の対応表（合成ソフトで ID の色から物を選ぶため）
    private void WriteInfo((float Near, float Far) depth)
    {
        static string Hex((byte R, byte G, byte B) c) => $"#{c.R:X2}{c.G:X2}{c.B:X2}";
        var objects = new List<object>();
        var materials = new List<object>();
        int index = 0;
        foreach (var (node, _) in Objects())
        {
            index++;
            objects.Add(new { index, name = node.Name, color = Hex(RenderPassColors.ObjectColor(index)) });
            for (int m = 0; m < node.MaterialNames.Count; m++)
            {
                materials.Add(new { @object = index, objectName = node.Name, index = m, name = node.MaterialNames[m], color = Hex(RenderPassColors.MaterialColor(index, m)) });
            }
        }
        var info = new
        {
            format = "3DTheater.passes",
            version = 1,
            description = "3D Theater のレンダーパスの説明。名前_パスの語.png（連番は名前_パスの語_フレーム.png）と一緒に使う",
            passes = _options.PassList.Select(ExportPassInfo.Suffix).ToArray(),
            depth = new
            {
                near = depth.Near,
                far = depth.Far,
                encoding = "16 ビットの白黒。カメラから見る向きの距離が near で 65535（白）、far で 0（黒）。何も描かれていない所は 0",
            },
            objects,
            materials,
        };
        string json = JsonSerializer.Serialize(info, new JsonSerializerOptions { WriteIndented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
        SafeFile.WriteAllText(_options.PassInfoPath, json);
    }
}
