using System.Windows;
using Theater3D.Editor;
using Theater3D.Engine.Interop;

namespace Theater3D.App.Shell;

/// <summary>
/// 描画品質の設定（レンダリングのメニュー）。プレビューの品質を「軽い・ふつう・きれい」から選ぶ。選ぶとすぐにプレビューへ反映し、
/// 利用者ごとの設定として覚える（終了時に保存。プロジェクトには入れない）。書き出しの品質は決まっているので、説明だけを出す。
/// </summary>
public partial class RenderQualityWindow : Window
{
    private readonly EditorContext _context;
    private readonly bool _initialized;

    internal RenderQualityWindow(EditorContext context)
    {
        InitializeComponent();
        _context = context;
        (context.PreviewQuality.Quality switch
        {
            PreviewQuality.Light => LightRadio,
            PreviewQuality.High => HighRadio,
            _ => StandardRadio,
        }).IsChecked = true;
        _initialized = true;
        NoteText.Text = "選ぶとすぐにプレビューへ反映します。設定は利用者ごとに覚えます（プロジェクトには入れません）。" + SupportNote(context);
    }

    /// <summary>この GPU で使えるなめらかさが 8 倍より少ないときの説明（小さな描画先を作って確かめる）。</summary>
    private static string SupportNote(EditorContext context)
    {
        if (context.Engine is not { } engine)
        {
            return string.Empty;
        }
        try
        {
            using NativeView probe = engine.CreateOffscreenView(16, 16);
            int most = probe.SetSampleCount(8);
            return most >= 8 ? string.Empty
                : most <= 1 ? "\nこの GPU ではなめらかさ（MSAA）を使えないため、どの品質でもなめらかにせずに描きます。"
                : $"\nこの GPU で使えるなめらかさは {most} 倍までです（それより多い品質では {most} 倍で描きます）。";
        }
        catch (NativeEngineException)
        {
            return string.Empty; // GPU を作り直している間など。説明を省くだけ
        }
    }

    private void OnQualityChanged(object sender, RoutedEventArgs e)
    {
        if (!_initialized)
        {
            return;
        }
        _context.PreviewQuality.Quality = HighRadio.IsChecked == true ? PreviewQuality.High
            : LightRadio.IsChecked == true ? PreviewQuality.Light
            : PreviewQuality.Standard;
    }

    private void OnCloseClick(object sender, RoutedEventArgs e) => Close();
}
