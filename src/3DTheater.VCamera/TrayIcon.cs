using System.Drawing;
using System.Drawing.Drawing2D;
using Forms = System.Windows.Forms;

namespace Theater3D.VCamera;

/// <summary>
/// 通知領域のアイコン（常駐。設計判断記録 98 章）。ダブルクリックで窓を開き、右クリックで「開く・送り始める／止める・正面にする・終了」。
/// アイコンは、送っている間は緑、止めている間は灰色の丸に「V」。
/// </summary>
public sealed class TrayIcon : IDisposable
{
    private readonly Forms.NotifyIcon _icon;
    private readonly Forms.ToolStripMenuItem _toggle;
    private readonly Icon _on;
    private readonly Icon _off;
    private bool _disposed;

    public TrayIcon(Action open, Action toggle, Action recalibrate, Action quit)
    {
        _on = MakeIcon(Color.FromArgb(0x3F, 0xB9, 0x50));
        _off = MakeIcon(Color.FromArgb(0x80, 0x80, 0x80));
        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add("開く", null, (_, _) => open());
        _toggle = new Forms.ToolStripMenuItem("送り始める", null, (_, _) => toggle());
        menu.Items.Add(_toggle);
        menu.Items.Add("正面にする", null, (_, _) => recalibrate());
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add("終了", null, (_, _) => quit());
        _icon = new Forms.NotifyIcon
        {
            Icon = _off,
            Text = "3D Theater VCamera（止めています）",
            ContextMenuStrip = menu,
            Visible = true,
        };
        _icon.DoubleClick += (_, _) => open();
    }

    /// <summary>送っているかを、アイコンとメニューに出す（片付けた後は何もしない。終わるときに、止めた知らせが後から届くため）。</summary>
    public void Update(bool sending)
    {
        if (_disposed)
        {
            return;
        }
        _icon.Icon = sending ? _on : _off;
        _icon.Text = sending ? "3D Theater VCamera（送っています）" : "3D Theater VCamera（止めています）";
        _toggle.Text = sending ? "止める" : "送り始める";
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;
        _icon.Visible = false;
        _icon.Dispose();
        _on.Dispose();
        _off.Dispose();
    }

    // 丸に「V」の絵（32 × 32）
    private static Icon MakeIcon(Color color)
    {
        using var bitmap = new Bitmap(32, 32);
        using (Graphics g = Graphics.FromImage(bitmap))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Color.Transparent);
            using var fill = new SolidBrush(color);
            g.FillEllipse(fill, 1, 1, 30, 30);
            using var pen = new Pen(Color.White, 4f) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round };
            g.DrawLines(pen, [new PointF(9, 9), new PointF(16, 23), new PointF(23, 9)]);
        }
        IntPtr handle = bitmap.GetHicon();
        using Icon temporary = Icon.FromHandle(handle);
        var icon = (Icon)temporary.Clone(); // 元の手綱は放す（放さないと、アイコンの分が残り続ける）
        TrayNativeMethods.DestroyIcon(handle);
        return icon;
    }
}

internal static partial class TrayNativeMethods
{
    [System.Runtime.InteropServices.LibraryImport("user32.dll")]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    internal static partial bool DestroyIcon(IntPtr handle);
}
