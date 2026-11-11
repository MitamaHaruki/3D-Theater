using System.IO;
using System.Numerics;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Theater3D.App.Export;
using Theater3D.Editor;
using Theater3D.Plugin.Sdk;

namespace Theater3D.App.Plugins;

/// <summary>
/// SDK の <see cref="ITheaterRenderer"/> を、書き出しの見本と同じ描き方（<see cref="ImageExporter.RenderSample"/>）で実装したもの（プラグイン 1 つに 1 つ）。
/// 指示書 7.1「3D Theater 専用 SDK：…レンダリング等を公開」。<see cref="Detach"/> の後は描かない（プラグインを無効にしたとき）。
/// </summary>
internal sealed class TheaterRenderService(EditorContext context) : ITheaterRenderer
{
    /// <summary>1 辺の上限（なめらかにするときは 2 倍で描くので、描く大きさの上限の半分）。</summary>
    public const int MaxSide = ExportOptions.MaxRenderSize / 2;

    private bool _attached = true;

    public (int Width, int Height) ExportSize => context.ExportSize;

    public RenderedImage RenderFrame(RenderRequest request)
    {
        if (!_attached)
        {
            throw new InvalidOperationException("プラグインが止まっているため、描けません");
        }
        ArgumentNullException.ThrowIfNull(request);
        if (request.Width is < 1 or > MaxSide)
        {
            throw new ArgumentOutOfRangeException(nameof(request), request.Width, $"幅は 1〜{MaxSide} にしてください");
        }
        if (request.Height is < 1 or > MaxSide)
        {
            throw new ArgumentOutOfRangeException(nameof(request), request.Height, $"高さは 1〜{MaxSide} にしてください");
        }
        int frame = Math.Clamp(request.Frame ?? context.Playback.CurrentFrame, 0, context.Playback.EndFrame);
        var options = new ExportOptions
        {
            Kind = ExportKind.Still,
            Width = request.Width,
            Height = request.Height,
            Smooth = request.Smooth,
            Transparent = request.Transparent,
            Background = Vector3.Clamp(Finite(request.Background), Vector3.Zero, Vector3.One),
            FirstFrame = frame,
            LastFrame = frame,
        };

        BitmapSource bitmap = ImageExporter.RenderSample(context, options);
        int stride = options.Width * 4;
        byte[] pixels = new byte[stride * options.Height];
        bitmap.CopyPixels(pixels, stride, 0);
        if (options.UsesTransparency)
        {
            ImageExport.Unpremultiply(pixels); // SDK では、透明度をかけ合わせていない値で渡す
        }
        else
        {
            for (int i = 3; i < pixels.Length; i += 4)
            {
                pixels[i] = 255;
            }
        }

        BitmapSource straight = BitmapSource.Create(options.Width, options.Height, 96, 96, PixelFormats.Bgra32, null, pixels, stride);
        var encoder = new PngBitmapEncoder();
        // 透明にしないときは色だけで PNG にする（ファイルが小さくなる。書き出しと同じ）
        encoder.Frames.Add(BitmapFrame.Create(options.UsesTransparency ? straight : new FormatConvertedBitmap(straight, PixelFormats.Bgr24, null, 0)));
        using var stream = new MemoryStream();
        encoder.Save(stream);
        return new RenderedImage(options.Width, options.Height, pixels, stream.ToArray());
    }

    /// <summary>もう描かない。</summary>
    public void Detach() => _attached = false;

    private static Vector3 Finite(Vector3 v) =>
        float.IsFinite(v.X) && float.IsFinite(v.Y) && float.IsFinite(v.Z) ? v : Vector3.One;
}
