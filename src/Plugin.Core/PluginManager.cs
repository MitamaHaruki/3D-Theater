using System.Reflection;

namespace Plugin.Core;

/// <summary>プラグインの状態。</summary>
public enum PluginStatus
{
    /// <summary>無効（利用者が有効にしていない・無効にした）。</summary>
    Disabled,

    /// <summary>動いている。</summary>
    Running,

    /// <summary>有効だが、安全モードで起動したため始めていない。</summary>
    NotStarted,

    /// <summary>読み込み・開始に失敗した（理由は <see cref="PluginEntry.StatusMessage"/>）。</summary>
    Failed,

    /// <summary>このアプリでは使えない（対象のアプリ・SDK の版・必要な機能が合わない）。</summary>
    Incompatible,

    /// <summary>plugin.json が壊れている。</summary>
    Invalid,

    /// <summary>同じ id のプラグインが先に見つかっている。</summary>
    Duplicate,
}

/// <summary>プラグインで起きた問題の記録（画面に出す）。</summary>
public sealed record PluginProblem(DateTime Time, string Message);

/// <summary>見つけたプラグイン 1 つ。</summary>
public sealed class PluginEntry
{
    private const int MaxProblems = 50;
    private readonly List<PluginProblem> _problems = [];

    internal PluginEntry(string folder) => Folder = folder;

    /// <summary>プラグインのフォルダー（plugin.json のある所）。</summary>
    public string Folder { get; }

    /// <summary>plugin.json の中身（壊れていれば null）。</summary>
    public PluginManifest? Manifest { get; internal set; }

    /// <summary>plugin.json の id（読めなければフォルダーの名前）。</summary>
    public string Id => Manifest?.Id ?? Path.GetFileName(Folder);

    /// <summary>画面に出す名前（読めなければフォルダーの名前）。</summary>
    public string Name => Manifest?.Name ?? Path.GetFileName(Folder);

    /// <summary>今の状態。</summary>
    public PluginStatus Status { get; internal set; }

    /// <summary>状態の理由（使えない・失敗したときなど）。</summary>
    public string? StatusMessage { get; internal set; }

    /// <summary>利用者が有効にしているか（次の起動でも始める）。</summary>
    public bool IsEnabled { get; internal set; }

    /// <summary>DLL を読み込んだか（無効にしても、アプリを再起動するまで読み込まれたまま）。</summary>
    public bool IsAssemblyLoaded => LoadContext is not null;

    /// <summary>起きた問題（古い順。多すぎれば古いものから消す）。</summary>
    public IReadOnlyList<PluginProblem> Problems => _problems;

    internal IPlugin? Instance { get; set; }

    internal IPluginScope? Scope { get; set; }

    internal PluginLoadContext? LoadContext { get; set; }

    internal Type? EntryType { get; set; }

    internal void AddProblem(string message)
    {
        _problems.Add(new PluginProblem(DateTime.Now, message));
        if (_problems.Count > MaxProblems)
        {
            _problems.RemoveAt(0);
        }
    }
}

/// <summary>アプリが出す、プラグインごとの機能（コマンド・パネルなど）。Dispose で、そのプラグインが登録したものを全部外す。</summary>
public interface IPluginScope : IDisposable
{
    /// <summary>機能を取り出す（無ければ null）。</summary>
    object? GetService(Type serviceType);
}

/// <summary>プラグインの状態と設定の保存先（アプリの利用者の設定）。</summary>
public interface IPluginStateStore
{
    /// <summary>有効にしているか（初めて見つけたものは false）。</summary>
    bool IsEnabled(string id);

    /// <summary>有効・無効を覚える。</summary>
    void SetEnabled(string id, bool enabled);

    /// <summary>プラグインの設定の値（無ければ null）。</summary>
    string? GetSetting(string id, string key);

    /// <summary>プラグインの設定の値を覚える（null なら消す）。</summary>
    void SetSetting(string id, string key, string? value);

    /// <summary>開始の途中でアプリが終わったときに残る印（無ければ null）。中身はプラグインの id。</summary>
    string? ReadStartingMarker();

    /// <summary>プラグインを始める前に書く（すぐにファイルへ書くこと。始め終わったら消す）。</summary>
    void WriteStartingMarker(string id);

    /// <summary>開始の途中の印を消す（始め終わったとき・印を読んだとき）。</summary>
    void ClearStartingMarker();
}

/// <summary>ログの書き先。</summary>
public interface IPluginLogSink
{
    /// <summary>知らせ。</summary>
    void Info(string message);

    /// <summary>気をつけること。</summary>
    void Warning(string message, Exception? exception = null);

    /// <summary>エラー。</summary>
    void Error(string message, Exception? exception = null);
}

/// <summary>
/// プラグインを見つけ、互換性を確かめ、有効なものを読み込んで始め、無効にしたら止める（指示書 7.3）。
/// - プラグインのコードで起きた例外は、ここ（とアプリの機能の側）で受け止め、そのプラグインだけを「失敗」にする。アプリは止めない
/// - 開始の途中でアプリごと落ちたとき（例外で受け止められないとき）に備え、始める前に印を書き、次の起動でそのプラグインを無効にする
/// - 安全モードでは、有効なプラグインも始めない
/// 画面のスレッドから使う。
/// </summary>
public sealed class PluginManager
{
    private readonly PluginHostInfo _host;
    private readonly IPluginStateStore _store;
    private readonly Func<PluginEntry, IPluginScope> _createScope;
    private readonly IPluginLogSink _log;
    private readonly IReadOnlyCollection<string> _sharedAssemblies;
    private readonly List<PluginEntry> _plugins = [];

    /// <summary>管理を作る（プラグインを見つけるのは <see cref="Discover"/>）。</summary>
    /// <param name="host">アプリの名前・SDK の版・出している機能（互換性の確かめに使う）。</param>
    /// <param name="store">有効・無効とプラグインの設定の保存先。</param>
    /// <param name="createScope">プラグインごとの、アプリの機能の入口を作る（無効にするときに Dispose する）。</param>
    /// <param name="log">ログの書き先。</param>
    /// <param name="sharedAssemblies">アプリとプラグインで共通にする契約の DLL の名前（Plugin.Core のほか、UI の契約・アプリ専用 SDK）。</param>
    public PluginManager(PluginHostInfo host, IPluginStateStore store, Func<PluginEntry, IPluginScope> createScope, IPluginLogSink log,
        IEnumerable<string> sharedAssemblies)
    {
        _host = host;
        _store = store;
        _createScope = createScope;
        _log = log;
        _sharedAssemblies = [typeof(IPlugin).Assembly.GetName().Name!, .. sharedAssemblies];
    }

    /// <summary>見つけたプラグイン（見つけた順）。</summary>
    public IReadOnlyList<PluginEntry> Plugins => _plugins;

    /// <summary>アプリの名前・SDK の版・出している機能。</summary>
    public PluginHostInfo Host => _host;

    /// <summary>安全モード（有効なプラグインも始めない）。</summary>
    public bool IsSafeMode { get; private set; }

    /// <summary>前回、開始の途中でアプリが終わったために無効にしたプラグイン（無ければ null）。</summary>
    public PluginEntry? DisabledAfterCrash { get; private set; }

    /// <summary>状態が変わった。</summary>
    public event EventHandler? Changed;

    /// <summary>
    /// フォルダーの中の、plugin.json を持つフォルダーをプラグインとして見つける（前に並べたフォルダーが優先。同じ id は後のものを使わない）。
    /// 前回の開始の途中で落ちた印があれば、そのプラグインを無効にする。
    /// </summary>
    public void Discover(IEnumerable<string> roots)
    {
        _plugins.Clear();
        foreach (string root in roots.Where(Directory.Exists))
        {
            foreach (string folder in Directory.GetDirectories(root).Order(StringComparer.OrdinalIgnoreCase))
            {
                string manifestPath = Path.Combine(folder, PluginManifest.FileName);
                if (File.Exists(manifestPath))
                {
                    _plugins.Add(Examine(folder, manifestPath));
                }
            }
        }

        string? crashed = _store.ReadStartingMarker();
        if (crashed is not null)
        {
            _store.ClearStartingMarker();
            if (_plugins.FirstOrDefault(p => p.Manifest?.Id == crashed) is { } entry)
            {
                _store.SetEnabled(entry.Id, false);
                entry.IsEnabled = false;
                entry.AddProblem("前回、このプラグインを始める途中でアプリが終了しました。安全のため無効にしました");
                DisabledAfterCrash = entry;
                _log.Warning($"前回、プラグイン「{entry.Name}」を始める途中でアプリが終了したため、無効にしました");
            }
        }
        _log.Info($"プラグインを {_plugins.Count} 個見つけました（使えるもの {_plugins.Count(p => p.Status == PluginStatus.Disabled)} 個）");
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>有効なプラグインを始める（安全モードなら始めない）。</summary>
    public void StartEnabled(bool safeMode)
    {
        IsSafeMode = safeMode;
        foreach (PluginEntry entry in _plugins.Where(p => p.IsEnabled && p.Status == PluginStatus.Disabled))
        {
            if (safeMode)
            {
                entry.Status = PluginStatus.NotStarted;
                entry.StatusMessage = "安全モードで起動したためです。アプリを普通に起動し直すと始めます";
            }
            else
            {
                Start(entry);
            }
        }
        if (safeMode)
        {
            _log.Info("安全モードで起動したため、プラグインを始めていません");
        }
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>有効にして始める（安全モードなら、次の通常の起動から始める）。使えないプラグインなら false。</summary>
    public bool Enable(PluginEntry entry)
    {
        if (entry.Status is PluginStatus.Incompatible or PluginStatus.Invalid or PluginStatus.Duplicate)
        {
            return false;
        }
        _store.SetEnabled(entry.Id, true);
        entry.IsEnabled = true;
        bool ok = true;
        if (entry.Status != PluginStatus.Running)
        {
            if (IsSafeMode)
            {
                entry.Status = PluginStatus.NotStarted;
                entry.StatusMessage = "安全モードで起動しているためです。次に普通に起動したときに始めます";
            }
            else
            {
                ok = Start(entry);
            }
        }
        Changed?.Invoke(this, EventArgs.Empty);
        return ok;
    }

    /// <summary>無効にして止める（登録したコマンド・パネルを外す。DLL は再起動まで残る）。</summary>
    public void Disable(PluginEntry entry)
    {
        _store.SetEnabled(entry.Id, false);
        entry.IsEnabled = false;
        Stop(entry);
        if (entry.Status is PluginStatus.Running or PluginStatus.NotStarted or PluginStatus.Failed)
        {
            entry.Status = PluginStatus.Disabled;
            entry.StatusMessage = null;
        }
        _log.Info($"プラグイン「{entry.Name}」を無効にしました");
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>アプリの終了時: 動いているプラグインを止める（有効のままにしておく）。</summary>
    public void ShutdownAll()
    {
        foreach (PluginEntry entry in _plugins.Where(p => p.Instance is not null))
        {
            Stop(entry);
        }
    }

    /// <summary>
    /// プラグインのコード（コマンド・パネル・イベント）で起きた例外を記録する（アプリの機能の側が受け止めたもの）。
    /// プラグインは止めない（何度も起きるなら、利用者が管理画面で無効にできる）。
    /// </summary>
    public void ReportProblem(PluginEntry entry, string message, Exception? exception = null)
    {
        entry.AddProblem(exception is null ? message : $"{message}: {exception.Message}");
        _log.Error($"プラグイン「{entry.Name}」: {message}", exception);
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private PluginEntry Examine(string folder, string manifestPath)
    {
        var entry = new PluginEntry(folder);
        try
        {
            entry.Manifest = PluginManifest.Load(manifestPath);
        }
        catch (PluginManifestException ex)
        {
            entry.Status = PluginStatus.Invalid;
            entry.StatusMessage = ex.Message;
            _log.Warning($"プラグインのフォルダー「{folder}」: {ex.Message}");
            return entry;
        }
        PluginManifest manifest = entry.Manifest;
        if (_plugins.FirstOrDefault(p => p.Manifest?.Id == manifest.Id) is { } first)
        {
            entry.Status = PluginStatus.Duplicate;
            entry.StatusMessage = $"同じ id（{manifest.Id}）のプラグインが「{first.Folder}」にあるため、こちらは使いません";
            return entry;
        }
        string? problem = _host.CheckCompatibility(manifest);
        entry.Status = problem is null ? PluginStatus.Disabled : PluginStatus.Incompatible;
        entry.StatusMessage = problem;
        entry.IsEnabled = problem is null && _store.IsEnabled(manifest.Id);
        return entry;
    }

    private bool Start(PluginEntry entry)
    {
        PluginManifest manifest = entry.Manifest!;
        IPlugin instance;
        try
        {
            if (entry.EntryType is null)
            {
                entry.EntryType = LoadEntryType(entry, manifest);
            }
            instance = (IPlugin)Activator.CreateInstance(entry.EntryType)!;
        }
        catch (PluginLoadException ex)
        {
            return Fail(entry, ex.Message, null);
        }
        catch (Exception ex) when (ex is FileLoadException or BadImageFormatException or FileNotFoundException or TypeLoadException
            or MissingMethodException or TargetInvocationException or ReflectionTypeLoadException or MemberAccessException)
        {
            return Fail(entry, "DLL を読み込めませんでした: " + (ex.InnerException ?? ex).Message, ex);
        }

        IPluginScope scope = _createScope(entry);
        var context = new PluginContext(entry, scope, _store, _log);
        _store.WriteStartingMarker(manifest.Id);
        try
        {
            instance.Initialize(context);
        }
        catch (Exception ex)
        {
            DisposeScope(entry, scope);
            return Fail(entry, "始めるときにエラーが起きました: " + ex.Message, ex);
        }
        finally
        {
            _store.ClearStartingMarker();
        }
        entry.Instance = instance;
        entry.Scope = scope;
        entry.Status = PluginStatus.Running;
        entry.StatusMessage = null;
        _log.Info($"プラグイン「{entry.Name}」{manifest.Version} を始めました");
        return true;
    }

    private Type LoadEntryType(PluginEntry entry, PluginManifest manifest)
    {
        string path = Path.GetFullPath(Path.Combine(entry.Folder, manifest.Assembly));
        if (!File.Exists(path))
        {
            throw new PluginLoadException($"DLL「{manifest.Assembly}」が見つかりません");
        }
        var context = new PluginLoadContext(path, _sharedAssemblies);
        Assembly assembly = context.LoadFromAssemblyPath(path);
        entry.LoadContext = context;
        Type? type = assembly.GetType(manifest.EntryType, throwOnError: false);
        if (type is null)
        {
            throw new PluginLoadException($"入口の型「{manifest.EntryType}」が DLL にありません");
        }
        if (!typeof(IPlugin).IsAssignableFrom(type))
        {
            bool sameNameOnly = type.GetInterfaces().Any(i => i.FullName == typeof(IPlugin).FullName);
            throw new PluginLoadException(sameNameOnly
                ? "共通の契約（Plugin.Core）が別に読み込まれています（プラグインに入っている写しと、アプリのものが違う可能性があります）"
                : $"入口の型「{manifest.EntryType}」が IPlugin を実装していません");
        }
        if (type.IsAbstract || type.GetConstructor(Type.EmptyTypes) is null)
        {
            throw new PluginLoadException($"入口の型「{manifest.EntryType}」は、引数なしで作れる型にしてください");
        }
        return type;
    }

    private void Stop(PluginEntry entry)
    {
        if (entry.Instance is { } instance)
        {
            try
            {
                instance.Shutdown();
            }
            catch (Exception ex)
            {
                entry.AddProblem("止めるときにエラーが起きました: " + ex.Message);
                _log.Error($"プラグイン「{entry.Name}」を止めるときにエラーが起きました", ex);
            }
        }
        if (entry.Scope is { } scope)
        {
            DisposeScope(entry, scope);
        }
        entry.Instance = null;
        entry.Scope = null;
    }

    private void DisposeScope(PluginEntry entry, IPluginScope scope)
    {
        try
        {
            scope.Dispose();
        }
        catch (Exception ex)
        {
            _log.Error($"プラグイン「{entry.Name}」の登録を外すときにエラーが起きました", ex);
        }
    }

    private bool Fail(PluginEntry entry, string message, Exception? exception)
    {
        entry.Status = PluginStatus.Failed;
        entry.StatusMessage = message;
        entry.AddProblem(message);
        _log.Error($"プラグイン「{entry.Name}」: {message}", exception);
        return false;
    }

    private sealed class PluginLoadException(string message) : Exception(message);

    /// <summary>プラグインに渡す入口（プラグインごと）。</summary>
    private sealed class PluginContext(PluginEntry entry, IPluginScope scope, IPluginStateStore store, IPluginLogSink log) : IPluginContext
    {
        public PluginManifest Manifest => entry.Manifest!;

        public string Folder => entry.Folder;

        public IPluginLog Log { get; } = new PrefixedLog(entry.Name, log);

        public IPluginSettings Settings { get; } = new StoreSettings(entry.Id, store);

        public ICommandRegistry Commands => scope.GetService(typeof(ICommandRegistry)) as ICommandRegistry
            ?? throw new NotSupportedException("このアプリはコマンドの登録に対応していません");

        public T? GetService<T>() where T : class => scope.GetService(typeof(T)) as T;
    }

    private sealed class PrefixedLog(string name, IPluginLogSink log) : IPluginLog
    {
        public void Info(string message) => log.Info($"[{name}] {message}");

        public void Warning(string message, Exception? exception = null) => log.Warning($"[{name}] {message}", exception);

        public void Error(string message, Exception? exception = null) => log.Error($"[{name}] {message}", exception);
    }

    private sealed class StoreSettings(string id, IPluginStateStore store) : IPluginSettings
    {
        public string? Get(string key) => store.GetSetting(id, key);

        public void Set(string key, string? value) => store.SetSetting(id, key, value);
    }
}
