using Theater3D.Domain;

namespace Theater3D.Editor;

/// <summary>元に戻せる操作 1 つ（実行し終わった後に積む）。</summary>
public interface IUndoableAction
{
    /// <summary>メニューに出す説明（「左ひじの回転」など）。</summary>
    string Description { get; }

    void Undo();

    void Redo();
}

/// <summary>
/// 元に戻す・やり直しの履歴。新しい操作を積むと、やり直しの履歴は消える。古いものから <see cref="Limit"/> 個を超えた分は捨てる。
/// </summary>
public sealed class UndoService(int limit = 300)
{
    private readonly List<IUndoableAction> _undo = [];
    private readonly List<IUndoableAction> _redo = [];

    /// <summary>履歴が変わった（メニューやボタンの有効・無効を合わせるため）。</summary>
    public event EventHandler? Changed;

    public int Limit { get; } = Math.Max(1, limit);

    public bool CanUndo => _undo.Count > 0;

    public bool CanRedo => _redo.Count > 0;

    public string? UndoDescription => _undo.Count > 0 ? _undo[^1].Description : null;

    public string? RedoDescription => _redo.Count > 0 ? _redo[^1].Description : null;

    public int UndoCount => _undo.Count;

    /// <summary>元に戻す・やり直しをした回数（その後の操作は、前の操作とまとめないため）。</summary>
    public int ReplayCount { get; private set; }

    /// <summary>元に戻す・やり直しの途中か（その間に起きた変更は履歴に積まない）。</summary>
    public bool IsReplaying { get; private set; }

    /// <summary>いちばん新しい操作（続けて変えた値を 1 つにまとめるときに見る）。</summary>
    public IUndoableAction? Top => _undo.Count > 0 ? _undo[^1] : null;

    public void Push(IUndoableAction action)
    {
        if (IsReplaying)
        {
            return;
        }
        _undo.Add(action);
        if (_undo.Count > Limit)
        {
            _undo.RemoveAt(0);
        }
        _redo.Clear();
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>いちばん新しい操作を書き換えた（まとめた）ことを知らせる。</summary>
    public void NotifyTopChanged()
    {
        _redo.Clear();
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public bool Undo()
    {
        if (_undo.Count == 0)
        {
            return false;
        }
        IUndoableAction action = _undo[^1];
        _undo.RemoveAt(_undo.Count - 1);
        Replay(action.Undo);
        _redo.Add(action);
        Changed?.Invoke(this, EventArgs.Empty);
        return true;
    }

    public bool Redo()
    {
        if (_redo.Count == 0)
        {
            return false;
        }
        IUndoableAction action = _redo[^1];
        _redo.RemoveAt(_redo.Count - 1);
        Replay(action.Redo);
        _undo.Add(action);
        Changed?.Invoke(this, EventArgs.Empty);
        return true;
    }

    public void Clear()
    {
        _undo.Clear();
        _redo.Clear();
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private void Replay(Action action)
    {
        ReplayCount++;
        IsReplaying = true;
        try
        {
            action();
        }
        finally
        {
            IsReplaying = false;
        }
    }
}

/// <summary>
/// シーンの項目の編集を、元に戻せるように行う入口。変える前と後の値の写し（<see cref="NodeState"/>）を残す。
/// 同じ項目の同じ値を続けて変えたとき（つまみのドラッグ、ホイールでの増減など）は、1 つの操作にまとめる（指示書 3.3「ドラッグ 1 回を 1 つの Undo に」）。
/// 元に戻すときは、キーの当てはめを止めて値を書き戻し、その後で今のフレームのキーを当てはめ直す。
/// </summary>
public sealed class EditService(UndoService undo, AnimationService animation, PlaybackService playback)
{
    /// <summary>この時間内に同じ値を続けて変えたら、1 つの操作にまとめる。</summary>
    public static readonly TimeSpan MergeWindow = TimeSpan.FromSeconds(1.5);

    public UndoService Undo { get; } = undo;

    /// <summary>
    /// <paramref name="nodes"/> を <paramref name="change"/> で変え、元に戻せるように履歴へ積む。何も変わらなければ積まない。
    /// <paramref name="mergeKey"/> が前の操作と同じで、すぐ後なら、前の操作にまとめる。
    /// キーを変える操作は <paramref name="includeKeys"/> を true にする（自動キーが ON のときは値の編集でもキーが変わるので、常に写す）。
    /// </summary>
    public bool Change(string description, IEnumerable<SceneNode> nodes, Action change, string? mergeKey = null, bool includeKeys = false)
    {
        if (Undo.IsReplaying)
        {
            change();
            return false;
        }
        SceneNode[] targets = [.. nodes.Distinct()];
        includeKeys |= animation.AutoKey;
        NodeState[] before = [.. targets.Select(n => n.CaptureState(includeKeys))];
        change();
        NodeState[] after = [.. targets.Select(n => n.CaptureState(includeKeys))];
        if (before.SequenceEqual(after))
        {
            return false;
        }

        DateTime now = DateTime.UtcNow;
        if (mergeKey is not null && Undo.Top is SceneChange top && top.MergeKey == mergeKey && now - top.LastChanged < MergeWindow
            && top.ReplayCount == Undo.ReplayCount && top.Nodes.SequenceEqual(targets))
        {
            top.Absorb(after, now);
            Undo.NotifyTopChanged();
            return true;
        }
        Undo.Push(new SceneChange(this, description, targets, before, after, mergeKey, now) { ReplayCount = Undo.ReplayCount });
        return true;
    }

    /// <summary>写しの値に戻す（元に戻す・やり直しから呼ぶ）。</summary>
    internal void Restore(IReadOnlyList<SceneNode> nodes, IReadOnlyList<NodeState> states)
    {
        playback.Pause();
        using (animation.SuspendTracking())
        {
            for (int i = 0; i < nodes.Count; i++)
            {
                nodes[i].RestoreState(states[i]);
            }
        }
        // 未登録でないキーのある項目は、今のフレームの値に合わせ直す（操作したときとフレームが違うことがある）
        animation.ApplyFrame();
        animation.NotifyStateChanged();
    }

    /// <summary>項目の値を書き換える操作 1 つ。</summary>
    private sealed class SceneChange(
        EditService owner, string description, SceneNode[] nodes, NodeState[] before, NodeState[] after, string? mergeKey, DateTime time) : IUndoableAction
    {
        private NodeState[] _after = after;

        public string Description { get; } = description;

        public IReadOnlyList<SceneNode> Nodes => nodes;

        public string? MergeKey { get; } = mergeKey;

        public DateTime LastChanged { get; private set; } = time;

        /// <summary>作ったときの <see cref="UndoService.ReplayCount"/>。</summary>
        public int ReplayCount { get; init; }

        public void Absorb(NodeState[] after, DateTime time)
        {
            _after = after;
            LastChanged = time;
        }

        public void Undo() => owner.Restore(nodes, before);

        public void Redo() => owner.Restore(nodes, _after);
    }
}
