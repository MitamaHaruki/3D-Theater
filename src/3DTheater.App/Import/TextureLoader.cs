using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Theater3D.Formats;
using Theater3D.Formats.Images;

namespace Theater3D.App.Import;

/// <summary>
/// テクスチャ画像を BGRA 8 ビットにする。PNG・BMP・JPG・GIF・TIFF・DDS は Windows 標準の画像機能（WIC）で、
/// TGA は自前の読み込み（<see cref="TgaDecoder"/>）で読む。スフィア（.spa / .sph）は中身が BMP なので WIC で読める。
/// 別のスレッドから呼んでよい。
/// </summary>
internal static class TextureLoader
{
    public static DecodedImage Load(string path)
    {
        byte[] data = File.ReadAllBytes(path);
        if (Path.GetExtension(path).Equals(".tga", StringComparison.OrdinalIgnoreCase))
        {
            return TgaDecoder.Decode(data);
        }
        try
        {
            return DecodeWithWic(data);
        }
        catch (Exception ex) when (ex is NotSupportedException or FileFormatException or ArgumentException or System.Runtime.InteropServices.COMException)
        {
            // 拡張子と中身が違うことがある（TGA なのに .png など）。最後に TGA として試す
            try
            {
                return TgaDecoder.Decode(data);
            }
            catch (ModelFormatException)
            {
                throw new ModelFormatException($"画像の形式が分かりません（{ex.Message}）");
            }
        }
    }

    private static DecodedImage DecodeWithWic(byte[] data)
    {
        using var stream = new MemoryStream(data, writable: false);
        BitmapDecoder decoder = BitmapDecoder.Create(stream, BitmapCreateOptions.IgnoreColorProfile | BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
        BitmapSource frame = decoder.Frames[0];
        if (frame.Format != PixelFormats.Bgra32)
        {
            frame = new FormatConvertedBitmap(frame, PixelFormats.Bgra32, null, 0);
        }
        int width = frame.PixelWidth;
        int height = frame.PixelHeight;
        var pixels = new byte[width * height * 4];
        frame.CopyPixels(pixels, width * 4, 0);
        return new DecodedImage(width, height, pixels);
    }
}
