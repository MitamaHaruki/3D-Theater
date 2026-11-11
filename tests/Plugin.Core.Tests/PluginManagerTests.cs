using System.Runtime.Loader;

namespace Plugin.Core.Tests;

/// <summary>
/// プラグインの見つけ方・読み込み・開始・停止と、失敗の切り分け（指示書 Phase 5 の「無効化・異常時にも本体が適切に対処する」）。
/// テスト用のプラグイン（Plugin.TestPlugins.dll）を、毎回別のフォルダーに写して、本物と同じ手順で読み込む。
/// </summary>
[TestClass]
public sealed class PluginManagerTests
{
    private static readonly PluginHostInfo Host = new("3DTheater", new Version(1, 0), [CoreCapabilities.Commands, CoreCapabilities.Settings]);
    private string _root = string.Empty;

    [TestInitialize]
    public void CreateRoot()
    {
        _root = Path.Combine(Path.GetTempPath(), "3dt_plugins_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    [TestCleanup]
    public void DeleteRoot()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 読み込んだ DLL は外せないので、消せなくてもよい（一時フォルダー）
        }
    }

    [TestMethod]
    public void EnabledPluginStartsWithSharedContracts()
    {
        AddPlugin("good", "test.good", "Plugin.TestPlugins.GoodPlugin");
        var (manager, store, scopes, _) = Create();
        store.Enabled.Add("test.good");

        manager.Discover([_root]);
        manager.StartEnabled(safeMode: false);

        PluginEntry entry = manager.Plugins.Single();
        Assert.AreEqual(PluginStatus.Running, entry.Status, entry.StatusMessage);
        Assert.IsNotNull(entry.Instance);
        Assert.IsInstanceOfType<IPlugin>(entry.Instance, "共通の契約（IPlugin）は、アプリとプラグインで同じ型");
        Assert.AreNotEqual(AssemblyLoadContext.Default, AssemblyLoadContext.GetLoadContext(entry.Instance.GetType().Assembly),
            "プラグインは自分の読み込み先に入る");
        Assert.AreEqual(AssemblyLoadContext.Default, AssemblyLoadContext.GetLoadContext(typeof(IPlugin).Assembly));
        Assert.AreEqual("hello", scopes[entry].Commands.Single().Id, "コマンドを登録できる");
        Assert.AreEqual("1", store.GetSetting("test.good", "started"), "設定を書ける");
        Assert.IsNull(store.Marker, "始め終わったら、開始の印を消す");
        CollectionAssert.Contains(store.MarkerHistory, "test.good", "始める前に、開始の印を書く");
    }

    [TestMethod]
    public void NewPluginsStayDisabledUntilEnabled()
    {
        AddPlugin("good", "test.good", "Plugin.TestPlugins.GoodPlugin");
        var (manager, store, scopes, _) = Create();
        manager.Discover([_root]);
        manager.StartEnabled(safeMode: false);
        PluginEntry entry = manager.Plugins.Single();
        Assert.AreEqual(PluginStatus.Disabled, entry.Status, "初めて見つけたプラグインは始めない");
        Assert.IsFalse(entry.IsAssemblyLoaded, "DLL も読まない");

        Assert.IsTrue(manager.Enable(entry));
        Assert.AreEqual(PluginStatus.Running, entry.Status);
        Assert.Contains("test.good", store.Enabled, "有効にしたことを覚える");
        Assert.HasCount(1, scopes[entry].Commands);
    }

    [TestMethod]
    public void FailingPluginsDoNotStopOthers()
    {
        AddPlugin("a-throwing", "test.throwing", "Plugin.TestPlugins.ThrowingPlugin");
        AddPlugin("b-needs-argument", "test.needs-argument", "Plugin.TestPlugins.NeedsArgumentPlugin");
        AddPlugin("c-not-a-plugin", "test.not-a-plugin", "Plugin.TestPlugins.NotAPlugin");
        AddPlugin("d-missing-type", "test.missing-type", "Plugin.TestPlugins.NoSuchType");
        AddPlugin("e-missing-dll", "test.missing-dll", "Plugin.TestPlugins.GoodPlugin", assembly: "NoSuch.dll");
        AddPlugin("f-good", "test.good", "Plugin.TestPlugins.GoodPlugin");
        var (manager, store, scopes, log) = Create();
        store.Enabled.UnionWith(["test.throwing", "test.needs-argument", "test.not-a-plugin", "test.missing-type", "test.missing-dll", "test.good"]);

        manager.Discover([_root]);
        manager.StartEnabled(safeMode: false);

        PluginEntry Get(string id) => manager.Plugins.Single(p => p.Id == id);
        AssertFailed(Get("test.throwing"), "わざと失敗しました");
        Assert.IsTrue(scopes[Get("test.throwing")].Disposed, "失敗したら、途中まで登録したものを外す");
        Assert.IsEmpty(scopes[Get("test.throwing")].Commands);
        AssertFailed(Get("test.needs-argument"), "引数なしで作れる型");
        AssertFailed(Get("test.not-a-plugin"), "IPlugin を実装していません");
        AssertFailed(Get("test.missing-type"), "DLL にありません");
        AssertFailed(Get("test.missing-dll"), "見つかりません");
        Assert.AreEqual(PluginStatus.Running, Get("test.good").Status, "ほかのプラグインは始まる");
        Assert.IsTrue(log.Lines.Any(l => l.Contains("わざと失敗しました", StringComparison.Ordinal)), "ログに残す");
        Assert.IsNull(store.Marker, "失敗しても開始の印は消す");
    }

    [TestMethod]
    public void DisablingStopsThePluginAndRemovesRegistrations()
    {
        AddPlugin("good", "test.good", "Plugin.TestPlugins.GoodPlugin");
        var (manager, store, scopes, _) = Create();
        store.Enabled.Add("test.good");
        manager.Discover([_root]);
        manager.StartEnabled(safeMode: false);
        PluginEntry entry = manager.Plugins.Single();
        FakeScope first = scopes[entry];
        int Shutdowns() => (int)entry.EntryType!.GetProperty("ShutdownCount")!.GetValue(null)!;

        manager.Disable(entry);
        Assert.AreEqual(PluginStatus.Disabled, entry.Status);
        Assert.AreEqual(1, Shutdowns(), "止めるときに Shutdown を呼ぶ");
        Assert.IsTrue(first.Disposed, "登録したものを外す");
        Assert.DoesNotContain("test.good", store.Enabled, "無効にしたことを覚える");
        Assert.IsTrue(entry.IsAssemblyLoaded, "DLL は再起動まで読み込まれたまま");

        Assert.IsTrue(manager.Enable(entry), "同じ起動の中で、もう一度有効にできる");
        Assert.AreEqual(PluginStatus.Running, entry.Status);
        Assert.AreNotSame(first, scopes[entry], "新しく始める");
        Assert.AreEqual("2", store.GetSetting("test.good", "started"));

        manager.ShutdownAll();
        Assert.AreEqual(2, Shutdowns(), "アプリの終了時にも止める");
        Assert.Contains("test.good", store.Enabled, "終了時は有効のまま");
    }

    [TestMethod]
    public void ExceptionsWhileStoppingAreRecorded()
    {
        AddPlugin("stop", "test.stop", "Plugin.TestPlugins.ShutdownThrowingPlugin");
        var (manager, store, _, _) = Create();
        store.Enabled.Add("test.stop");
        manager.Discover([_root]);
        manager.StartEnabled(safeMode: false);
        PluginEntry entry = manager.Plugins.Single();

        manager.Disable(entry);
        Assert.AreEqual(PluginStatus.Disabled, entry.Status);
        Assert.IsTrue(entry.Problems.Any(p => p.Message.Contains("止めるときに失敗しました", StringComparison.Ordinal)));

        manager.ReportProblem(entry, "コマンドでエラーが起きました", new InvalidOperationException("中身"));
        StringAssert.Contains(entry.Problems[^1].Message, "中身", "アプリの機能の側で受け止めた例外も記録する");
    }

    [TestMethod]
    public void SafeModeDoesNotStartPlugins()
    {
        AddPlugin("good", "test.good", "Plugin.TestPlugins.GoodPlugin");
        AddPlugin("other", "test.other", "Plugin.TestPlugins.GoodPlugin");
        var (manager, store, _, _) = Create();
        store.Enabled.Add("test.good");
        manager.Discover([_root]);
        manager.StartEnabled(safeMode: true);

        PluginEntry good = manager.Plugins.Single(p => p.Id == "test.good");
        Assert.AreEqual(PluginStatus.NotStarted, good.Status);
        Assert.IsFalse(good.IsAssemblyLoaded, "安全モードでは DLL も読まない");
        PluginEntry other = manager.Plugins.Single(p => p.Id == "test.other");
        Assert.IsTrue(manager.Enable(other));
        Assert.AreEqual(PluginStatus.NotStarted, other.Status, "安全モードの間に有効にしても、次の通常の起動から");
        Assert.Contains("test.other", store.Enabled);
    }

    [TestMethod]
    public void PluginThatCrashedWhileStartingIsDisabled()
    {
        AddPlugin("good", "test.good", "Plugin.TestPlugins.GoodPlugin");
        var (manager, store, _, _) = Create();
        store.Enabled.Add("test.good");
        store.Marker = "test.good"; // 前回、始める途中でアプリが落ちた

        manager.Discover([_root]);
        manager.StartEnabled(safeMode: false);

        PluginEntry entry = manager.Plugins.Single();
        Assert.AreSame(entry, manager.DisabledAfterCrash);
        Assert.AreEqual(PluginStatus.Disabled, entry.Status, "始めない");
        Assert.DoesNotContain("test.good", store.Enabled, "無効にしたことを覚える");
        Assert.IsNull(store.Marker, "印は消す");
        Assert.IsTrue(entry.Problems.Any(p => p.Message.Contains("始める途中", StringComparison.Ordinal)));
    }

    [TestMethod]
    public void BrokenIncompatibleAndDuplicatePluginsAreListedButNotStarted()
    {
        string broken = Path.Combine(_root, "a-broken");
        Directory.CreateDirectory(broken);
        File.WriteAllText(Path.Combine(broken, PluginManifest.FileName), "{ 壊れた");
        AddPlugin("b-other-app", "test.other-app", "Plugin.TestPlugins.GoodPlugin", host: "DrawingCanvas");
        AddPlugin("c-needs-more", "test.needs-more", "Plugin.TestPlugins.GoodPlugin", requires: "\"panels\"");
        AddPlugin("d-good", "test.good", "Plugin.TestPlugins.GoodPlugin");
        Directory.CreateDirectory(Path.Combine(_root, "e-not-a-plugin")); // plugin.json の無いフォルダーは数えない
        string second = Path.Combine(_root, "second");
        Directory.CreateDirectory(second);
        AddPlugin("good-copy", "test.good", "Plugin.TestPlugins.GoodPlugin", root: second);
        var (manager, store, _, _) = Create();
        store.Enabled.UnionWith(["test.other-app", "test.needs-more", "test.good"]);

        manager.Discover([_root, second]);
        manager.StartEnabled(safeMode: false);

        Assert.HasCount(5, manager.Plugins);
        Assert.AreEqual(PluginStatus.Invalid, manager.Plugins[0].Status);
        Assert.AreEqual(PluginStatus.Incompatible, manager.Plugins[1].Status);
        StringAssert.Contains(manager.Plugins[1].StatusMessage, "DrawingCanvas");
        Assert.AreEqual(PluginStatus.Incompatible, manager.Plugins[2].Status);
        StringAssert.Contains(manager.Plugins[2].StatusMessage, "panels");
        Assert.AreEqual(PluginStatus.Running, manager.Plugins[3].Status);
        Assert.AreEqual(PluginStatus.Duplicate, manager.Plugins[4].Status, "前に並べたフォルダーのものを使う");
        Assert.IsFalse(manager.Enable(manager.Plugins[1]), "使えないプラグインは有効にできない");
        Assert.IsFalse(manager.Plugins[1].IsAssemblyLoaded);
    }

    private static void AssertFailed(PluginEntry entry, string expected)
    {
        Assert.AreEqual(PluginStatus.Failed, entry.Status, entry.Id);
        StringAssert.Contains(entry.StatusMessage, expected, entry.Id);
        Assert.IsNull(entry.Instance);
    }

    private void AddPlugin(string folder, string id, string entryType, string assembly = "Plugin.TestPlugins.dll", string host = "3DTheater",
        string requires = "\"commands\", \"settings\"", string? root = null)
    {
        string directory = Path.Combine(root ?? _root, folder);
        Directory.CreateDirectory(directory);
        File.Copy(Path.Combine(AppContext.BaseDirectory, "TestPlugins", "Plugin.TestPlugins.dll"), Path.Combine(directory, "Plugin.TestPlugins.dll"));
        File.WriteAllText(Path.Combine(directory, PluginManifest.FileName), $$"""
            {
              "id": "{{id}}",
              "name": "テスト {{id}}",
              "version": "1.0.0",
              "host": "{{host}}",
              "sdkVersion": "1.0",
              "assembly": "{{assembly}}",
              "entryType": "{{entryType}}",
              "requires": [{{requires}}]
            }
            """);
    }

    private static (PluginManager Manager, MemoryStore Store, Dictionary<PluginEntry, FakeScope> Scopes, ListLog Log) Create()
    {
        var store = new MemoryStore();
        var scopes = new Dictionary<PluginEntry, FakeScope>();
        var log = new ListLog();
        var manager = new PluginManager(Host, store, entry => scopes[entry] = new FakeScope(), log, []);
        return (manager, store, scopes, log);
    }

    private sealed class MemoryStore : IPluginStateStore
    {
        private readonly Dictionary<(string, string), string> _settings = [];

        public HashSet<string> Enabled { get; } = [];

        public string? Marker { get; set; }

        public List<string> MarkerHistory { get; } = [];

        public bool IsEnabled(string id) => Enabled.Contains(id);

        public void SetEnabled(string id, bool enabled)
        {
            if (enabled)
            {
                Enabled.Add(id);
            }
            else
            {
                Enabled.Remove(id);
            }
        }

        public string? GetSetting(string id, string key) => _settings.GetValueOrDefault((id, key));

        public void SetSetting(string id, string key, string? value)
        {
            if (value is null)
            {
                _settings.Remove((id, key));
            }
            else
            {
                _settings[(id, key)] = value;
            }
        }

        public string? ReadStartingMarker() => Marker;

        public void WriteStartingMarker(string id)
        {
            Marker = id;
            MarkerHistory.Add(id);
        }

        public void ClearStartingMarker() => Marker = null;
    }

    private sealed class FakeScope : IPluginScope, ICommandRegistry
    {
        public List<PluginCommand> Commands { get; } = [];

        public bool Disposed { get; private set; }

        public object? GetService(Type serviceType) => serviceType == typeof(ICommandRegistry) ? this : null;

        public IDisposable Register(PluginCommand command)
        {
            Commands.Add(command);
            return new Removal(() => Commands.Remove(command));
        }

        public void Dispose()
        {
            Disposed = true;
            Commands.Clear();
        }

        private sealed class Removal(Action action) : IDisposable
        {
            public void Dispose() => action();
        }
    }

    private sealed class ListLog : IPluginLogSink
    {
        public List<string> Lines { get; } = [];

        public void Info(string message) => Lines.Add(message);

        public void Warning(string message, Exception? exception = null) => Lines.Add(message + exception?.Message);

        public void Error(string message, Exception? exception = null) => Lines.Add(message + exception?.Message);
    }
}
