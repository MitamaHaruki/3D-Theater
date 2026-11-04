using System.Globalization;
using System.Numerics;
using System.Windows;
using System.Windows.Media;
using Theater3D.Editor;

namespace Theater3D.App.Preview;

/// <summary>区画の左下に出す、X・Y・Z 軸の向きの目印。</summary>
public sealed class AxisIndicator : FrameworkElement
{
    private static readonly (Vector3 Axis, Brush Brush, string Label)[] Axes =
    [
        (Vector3.UnitX, Freeze(new SolidColorBrush(Color.FromRgb(0xE0, 0x52, 0x52))), "X"),
        (Vector3.UnitY, Freeze(new SolidColorBrush(Color.FromRgb(0x5B, 0xC4, 0x6A))), "Y"),
        (Vector3.UnitZ, Freeze(new SolidColorBrush(Color.FromRgb(0x4A, 0x7E, 0xEA))), "Z"),
    ];

    private static readonly Typeface LabelFace = new(new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.Bold, FontStretches.Normal);

    private CameraBasis _basis = CameraBasis.FromAngles(0f, 0f);

    public AxisIndicator()
    {
        IsHitTestVisible = false;
    }

    public void SetBasis(CameraBasis basis)
    {
        if (basis == _basis)
        {
            return;
        }
        _basis = basis;
        InvalidateVisual();
    }

    protected override void OnRender(DrawingContext drawingContext)
    {
        var center = new Point(RenderSize.Width / 2, RenderSize.Height / 2);
        double length = (Math.Min(RenderSize.Width, RenderSize.Height) / 2) - 9;
        double pixelsPerDip = VisualTreeHelper.GetDpi(this).PixelsPerDip;

        // 奥を向く軸から描き、手前の軸が上に来るようにする
        foreach (var (axis, brush, label) in Axes.OrderByDescending(a => Vector3.Dot(a.Axis, _basis.Forward)))
        {
            var direction = new System.Windows.Vector(Vector3.Dot(axis, _basis.Right), -Vector3.Dot(axis, _basis.Up));
            Point end = center + (direction * length);
            drawingContext.DrawLine(new Pen(brush, 2), center, end);
            drawingContext.DrawEllipse(brush, null, end, 7, 7);
            var text = new FormattedText(label, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, LabelFace, 9, Brushes.Black, pixelsPerDip);
            drawingContext.DrawText(text, new Point(end.X - (text.Width / 2), end.Y - (text.Height / 2)));
        }
    }

    private static Brush Freeze(Brush brush)
    {
        brush.Freeze();
        return brush;
    }
}
