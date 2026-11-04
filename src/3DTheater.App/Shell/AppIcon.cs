using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Theater3D.App.Shell;

/// <summary>アプリのアイコン（角を丸めた四角に立方体）。画像ファイルを持たずにその場で描く。</summary>
internal static class AppIcon
{
    public static DrawingImage CreateDrawing()
    {
        var background = new LinearGradientBrush(Color.FromRgb(0x3B, 0x82, 0xF6), Color.FromRgb(0x7C, 0x5C, 0xE0), 45);
        var line = new Pen(Brushes.White, 1.1) { LineJoin = PenLineJoin.Round, StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
        var group = new DrawingGroup();
        group.Children.Add(new GeometryDrawing(background, null, new RectangleGeometry(new Rect(0, 0, 16, 16), 3.5, 3.5)));
        group.Children.Add(new GeometryDrawing(
            new SolidColorBrush(Color.FromArgb(0x40, 0xFF, 0xFF, 0xFF)), line,
            Geometry.Parse("M8,2.6 L12.8,5.3 L12.8,10.7 L8,13.4 L3.2,10.7 L3.2,5.3 Z")));
        group.Children.Add(new GeometryDrawing(null, line, Geometry.Parse("M3.2,5.3 L8,8 L12.8,5.3 M8,8 L8,13.4")));
        var image = new DrawingImage(group);
        image.Freeze();
        return image;
    }

    /// <summary>タスクバー用のビットマップ。</summary>
    public static BitmapFrame CreateBitmap(int size)
    {
        var visual = new DrawingVisual();
        using (DrawingContext dc = visual.RenderOpen())
        {
            dc.DrawImage(CreateDrawing(), new Rect(0, 0, size, size));
        }
        var bitmap = new RenderTargetBitmap(size, size, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        bitmap.Freeze();
        return BitmapFrame.Create(bitmap);
    }
}
