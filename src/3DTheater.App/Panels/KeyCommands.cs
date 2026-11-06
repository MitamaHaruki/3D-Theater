using Theater3D.App.Logging;
using Theater3D.Domain;
using Theater3D.Editor;

namespace Theater3D.App.Panels;

/// <summary>
/// 選んでいるキーへの操作（タイムラインの右クリックのメニューと、Delete・Ctrl+C・Ctrl+V から使う）。
/// どれも元に戻せるように <see cref="EditService"/> を通す。ロックした項目のキーは変えない。
/// </summary>
internal static class KeyCommands
{
    public static bool HasSelection(EditorContext context) => context.KeySelection.Count > 0;

    public static bool CanPaste(EditorContext context) => context.KeyClipboard is { Count: > 0 };

    /// <summary>選んでいるキーのうち、ロックしていない項目のもの。</summary>
    public static KeyRef[] EditableSelection(EditorContext context) =>
        [.. context.KeySelection.Items.Where(k => k.Node.CanEdit && KeyEditing.Exists(k))];

    public static void Move(EditorContext context, int delta, bool duplicate)
    {
        KeyRef[] keys = EditableSelection(context);
        delta = KeyEditing.ClampDelta(keys, delta);
        if (keys.Length == 0 || (delta == 0 && !duplicate))
        {
            return;
        }
        context.Playback.Pause();
        IReadOnlyList<KeyRef> moved = [];
        string what = duplicate ? "キーの複製" : "キーの移動";
        context.Edits.Change($"{what}（{keys.Length} 個）", Nodes(keys), () => moved = KeyEditing.Move(keys, delta, duplicate), includeKeys: true);
        context.KeySelection.Set(moved);
    }

    public static void Delete(EditorContext context)
    {
        KeyRef[] keys = EditableSelection(context);
        if (keys.Length == 0)
        {
            return;
        }
        context.Playback.Pause();
        int count = 0;
        context.Edits.Change($"キーの削除（{keys.Length} 個）", Nodes(keys), () => count = KeyEditing.Delete(keys), includeKeys: true);
        context.KeySelection.Clear();
        AppLog.Info($"キーを {count} 個削除しました");
    }

    public static void Copy(EditorContext context)
    {
        KeyRef[] keys = [.. context.KeySelection.Items.Where(KeyEditing.Exists)];
        if (keys.Length == 0)
        {
            return;
        }
        context.KeyClipboard = KeyEditing.Copy(keys);
        AppLog.Info($"キーを {context.KeyClipboard.Count} 個コピーしました");
    }

    /// <summary>コピーしたキーを、いちばん前のキーが <paramref name="frame"/> に来るように貼る。</summary>
    public static void Paste(EditorContext context, int frame)
    {
        if (context.KeyClipboard is not { Count: > 0 } clipboard)
        {
            return;
        }
        SceneNode[] nodes = [.. clipboard.Nodes];
        if (nodes.Any(n => !n.CanEdit))
        {
            AppLog.Warning("ロックしている項目があるため、キーを貼り付けられません（シーン一覧の鍵で外せます）");
            return;
        }
        context.Playback.Pause();
        IReadOnlyList<KeyRef> pasted = [];
        context.Edits.Change($"キーの貼り付け（{clipboard.Count} 個）", nodes, () => pasted = KeyEditing.Paste(clipboard, frame), includeKeys: true);
        context.KeySelection.Set(pasted);
    }

    public static void SetInterpolation(EditorContext context, InterpolationPreset preset)
    {
        KeyRef[] keys = EditableSelection(context);
        if (keys.Length == 0)
        {
            return;
        }
        string name = preset switch
        {
            InterpolationPreset.Linear => "線形",
            InterpolationPreset.Smooth => "なめらか",
            _ => "段階",
        };
        context.Edits.Change($"キーの補間を「{name}」に（{keys.Length} 個）", Nodes(keys), () => KeyEditing.SetInterpolation(keys, preset), includeKeys: true);
    }

    private static SceneNode[] Nodes(IEnumerable<KeyRef> keys) => [.. keys.Select(k => k.Node).Distinct()];
}
