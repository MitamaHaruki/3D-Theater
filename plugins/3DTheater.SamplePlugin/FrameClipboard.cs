using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Theater3D.Plugin.Sdk;

namespace Theater3D.SamplePlugin;

/// <summary>
/// ほかのアプリとの連携の見本（指示書 7.2 の Interop・7.4）: SDK で描いた今のフレームを、クリップボードに写す。
/// どのアプリでも受け取れる「クリップボード」を連携の口にする（特定のアプリだけの決まりを使わない）。
/// </summary>
internal static class FrameClipboard
{
    /// <summary>
    /// 2 つの形で写す: 透明度を保つ PNG（"PNG" の形。読めるアプリが使う）と、白い背景に重ねた画像（ふつうの画像の形。透明度を読めないアプリが使う。
    /// 透明な所が黒くならないように）。
    /// </summary>
    public static void Copy(RenderedImage image)
    {
        var data = new DataObject();
        data.SetData("PNG", new MemoryStream(image.Png), autoConvert: false);
        data.SetImage(OnWhite(image));
        Clipboard.SetDataObject(data, copy: true);
    }

    private static BitmapSource OnWhite(RenderedImage image)
    {
        byte[] pixels = (byte[])image.Pixels.Clone();
        for (int i = 0; i + 3 < pixels.Length; i += 4)
        {
            int alpha = pixels[i + 3];
            for (int c = 0; c < 3; c++)
            {
                pixels[i + c] = (byte)(((pixels[i + c] * alpha) + (255 * (255 - alpha)) + 127) / 255);
            }
            pixels[i + 3] = 255;
        }
        var bitmap = BitmapSource.Create(image.Width, image.Height, 96, 96, PixelFormats.Bgra32, null, pixels, image.Width * 4);
        bitmap.Freeze();
        return bitmap;
    }
}
