using System.IO;
using System.Windows;
using Theater3D.App.Logging;
using Theater3D.Domain;
using Theater3D.Editor;
using Theater3D.Media;

namespace Theater3D.App.Audio;

/// <summary>
/// 音声ファイルを読み込んで、シーンの音声にする（音声は 1 つまでで、前の音声とは入れ替える）。元のファイルは書き換えない。
/// 作品が音声より短ければ、音声の終わりまで伸ばす。読み込み・入れ替え・外すは元に戻せる。
/// </summary>
internal static class AudioImporter
{
    public const string FileFilter = "音声 (*.wav;*.mp3;*.m4a;*.aac;*.wma;*.flac)|*.wav;*.mp3;*.m4a;*.aac;*.wma;*.flac|すべてのファイル (*.*)|*.*";

    public static bool IsAudioFile(string path) => AudioClip.IsAudioFile(path);

    public static async Task<AudioNode?> ImportAsync(EditorContext context, string path, Window owner)
    {
        string fullPath = Path.GetFullPath(path);
        AppLog.Info($"音声を読み込みます: {fullPath}");
        AudioClip clip;
        try
        {
            clip = await Task.Run(() => AudioClip.Open(fullPath));
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            AppLog.Warning($"音声を読み込めませんでした: {fullPath}", ex);
            MessageBox.Show(owner, $"音声を読み込めませんでした。\n{Path.GetFileName(fullPath)}\n\n{ex.Message}", "音声の読み込み",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return null;
        }

        context.Playback.Pause();
        var node = new AudioNode(Path.GetFileNameWithoutExtension(fullPath), fullPath, clip.Duration);
        int end = node.EndFrame(context.Playback.FrameRate);
        // 読み込みと、それに合わせて伸ばした終わりのフレームは、1 回の元に戻すで戻す
        using (context.Undo.BeginGroup())
        {
            if (AudioPresenceAction.Replace(context, node) && context.Edits.ExtendEndFrame(end))
            {
                AppLog.Info($"音声の終わりに合わせて、終わりのフレームを {end} にしました");
            }
        }
        context.Selection.Select(node, SelectMode.Replace, null);
        AppLog.Info($"音声「{node.Name}」を読み込みました（{clip.Duration:m\\:ss\\.fff}、{clip.SampleRate:N0} Hz・{clip.Channels} ch）");
        return node;
    }
}

/// <summary>音声の読み込み・入れ替え・外す（元に戻せる）。</summary>
internal sealed class AudioPresenceAction : IUndoableAction
{
    private readonly EditorContext _context;
    private readonly AudioNode? _removed;
    private readonly AudioNode? _added;

    private AudioPresenceAction(EditorContext context, AudioNode? removed, AudioNode? added)
    {
        _context = context;
        _removed = removed;
        _added = added;
    }

    public string Description => _added is null
        ? $"音声を外す（{_removed?.Name}）"
        : _removed is null ? $"音声の読み込み（{_added.Name}）" : $"音声の入れ替え（{_added.Name}）";

    /// <summary>シーンの音声を <paramref name="next"/> にする（null なら外す）。変わったら true。</summary>
    public static bool Replace(EditorContext context, AudioNode? next)
    {
        AudioNode? current = context.Scene.Audio;
        if (ReferenceEquals(current, next))
        {
            return false;
        }
        if (current is not null && !current.CanEdit)
        {
            AppLog.Warning($"音声「{current.Name}」はロックされているため、外せません");
            return false;
        }
        var action = new AudioPresenceAction(context, current, next);
        action.Redo();
        context.Undo.Push(action);
        return true;
    }

    public void Undo() => Swap(_added, _removed);

    public void Redo() => Swap(_removed, _added);

    private void Swap(AudioNode? from, AudioNode? to)
    {
        _context.Playback.Pause();
        if (from?.Parent is not null)
        {
            if (_context.Selection.Contains(from))
            {
                _context.Selection.Clear();
            }
            _context.Scene.Remove(from);
        }
        if (to is not null && to.Parent is null)
        {
            _context.Scene.Add(_context.Scene.AudioGroup, to);
        }
    }
}
