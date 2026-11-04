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
/// </summary>
public sealed class DockingLayout
{
    public const string SceneId = "Panel.Scene";
    public const string PropertiesId = "Panel.Properties";
    public const string TimelineId = "Panel.Timeline";
    public const string LogId = "Panel.Log";
    private const string PreviewPrefix = "Preview.";

    private readonly DockingManager _dock;
    private readonly EditorContext _context;
    private readonly Dictionary<string, (string Title, FrameworkElement Content, AnchorableShowStrategy Side)> _panels;
    private readonly List<PreviewPanel> _previews = [];

    public DockingLayout(DockingManager dock, EditorContext context)
    {
        _dock = dock;
        _context = context;
        _panels = new()
        {
            [SceneId] = ("シーン", new ScenePanel(context), AnchorableShowStrategy.Left),
            [PropertiesId] = ("プロパティ", new PropertiesPanel(context), AnchorableShowStrategy.Right),
            [TimelineId] = ("タイムライン", new TimelinePanel(context), AnchorableShowStrategy.Bottom),
            [LogId] = ("ログ", new LogPanel(), AnchorableShowStrategy.Bottom),
        };
        dock.DocumentClosed += (_, e) =>
        {
            if (e.Document.Content is PreviewPanel preview)
            {
                RemovePreview(preview);
            }
        };
    }

    public event EventHandler<PreviewPanel>? PreviewCreated;

    public event EventHandler<PreviewPanel>? PreviewRemoved;

    public IReadOnlyList<PreviewPanel> Previews => _previews;

    public static IReadOnlyList<(string Id, string Title)> PanelIds { get; } =
    [
        (SceneId, "シーン"),
        (PropertiesId, "プロパティ"),
        (TimelineId, "タイムライン"),
        (LogId, "ログ"),
    ];

    /// <summary>保存したレイアウトがあれば復元し、無いか壊れていれば初期配置にする。</summary>
    public void LoadOrDefault(UserSettings settings)
    {
        if (File.Exists(UserSettingsStore.LayoutFile) && settings.Previews.Count > 0)
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

        var documents = new LayoutDocumentPaneGroup { Orientation = Orientation.Horizontal };
        documents.Children.Add(new LayoutDocumentPane(CreatePreviewDocument(PreviewSettings.Default(PreviewLayout.Quad))));
        documents.Children.Add(new LayoutDocumentPane(CreatePreviewDocument(
            new PreviewSettings(PreviewLayout.Single, PreviewLayouts.DefaultQuadPreset.Name, [ViewKind.Camera], true))));

        var bottom = new LayoutAnchorablePane { DockHeight = new GridLength(240) };
        bottom.Children.Add(CreateAnchorable(TimelineId));
        bottom.Children.Add(CreateAnchorable(LogId));
        bottom.SelectedContentIndex = 0;

        var center = new LayoutPanel { Orientation = Orientation.Vertical };
        center.Children.Add(documents);
        center.Children.Add(bottom);

        var left = new LayoutAnchorablePane { DockWidth = new GridLength(250) };
        left.Children.Add(CreateAnchorable(SceneId));
        var right = new LayoutAnchorablePane { DockWidth = new GridLength(330) };
        right.Children.Add(CreateAnchorable(PropertiesId));

        var root = new LayoutPanel { Orientation = Orientation.Horizontal };
        root.Children.Add(left);
        root.Children.Add(center);
        root.Children.Add(right);
        _dock.Layout = new LayoutRoot { RootPanel = root };
    }

    public void Save(UserSettings settings)
    {
        settings.Previews = _previews.ToDictionary(p => p.ContentId, p => p.GetSettings());
        try
        {
            var serializer = new XmlLayoutSerializer(_dock);
            SafeFile.Write(UserSettingsStore.LayoutFile, temp => serializer.Serialize(temp));
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
            anchorable.AddToLayout(_dock, _panels[id].Side);
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
                e.Content = CreatePreview(id, settings.Previews.GetValueOrDefault(id));
            }
            else
            {
                AppLog.Warning($"レイアウトにあるパネル「{id}」が見つからないため外しました（そのパネルを出すプラグインが無効になっている可能性があります）");
                e.Cancel = true;
            }
        };
        try
        {
            serializer.Deserialize(UserSettingsStore.LayoutFile);
        }
        catch
        {
            DisposeAllPreviews();
            throw;
        }

        // 前回のレイアウトに無かった標準パネルは足しておく（後の版で増えたパネルなど）
        foreach (string id in _panels.Keys.Where(id => FindAnchorable(id) is null))
        {
            CreateAnchorable(id).AddToLayout(_dock, _panels[id].Side);
        }
        if (_previews.Count == 0)
        {
            AddPreview();
        }
    }

    private LayoutAnchorable? FindAnchorable(string id) =>
        _dock.Layout.Descendents().OfType<LayoutAnchorable>().Concat(_dock.Layout.Hidden).FirstOrDefault(a => a.ContentId == id);

    private LayoutAnchorable CreateAnchorable(string id)
    {
        var (title, content, _) = _panels[id];
        return new LayoutAnchorable { ContentId = id, Title = title, Content = content, CanHide = true, CanClose = false };
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
