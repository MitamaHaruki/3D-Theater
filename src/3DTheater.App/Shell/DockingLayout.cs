using System.IO;
using System.Windows;
using System.Windows.Controls;
using AvalonDock;
using AvalonDock.Layout;
using AvalonDock.Layout.Serialization;
using Theater3D.App.Logging;
using Theater3D.App.Panels;
using Theater3D.App.Preview;
using Theater3D.Editor;

namespace Theater3D.App.Shell;

/// <summary>
/// Visual Studio 型のドッキング画面（AvalonDock）の並べ方。初期配置の組み立て、保存と復元、プレビューの追加と片付けを受け持つ。
/// 保存したレイアウトに知らないパネル（無効になったプラグインのパネルなど）があっても、そのパネルだけ外して起動する。
/// プラグインのパネル（<see cref="RegisterPluginPanel"/>）も、アプリのパネルと同じ決まりで出し入れ・保存・復元する。
/// ライブモードでは、タイムラインとグラフを作らず、並びも別のファイルに保存する。
/// </summary>
public sealed class DockingLayout
{
    public const string SceneId = "Panel.Scene";
    public const string PropertiesId = "Panel.Properties";
    public const string MorphId = "Panel.Morph";
    public const string TimelineId = "Panel.Timeline";
    public const string LogId = "Panel.Log";
    public const string GraphId = "Panel.Graph";
    public const string TrackingId = "Panel.Tracking";
    public const string HotkeysId = "Panel.Hotkeys";
    private const string PreviewPrefix = "Preview.";
    private const string PluginPrefix = "Plugin/";

    private readonly DockingManager _dock;
    private readonly EditorContext _context;
    private readonly AppMode _mode;
    private readonly Dictionary<string, (string Title, FrameworkElement Content, AnchorableShowStrategy Side)> _panels;
    private readonly List<PreviewPanel> _previews = [];
    private readonly Dictionary<string, PluginPanelSlot> _pluginPanels = [];
    private bool _layoutReady;

    public DockingLayout(DockingManager dock, EditorContext context, AppMode mode = AppMode.Edit)
    {
        _dock = dock;
        _context = context;
        _mode = mode;
        _panels = new()
        {
            [SceneId] = ("シーン", new ScenePanel(context), AnchorableShowStrategy.Left),
            [PropertiesId] = ("プロパティ", new PropertiesPanel(context), AnchorableShowStrategy.Right),
            [MorphId] = ("モーフ", new MorphPanel(context), AnchorableShowStrategy.Right),
            [LogId] = ("ログ", new LogPanel(), AnchorableShowStrategy.Bottom),
        };
        if (mode == AppMode.Edit)
        {
            // キーフレームの編集はエディットモードだけ
            _panels[TimelineId] = ("タイムライン", new TimelinePanel(context), AnchorableShowStrategy.Bottom);
            _panels[GraphId] = ("グラフ", new GraphEditorPanel(context), AnchorableShowStrategy.Bottom);
        }
        else if (context.Tracking is not null)
        {
            _panels[TrackingId] = ("トラッキング", new Live.TrackingPanel(context), AnchorableShowStrategy.Right); // 顔のトラッキングはライブだけ
        }
        if (mode == AppMode.Live && context.Hotkeys is not null)
        {
            _panels[HotkeysId] = ("キー操作", new Live.HotkeyPanel(context), AnchorableShowStrategy.Right); // キー操作もライブだけ
        }
        dock.DocumentClosed += (_, e) =>
        {
            if (e.Document.Content is PreviewPanel preview)
            {
                RemovePreview(preview);
            }
        };
    }

    public event EventHandler<PreviewPanel>? PreviewCreated;

    /// <summary>プラグインのパネルが増えた・減った（表示のメニューを作り直す）。</summary>
    public event EventHandler? PluginPanelsChanged;

    /// <summary>プラグインのパネル（登録順）。</summary>
    public IEnumerable<(string Id, string Title)> PluginPanels => _pluginPanels.Select(p => (p.Key, p.Value.Title));

    /// <summary>プラグインのパネルの名前（レイアウトの保存に使う）。</summary>
    public static string PluginPanelId(string pluginId, string panelId) => $"{PluginPrefix}{pluginId}/{panelId}";

    /// <summary>
    /// プラグインのパネルを足す。レイアウトを戻す前なら、前回の場所に戻す（前回に無ければ初めの場所に出す）。
    /// 戻した後なら、初めの場所に出す。中身は初めて出すときに作る。
    /// </summary>
    public void RegisterPluginPanel(string id, string title, Func<FrameworkElement> createContent, AnchorableShowStrategy side)
    {
        if (_panels.ContainsKey(id) || _pluginPanels.ContainsKey(id))
        {
            throw new ArgumentException($"同じ Id のパネル（{id}）がすでにあります", nameof(id));
        }
        _pluginPanels[id] = new PluginPanelSlot(title, createContent, side);
        if (_layoutReady && FindAnchorable(id) is null)
        {
            // 動いているアプリで有効にしたとき: 足したパネルを前に出して、増えたことが分かるようにする
            LayoutAnchorable anchorable = CreateAnchorable(id);
            AddMissing(anchorable);
            anchorable.IsSelected = true;
        }
        PluginPanelsChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>プラグインのパネルを外す（プラグインを無効にしたとき）。</summary>
    public void UnregisterPluginPanel(string id)
    {
        if (!_pluginPanels.Remove(id))
        {
            return;
        }
        if (FindAnchorable(id) is { } anchorable)
        {
            anchorable.Content = null;
            if (_dock.Layout.Hidden.Contains(anchorable))
            {
                _dock.Layout.Hidden.Remove(anchorable);
            }
            else
            {
                (anchorable.Parent as ILayoutContainer)?.RemoveChild(anchorable);
            }
        }
        PluginPanelsChanged?.Invoke(this, EventArgs.Empty);
    }

    public event EventHandler<PreviewPanel>? PreviewRemoved;

    public IReadOnlyList<PreviewPanel> Previews => _previews;

    public static IReadOnlyList<(string Id, string Title)> PanelIds { get; } =
    [
        (SceneId, "シーン"),
        (PropertiesId, "プロパティ"),
        (MorphId, "モーフ"),
        (TimelineId, "タイムライン"),
        (GraphId, "グラフ"),
        (LogId, "ログ"),
        (TrackingId, "トラッキング"),
    ];

    /// <summary>このモードで使うパネル（表示のメニューに出す）。</summary>
    public IEnumerable<(string Id, string Title)> Panels => PanelIds.Where(p => _panels.ContainsKey(p.Id));

    private string LayoutFile => UserSettingsStore.LayoutFileFor(_mode);

    private Dictionary<string, PreviewSettings> SavedPreviews(UserSettings settings) => _mode == AppMode.Live ? settings.LivePreviews : settings.Previews;

    /// <summary>保存したレイアウトがあれば復元し、無いか壊れていれば初期配置にする。</summary>
    public void LoadOrDefault(UserSettings settings)
    {
        if (File.Exists(LayoutFile) && SavedPreviews(settings).Count > 0)
        {
            try
            {
                Load(settings);
                AppLog.Info("前回のレイアウトを復元しました");
                return;
            }
            catch (Exception ex)
            {
                AppLog.Warning("レイアウトを復元できなかったため、初期配置で起動します", ex);
            }
        }
        ResetToDefault();
    }

    public void ResetToDefault()
    {
        DetachContents();
        DisposeAllPreviews();
        if (_mode == AppMode.Live)
        {
            ResetToLiveDefault();
            return;
        }

        var documents = new LayoutDocumentPaneGroup { Orientation = Orientation.Horizontal };
        documents.Children.Add(new LayoutDocumentPane(CreatePreviewDocument(PreviewSettings.Default(PreviewLayout.Quad))));
        documents.Children.Add(new LayoutDocumentPane(CreatePreviewDocument(
            new PreviewSettings(PreviewLayout.Single, PreviewLayouts.DefaultQuadPreset.Name, [ViewKind.Camera], true))));

        var bottom = new LayoutAnchorablePane { DockHeight = new GridLength(240) };
        bottom.Children.Add(CreateAnchorable(TimelineId));
        bottom.Children.Add(CreateAnchorable(GraphId));
        bottom.Children.Add(CreateAnchorable(LogId));
        bottom.SelectedContentIndex = 0;

        var center = new LayoutPanel { Orientation = Orientation.Vertical };
        center.Children.Add(documents);
        center.Children.Add(bottom);

        var left = new LayoutAnchorablePane { DockWidth = new GridLength(250) };
        left.Children.Add(CreateAnchorable(SceneId));
        var right = new LayoutAnchorablePane { DockWidth = new GridLength(330) };
        right.Children.Add(CreateAnchorable(PropertiesId));
        right.Children.Add(CreateAnchorable(MorphId));
        right.SelectedContentIndex = 0;

        var root = new LayoutPanel { Orientation = Orientation.Horizontal };
        root.Children.Add(left);
        root.Children.Add(center);
        root.Children.Add(right);
        _dock.Layout = new LayoutRoot { RootPanel = root };
        foreach (string id in _pluginPanels.Keys)
        {
            AddMissing(CreateAnchorable(id));
        }
        _layoutReady = true;
    }

    // ライブの初期配置: 置くための自由視点と、見せる絵のカメラ視点を並べる。下はログだけ
    private void ResetToLiveDefault()
    {
        var documents = new LayoutDocumentPaneGroup { Orientation = Orientation.Horizontal };
        documents.Children.Add(new LayoutDocumentPane(CreatePreviewDocument(
            new PreviewSettings(PreviewLayout.Single, PreviewLayouts.DefaultQuadPreset.Name, [ViewKind.Free], true))));
        documents.Children.Add(new LayoutDocumentPane(CreatePreviewDocument(
            new PreviewSettings(PreviewLayout.Single, PreviewLayouts.DefaultQuadPreset.Name, [ViewKind.Camera], true))));

        var bottom = new LayoutAnchorablePane { DockHeight = new GridLength(160) };
        bottom.Children.Add(CreateAnchorable(LogId));

        var center = new LayoutPanel { Orientation = Orientation.Vertical };
        center.Children.Add(documents);
        center.Children.Add(bottom);

        var left = new LayoutAnchorablePane { DockWidth = new GridLength(250) };
        left.Children.Add(CreateAnchorable(SceneId));
        var right = new LayoutAnchorablePane { DockWidth = new GridLength(330) };
        right.Children.Add(CreateAnchorable(PropertiesId));
        right.Children.Add(CreateAnchorable(MorphId));
        if (_panels.ContainsKey(TrackingId))
        {
            right.Children.Add(CreateAnchorable(TrackingId));
        }
        right.SelectedContentIndex = 0;

        var root = new LayoutPanel { Orientation = Orientation.Horizontal };
        root.Children.Add(left);
        root.Children.Add(center);
        root.Children.Add(right);
        _dock.Layout = new LayoutRoot { RootPanel = root };
        foreach (string id in _pluginPanels.Keys)
        {
            AddMissing(CreateAnchorable(id));
        }
        _layoutReady = true;
    }

    public void Save(UserSettings settings)
    {
        Dictionary<string, PreviewSettings> previews = _previews.ToDictionary(p => p.ContentId, p => p.GetSettings());
        if (_mode == AppMode.Live)
        {
            settings.LivePreviews = previews;
        }
        else
        {
            settings.Previews = previews;
        }
        try
        {
            var serializer = new XmlLayoutSerializer(_dock);
            SafeFile.Write(LayoutFile, temp => serializer.Serialize(temp));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            AppLog.Warning("レイアウトを保存できませんでした", ex);
        }
    }

    public PreviewPanel AddPreview(PreviewSettings? settings = null)
    {
        LayoutDocument document = CreatePreviewDocument(settings ?? PreviewSettings.Default(PreviewLayout.Quad));
        LayoutDocumentPane? pane = _dock.Layout.Descendents().OfType<LayoutDocumentPane>().FirstOrDefault();
        if (pane is null)
        {
            pane = new LayoutDocumentPane();
            _dock.Layout.RootPanel.Children.Add(pane);
        }
        pane.Children.Add(document);
        document.IsActive = true;
        return (PreviewPanel)document.Content;
    }

    public bool IsPanelShown(string id) => FindAnchorable(id) is { IsHidden: false };

    public void TogglePanel(string id)
    {
        LayoutAnchorable? anchorable = FindAnchorable(id);
        if (anchorable is null)
        {
            anchorable = CreateAnchorable(id);
            AddMissing(anchorable);
            anchorable.IsActive = true;
        }
        else if (anchorable.IsHidden)
        {
            anchorable.Show();
            anchorable.IsActive = true;
        }
        else
        {
            anchorable.Hide();
        }
    }

    private void Load(UserSettings settings)
    {
        var serializer = new XmlLayoutSerializer(_dock);
        serializer.LayoutSerializationCallback += (_, e) =>
        {
            string id = e.Model.ContentId ?? string.Empty;
            if (_panels.TryGetValue(id, out var panel))
            {
                e.Content = panel.Content;
                e.Model.Title = panel.Title;
            }
            else if (id.StartsWith(PreviewPrefix, StringComparison.Ordinal))
            {
                e.Content = CreatePreview(id, SavedPreviews(settings).GetValueOrDefault(id));
            }
            else if (_pluginPanels.TryGetValue(id, out PluginPanelSlot? slot))
            {
                e.Content = slot.GetContent();
                e.Model.Title = slot.Title;
            }
            else
            {
                AppLog.Warning($"レイアウトにあるパネル「{id}」が見つからないため外しました（そのパネルを出すプラグインが無効になっている可能性があります）");
                e.Cancel = true;
            }
        };
        try
        {
            serializer.Deserialize(LayoutFile);
        }
        catch
        {
            DisposeAllPreviews();
            throw;
        }

        // 前回のレイアウトに無かった標準パネル（後の版で増えたパネルなど）・プラグインのパネルは足しておく
        foreach (string id in _panels.Keys.Concat(_pluginPanels.Keys).Where(id => FindAnchorable(id) is null).ToList())
        {
            AddMissing(CreateAnchorable(id));
        }
        _layoutReady = true;
        if (_previews.Count == 0)
        {
            AddPreview();
        }
    }

    // 下に出すパネル（グラフなど）は、タイムラインと同じ場所にタブで足す（無ければ下の端に）
    private void AddMissing(LayoutAnchorable anchorable)
    {
        AnchorableShowStrategy side = _panels.TryGetValue(anchorable.ContentId, out var panel) ? panel.Side : _pluginPanels[anchorable.ContentId].Side;
        if (side == AnchorableShowStrategy.Bottom && FindAnchorable(TimelineId)?.Parent is LayoutAnchorablePane pane)
        {
            pane.Children.Add(anchorable);
            return;
        }
        anchorable.AddToLayout(_dock, side);
    }

    private LayoutAnchorable? FindAnchorable(string id) =>
        _dock.Layout.Descendents().OfType<LayoutAnchorable>().Concat(_dock.Layout.Hidden).FirstOrDefault(a => a.ContentId == id);

    private LayoutAnchorable CreateAnchorable(string id)
    {
        if (_pluginPanels.TryGetValue(id, out PluginPanelSlot? slot))
        {
            return new LayoutAnchorable { ContentId = id, Title = slot.Title, Content = slot.GetContent(), CanHide = true, CanClose = false };
        }
        var (title, content, _) = _panels[id];
        return new LayoutAnchorable { ContentId = id, Title = title, Content = content, CanHide = true, CanClose = false };
    }

    /// <summary>プラグインのパネル 1 つ。中身は初めて要るときに作り、レイアウトを組み直しても使い回す。</summary>
    private sealed class PluginPanelSlot(string title, Func<FrameworkElement> createContent, AnchorableShowStrategy side)
    {
        private FrameworkElement? _content;

        public string Title { get; } = title;

        public AnchorableShowStrategy Side { get; } = side;

        public FrameworkElement GetContent() => _content ??= createContent();
    }

    private LayoutDocument CreatePreviewDocument(PreviewSettings settings)
    {
        int number = _previews.Select(p => int.TryParse(p.ContentId[PreviewPrefix.Length..], out int n) ? n : 0).DefaultIfEmpty(0).Max() + 1;
        string id = PreviewPrefix + number;
        return new LayoutDocument { ContentId = id, Title = $"プレビュー {number}", Content = CreatePreview(id, settings), CanClose = true };
    }

    private PreviewPanel CreatePreview(string id, PreviewSettings? settings)
    {
        var preview = new PreviewPanel(_context, id, settings);
        _previews.Add(preview);
        PreviewCreated?.Invoke(this, preview);
        return preview;
    }

    private void RemovePreview(PreviewPanel preview)
    {
        preview.Dispose();
        _previews.Remove(preview);
        PreviewRemoved?.Invoke(this, preview);
    }

    private void DisposeAllPreviews()
    {
        foreach (PreviewPanel preview in _previews.ToList())
        {
            RemovePreview(preview);
        }
    }

    // 古いレイアウトから中身を外してから新しいレイアウトに付け直す（同じパネルを使い回すため）
    private void DetachContents()
    {
        if (_dock.Layout is null)
        {
            return;
        }
        foreach (LayoutContent content in _dock.Layout.Descendents().OfType<LayoutContent>().Concat(_dock.Layout.Hidden).ToList())
        {
            content.Content = null;
        }
    }
}
