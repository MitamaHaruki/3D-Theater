using System.Numerics;
using System.Windows;
using Plugin.Core;
using Plugin.UI.Wpf;
using Theater3D.App.Shell;
using Theater3D.Editor;
using Theater3D.Plugin.Sdk;

namespace Theater3D.App.Plugins;

/// <summary>
/// プラグインの編集の道具（指示書 7.2 の「編集ツール」。SDK 1.5）。道具はツールバーに並び、選ぶとプレビューの左ボタンの操作を受け取る。
/// ドラッグ 1 回の編集はアプリがまとめて開き（<see cref="EditService.BeginBatch"/>）、放したら 1 つの「元に戻す」として積み、
/// 取り消し・エラーなら元に戻す。道具の例外は受け止めて記録し、アプリは止めない。
/// </summary>
public sealed partial class PluginHost
{
    private readonly List<EditTool> _tools = [];

    /// <summary>編集の道具が増えた・減った（ツールバーを作り直す）。</summary>
    public event EventHandler? EditToolsChanged;

    /// <summary>編集の道具（登録順）。</summary>
    public IReadOnlyList<EditTool> EditTools => _tools;

    /// <summary>プラグインの編集の道具 1 つ。</summary>
    public sealed class EditTool
    {
        internal EditTool(PluginEntry plugin, PluginEditTool tool)
        {
            Plugin = plugin;
            Tool = tool;
            string description = tool.Description is { Length: > 0 } d ? "\n" + d : string.Empty;
            ToolTip = $"{tool.Title}{description}\n（プラグイン「{plugin.Name}」の編集の道具。もう一度押すと外します）";
        }

        public PluginEntry Plugin { get; }

        public PluginEditTool Tool { get; }

        public string ToolTip { get; }
    }

    /// <summary>この道具を選んでいるか。</summary>
    public bool IsActive(EditTool tool) => _context.Tools.Active is ToolAdapter adapter && adapter.Tool == tool;

    /// <summary>道具を選ぶ（選んでいれば外して、アプリの道具に戻す）。</summary>
    public void ToggleEditTool(EditTool tool)
    {
        if (IsActive(tool))
        {
            _context.Tools.Activate(null);
            return;
        }
        IEditToolHandler handler;
        try
        {
            handler = tool.Tool.Activate() ?? throw new InvalidOperationException("受け取り手が null です");
        }
        catch (Exception ex)
        {
            Manager.ReportProblem(tool.Plugin, $"編集の道具「{tool.Tool.Title}」を選ぶときにエラーが起きました", ex);
            MessageBox.Show(_owner, $"プラグイン「{tool.Plugin.Name}」の編集の道具「{tool.Tool.Title}」を選べませんでした。\n\n{ex.Message}",
                "プラグイン", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        _context.Tools.Activate(new ToolAdapter(this, tool, handler));
    }

    private void AddEditTool(EditTool tool)
    {
        _tools.Add(tool);
        EditToolsChanged?.Invoke(this, EventArgs.Empty);
    }

    private void RemoveEditTool(EditTool tool)
    {
        if (IsActive(tool))
        {
            _context.Tools.Activate(null);
        }
        if (_tools.Remove(tool))
        {
            EditToolsChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>プラグインの道具を、プレビューの道具として使う（ドラッグ 1 回分の編集を開き、閉じる）。</summary>
    private sealed class ToolAdapter(PluginHost host, EditTool tool, IEditToolHandler handler) : IViewportTool
    {
        private BatchSession? _session;
        private TheaterEditService.Session? _edit;

        public EditTool Tool => tool;

        public bool Press(ViewportPointer pointer)
        {
            if (host._context.Edits.HasOpenBatch)
            {
                return false; // ほかの編集の途中
            }
            _session = host._context.Edits.BeginBatch(tool.Tool.Title);
            _edit = new TheaterEditService.Session(host._context, _session.Edit);
            bool taken;
            try
            {
                taken = handler.Pressed(Convert(pointer));
            }
            catch (Exception ex)
            {
                Fail(ex, "押したとき");
                return false;
            }
            if (!taken)
            {
                EndSession(commit: false); // 受け取らなかったときは、そこまでの編集を戻す
            }
            return taken;
        }

        public bool Move(ViewportPointer pointer)
        {
            try
            {
                handler.Moved(Convert(pointer));
                return true;
            }
            catch (Exception ex)
            {
                Fail(ex, "動かしたとき");
                Notify(handler.Cancelled, "取り消したとき");
                return false;
            }
        }

        public void Release(ViewportPointer pointer)
        {
            try
            {
                handler.Released(Convert(pointer));
            }
            catch (Exception ex)
            {
                Fail(ex, "放したとき");
                return;
            }
            EndSession(commit: true);
        }

        public void Cancel()
        {
            EndSession(commit: false);
            Notify(handler.Cancelled, "取り消したとき");
        }

        public void Deactivate()
        {
            EndSession(commit: false);
            Notify(handler.Deactivated, "外したとき");
        }

        private EditToolPointer Convert(ViewportPointer pointer) =>
            new(pointer.Position, pointer.ViewSize, pointer.Modifiers, new TheaterView(pointer, _session is { IsOpen: true } ? _edit : null));

        // エラー: ドラッグの編集を戻し、記録して状態表示に出す
        private void Fail(Exception ex, string when)
        {
            EndSession(commit: false);
            host.Manager.ReportProblem(tool.Plugin, $"編集の道具「{tool.Tool.Title}」で、{when}にエラーが起きました", ex);
            host.ShowStatus?.Invoke($"編集の道具「{tool.Tool.Title}」でエラーが起きました（{ex.Message}）", true);
        }

        private void Notify(Action action, string when)
        {
            try
            {
                action();
            }
            catch (Exception ex)
            {
                host.Manager.ReportProblem(tool.Plugin, $"編集の道具「{tool.Tool.Title}」で、{when}にエラーが起きました", ex);
            }
        }

        private void EndSession(bool commit)
        {
            BatchSession? session = _session;
            _session = null;
            _edit = null;
            if (session is null)
            {
                return;
            }
            if (commit)
            {
                session.Commit();
            }
            else
            {
                session.Cancel();
            }
        }
    }

    /// <summary>SDK の <see cref="ITheaterView"/>（道具に渡す、プレビューの視線と、ドラッグの編集）。</summary>
    private sealed class TheaterView(ViewportPointer pointer, ISceneEdit? dragEdit) : ITheaterView
    {
        public Vector3 RayOrigin => pointer.Ray.Origin;

        public Vector3 RayDirection => Vector3.Normalize(pointer.Ray.Direction);

        public ISceneEdit? DragEdit => dragEdit;

        public Vector2? Project(Vector3 world)
        {
            Vector4 clip = Vector4.Transform(new Vector4(world, 1f), pointer.ViewProjection);
            if (clip.W <= 1e-6f)
            {
                return null;
            }
            float x = clip.X / clip.W;
            float y = clip.Y / clip.W;
            return new Vector2((x + 1f) * 0.5f * (float)pointer.ViewSize.Width, (1f - y) * 0.5f * (float)pointer.ViewSize.Height);
        }
    }

    private sealed partial class Scope : IEditToolRegistry
    {
        public IDisposable Register(PluginEditTool tool)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            ArgumentNullException.ThrowIfNull(tool);
            ArgumentException.ThrowIfNullOrWhiteSpace(tool.Id);
            ArgumentException.ThrowIfNullOrWhiteSpace(tool.Title);
            ArgumentNullException.ThrowIfNull(tool.Activate);
            if (tool.Glyph is { } glyph && (glyph.Length == 0 || glyph.Length > 2))
            {
                throw new ArgumentException("絵の文字（Glyph）は 1 文字にしてください", nameof(tool));
            }
            if (_host._tools.Any(t => t.Plugin == _entry && t.Tool.Id == tool.Id))
            {
                throw new ArgumentException($"同じ Id（{tool.Id}）の編集の道具がすでにあります", nameof(tool));
            }
            var entry = new EditTool(_entry, tool);
            _host.AddEditTool(entry);
            return Track(new Registration(() => _host.RemoveEditTool(entry)));
        }
    }
}
