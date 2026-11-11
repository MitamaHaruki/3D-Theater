using System.Windows;

namespace Plugin.UI.Wpf;

/// <summary>
/// ドッキングパネルの登録（<c>context.GetService&lt;IPanelRegistry&gt;()</c>）。登録したパネルは、アプリのパネルと同じ決まりで
/// 表示・タブ化・切り離し・レイアウトの保存と復元ができる（指示書 7.2）。表示のメニュー「パネル」からも出し入れできる。
/// プラグインを無効にすると外れる。
/// </summary>
public interface IPanelRegistry
{
    /// <summary>登録する（同じ Id は登録できない）。返したものを Dispose すると、そのパネルだけ外す。</summary>
    IDisposable Register(PluginPanel panel);
}

/// <summary>プラグインのドッキングパネル。</summary>
public sealed class PluginPanel
{
    /// <summary>プラグインの中で一意の名前（英数字・. _ -）。レイアウトの保存に使う。</summary>
    public required string Id { get; init; }

    /// <summary>タブに出す名前。</summary>
    public required string Title { get; init; }

    /// <summary>
    /// 中身を作る（初めて出すときに 1 度だけ呼ぶ。画面のスレッド）。例外を投げると、代わりにエラーの説明を出す。
    /// </summary>
    public required Func<FrameworkElement> CreateContent { get; init; }

    /// <summary>初めて出すときの場所（前回のレイアウトがあれば、そちらに従う）。</summary>
    public PanelPlacement Placement { get; init; } = PanelPlacement.Right;
}

/// <summary>パネルを初めて出す場所。</summary>
public enum PanelPlacement
{
    Left,
    Right,
    Bottom,
}

/// <summary>アプリの画面に合わせた知らせ（<c>context.GetService&lt;IPluginUi&gt;()</c>）。</summary>
public interface IPluginUi
{
    /// <summary>プラグインが自分の窓を出すときの持ち主（アプリのメインの窓）。</summary>
    Window? Owner { get; }

    /// <summary>メッセージを出す（アプリの窓の上に。題名を省けばプラグインの名前）。</summary>
    void ShowMessage(string message, string? title = null);
}

/// <summary>画面の機能の名前（plugin.json の requires に書く）。</summary>
public static class UiCapabilities
{
    /// <summary>ドッキングパネル（<see cref="IPanelRegistry"/>）。</summary>
    public const string Panels = "panels";

    /// <summary>メッセージ・窓（<see cref="IPluginUi"/>）。</summary>
    public const string Dialogs = "ui.dialogs";
}

/// <summary>
/// アプリの配色の資源の名前（<c>element.SetResourceReference(TextBlock.ForegroundProperty, ThemeKeys.Text)</c> のように使う）。
/// プラグインのパネルを、アプリの暗い配色に合わせるため。資源の無いアプリでは、WPF の初期の色になる。
/// </summary>
public static class ThemeKeys
{
    /// <summary>文字の色（Brush）。</summary>
    public const string Text = "TextBrush";

    /// <summary>控えめな文字の色（Brush）。</summary>
    public const string SubText = "SubTextBrush";

    /// <summary>パネルの地の色（Brush）。</summary>
    public const string Panel = "PanelBrush";

    /// <summary>枠の線の色（Brush）。</summary>
    public const string Border = "BorderBrush";

    /// <summary>強調の色（Brush）。</summary>
    public const string Accent = "AccentBrush";
}
