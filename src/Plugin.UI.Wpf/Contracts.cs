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
    /// <summary>左（アプリの「シーン」の側）。</summary>
    Left,

    /// <summary>右（アプリの「プロパティ」の側）。</summary>
    Right,

    /// <summary>下（アプリの「タイムライン」の側）。</summary>
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

    /// <summary>ツールバーのボタン（<see cref="IToolbarRegistry"/>。3D Theater は SDK 1.2 から）。</summary>
    public const string Toolbar = "ui.toolbar";

    /// <summary>右クリックのメニューの項目（<see cref="IContextMenuRegistry"/>。3D Theater は SDK 1.2 から）。</summary>
    public const string ContextMenus = "ui.contextmenu";

    /// <summary>状態表示の欄（<see cref="IStatusBarRegistry"/>。3D Theater は SDK 1.2 から）。</summary>
    public const string StatusBar = "ui.statusbar";
}

/// <summary>
/// ツールバーにボタンを足す（<c>context.GetService&lt;IToolbarRegistry&gt;()</c>）。ボタンは、プラグインが登録したコマンドを実行する。
/// アプリのツールバーの、アプリのボタンの後ろに並ぶ。コマンドが今実行できないときは押せない。プラグインを無効にすると外れる。
/// </summary>
public interface IToolbarRegistry
{
    /// <summary>足す（コマンドは先に登録しておく）。返したものを Dispose すると、そのボタンだけ外す。コマンドを外すとボタンも外れる。</summary>
    IDisposable Register(PluginToolbarButton button);
}

/// <summary>ツールバーのボタン。</summary>
public sealed class PluginToolbarButton
{
    /// <summary>押したときに実行する、このプラグインのコマンドの Id。</summary>
    public required string CommandId { get; init; }

    /// <summary>
    /// 絵の文字（Segoe Fluent Icons の 1 文字。例: "\uE890"）。無ければ <see cref="Text"/> を出す。
    /// 説明（ツールチップ）には、コマンドの名前・ショートカット・説明を出す。
    /// </summary>
    public string? Glyph { get; init; }

    /// <summary>ボタンの文字（<see cref="Glyph"/> も無ければ、コマンドの名前）。</summary>
    public string? Text { get; init; }
}

/// <summary>
/// アプリの右クリックのメニューに項目を足す（<c>context.GetService&lt;IContextMenuRegistry&gt;()</c>）。項目は、プラグインが登録した
/// コマンドを実行する（今実行できないときは使えない）。どのメニューに足せるかはアプリが決める（3D Theater は SDK の TheaterMenuPlaces）。
/// アプリの項目の後ろに、区切り線をはさんで並ぶ。プラグインを無効にすると外れる。
/// </summary>
public interface IContextMenuRegistry
{
    /// <summary>足す（コマンドは先に登録しておく。知らないメニューの名前は誤り）。返したものを Dispose すると外す。</summary>
    IDisposable Register(PluginContextMenuItem item);
}

/// <summary>右クリックのメニューの項目。</summary>
public sealed class PluginContextMenuItem
{
    /// <summary>選んだときに実行する、このプラグインのコマンドの Id（メニューにはコマンドの名前を出す）。</summary>
    public required string CommandId { get; init; }

    /// <summary>足すメニューの名前（アプリが決める。3D Theater は TheaterMenuPlaces）。</summary>
    public required string Place { get; init; }
}

/// <summary>
/// 状態表示（ウィンドウのいちばん下の行）に文字の欄を足す（<c>context.GetService&lt;IStatusBarRegistry&gt;()</c>）。
/// アプリの欄の左に並ぶ。プラグインを無効にすると外れる。
/// </summary>
public interface IStatusBarRegistry
{
    /// <summary>欄を足す（<paramref name="id"/> はプラグインの中で一意）。返したものの文字を変えると、表示が変わる。Dispose で外す。</summary>
    IStatusBarItem Add(string id);
}

/// <summary>状態表示の欄 1 つ（画面のスレッドから変える）。</summary>
public interface IStatusBarItem : IDisposable
{
    /// <summary>出す文字（長いときは途中で切る）。空なら欄を隠す。</summary>
    string Text { get; set; }

    /// <summary>マウスを乗せたときの説明（無ければ出さない）。</summary>
    string? ToolTip { get; set; }
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
