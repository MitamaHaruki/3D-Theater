namespace Plugin.Core;

/// <summary>
/// プラグインの入口。plugin.json の entryType に書いた型がこれを実装し、引数なしで作れること。
/// どちらもアプリの画面のスレッドから呼ぶ。例外を投げてもアプリは止まらない（そのプラグインだけ「異常」になる）。
/// </summary>
public interface IPlugin
{
    /// <summary>有効にしたとき（起動時と、管理画面で有効にしたとき）。コマンドやパネルはここで登録する。</summary>
    void Initialize(IPluginContext context);

    /// <summary>
    /// 無効にしたときとアプリの終了時。登録したコマンド・パネル・イベントの受け取りは、この後にアプリが外す。
    /// DLL は、アプリを再起動するまで読み込まれたまま（指示書 7.3）。
    /// </summary>
    void Shutdown();
}

/// <summary>プラグインに渡す、アプリの機能の入口（プラグインごとに 1 つ）。</summary>
public interface IPluginContext
{
    /// <summary>plugin.json の中身。</summary>
    PluginManifest Manifest { get; }

    /// <summary>プラグインのフォルダー（plugin.json のある所）。</summary>
    string Folder { get; }

    /// <summary>アプリのログへ書く（プラグインの名前を付けて書く）。</summary>
    IPluginLog Log { get; }

    /// <summary>プラグインごとの設定（文字列の値。アプリの利用者の設定と一緒に保存する）。</summary>
    IPluginSettings Settings { get; }

    /// <summary>コマンド（メニュー・ショートカット）の登録。</summary>
    ICommandRegistry Commands { get; }

    /// <summary>
    /// アプリが出す機能（WPF の画面・アプリ専用 SDK など）を取り出す。そのアプリに無い機能なら null。
    /// 使う機能は plugin.json の requires に書いておくと、無いアプリでは読み込む前に「互換性なし」にできる。
    /// </summary>
    T? GetService<T>() where T : class;
}

/// <summary>アプリのログへ書く。</summary>
public interface IPluginLog
{
    /// <summary>知らせ（始めた・読み込んだなど）。</summary>
    void Info(string message);

    /// <summary>気をつけること（続けられるが、思ったとおりでないことがある）。</summary>
    void Warning(string message, Exception? exception = null);

    /// <summary>エラー（できなかったこと）。</summary>
    void Error(string message, Exception? exception = null);
}

/// <summary>プラグインごとの設定（キーと文字列の値）。</summary>
public interface IPluginSettings
{
    /// <summary>値（無ければ null）。</summary>
    string? Get(string key);

    /// <summary>値を入れる（null なら消す）。</summary>
    void Set(string key, string? value);
}

/// <summary>コマンドを登録する。登録したものは、プラグインを無効にすると外れる。</summary>
public interface ICommandRegistry
{
    /// <summary>登録する。返したものを Dispose すると、そのコマンドだけ外す。同じ Id は登録できない。</summary>
    IDisposable Register(PluginCommand command);
}

/// <summary>
/// メニューから実行できるコマンド。アプリはプラグインのメニュー（「プラグイン」の下の、プラグインの名前の項目）に並べる。
/// </summary>
public sealed class PluginCommand
{
    /// <summary>プラグインの中で一意の名前（英数字・. _ -）。</summary>
    public required string Id { get; init; }

    /// <summary>メニューに出す名前。</summary>
    public required string Title { get; init; }

    /// <summary>実行する処理（画面のスレッドで呼ぶ）。</summary>
    public required Action Execute { get; init; }

    /// <summary>今実行できるか（無ければいつでも実行できる）。</summary>
    public Func<bool>? CanExecute { get; init; }

    /// <summary>ショートカット（例: "Ctrl+Alt+I"）。アプリのほかの操作と重なるときは付けない。</summary>
    public string? Shortcut { get; init; }

    /// <summary>メニューに出す説明。</summary>
    public string? Description { get; init; }
}

/// <summary>共通の機能の名前（plugin.json の requires に書く）。アプリ専用の機能は、そのアプリの SDK が決める。</summary>
public static class CoreCapabilities
{
    /// <summary>コマンド（メニュー・ショートカット）。</summary>
    public const string Commands = "commands";

    /// <summary>プラグインごとの設定。</summary>
    public const string Settings = "settings";

    /// <summary>時間のかかる処理（進み具合と取り消し。<see cref="IPluginTasks"/>。3D Theater は SDK 1.3 から）。</summary>
    public const string Tasks = "tasks";

    /// <summary>ファイルの形式（読み込み・書き出しの追加。<see cref="IFileFormatRegistry"/>。3D Theater は SDK 1.3 から）。</summary>
    public const string Files = "files";
}
