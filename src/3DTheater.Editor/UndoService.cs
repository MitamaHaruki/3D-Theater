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
/// 捨てた操作が <see cref="IDisposable"/> なら破棄する（外したモデルの GPU の資源を手放すため）。
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
            Discard(_undo[0]);
            _undo.RemoveAt(0);
        }
        ClearRedo();
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>いちばん新しい操作を書き換えた（まとめた）ことを知らせる。</summary>
    public void NotifyTopChanged()
    {
        ClearRedo();
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
        foreach (IUndoableAction action in _undo.Concat(_redo))
        {
            Discard(action);
        }
        _undo.Clear();
        _redo.Clear();
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private void ClearRedo()
    {
        foreach (IUndoableAction action in _redo)
        {
            Discard(action);
        }
        _redo.Clear();
    }

    private static void Discard(IUndoableAction action) => (action as IDisposable)?.Dispose();

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

    private bool _inBatch;

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
            && top.ReplayCount == Undo.ReplayCount && top.IncludesKeys == includeKeys && top.Nodes.SequenceEqual(targets))
        {
            // キーを写したかどうかが違う操作はまとめない（途中で自動キーを切り替えたとき、キーを写していない側の写しで
            // 元に戻す・やり直しをすると、キーが戻らなくなるため）
            top.Absorb(after, now);
            Undo.NotifyTopChanged();
            return true;
        }
        Undo.Push(new SceneChange(this, description, targets, before, after, mergeKey, now, includeKeys) { ReplayCount = Undo.ReplayCount });
        return true;
    }

    /// <summary>
    /// 作品の描き方の設定（映像効果・霧・影・イラスト風・床の映り込み・環境の画像）を、何度も変えてから 1 つの操作として残す編集を始める
    /// （設定の画面を開く前に呼び、閉じたら <see cref="SettingsTransaction.Commit"/>）。映像効果のキーと未登録の印も写す
    /// （キーのあるときに映像効果の値を変えると未登録の変更になり、自動キーならキーが置かれるため）。
    /// </summary>
    public SettingsTransaction BeginSettings(string description, RenderSettings settings)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(description);
        ArgumentNullException.ThrowIfNull(settings);
        EffectsNode? effects = animation.Scene.Effects;
        return new SettingsTransaction(this, description, settings, settings.Capture(), effects, effects?.CaptureState(includeKeys: true));
    }

    /// <summary>描き方の設定をすぐ変え、元に戻せるように履歴へ積む（メニューの切り替え）。何も変わらなければ積まずに false。</summary>
    public bool ChangeSettings(string description, RenderSettings settings, Action change)
    {
        if (Undo.IsReplaying)
        {
            change();
            return false;
        }
        SettingsTransaction transaction = BeginSettings(description, settings);
        change();
        return transaction.Commit();
    }

    internal void PushSettings(string description, RenderSettings settings, RenderSettingsSnapshot before, RenderSettingsSnapshot after,
        EffectsNode? effects, NodeState? effectsBefore, NodeState? effectsAfter) =>
        Undo.Push(new SettingsChange(this, description, settings, before, after, effects, effectsBefore, effectsAfter));

    /// <summary>描き方の設定（と映像効果の項目）を写しの値に戻す（元に戻す・やり直しから呼ぶ）。</summary>
    private void RestoreSettings(RenderSettings settings, RenderSettingsSnapshot snapshot, EffectsNode? effects, NodeState? effectsState)
    {
        playback.Pause();
        using (animation.SuspendTracking())
        {
            settings.Restore(snapshot);
            if (effects is not null && effectsState is not null)
            {
                effects.RestoreState(effectsState);
            }
        }
        // 映像効果のキーがあれば（未登録でなければ）、今のフレームの値に合わせ直す
        animation.ApplyFrame();
        animation.NotifyStateChanged();
    }

    /// <summary>描き方の設定を変える操作 1 つ。</summary>
    private sealed class SettingsChange(
        EditService owner, string description, RenderSettings settings, RenderSettingsSnapshot before, RenderSettingsSnapshot after,
        EffectsNode? effects, NodeState? effectsBefore, NodeState? effectsAfter) : IUndoableAction
    {
        public string Description { get; } = description;

        public void Undo() => owner.RestoreSettings(settings, before, effects, effectsBefore);

        public void Redo() => owner.RestoreSettings(settings, after, effects, effectsAfter);
    }

    /// <summary>
    /// ドラッグのように、何度も値を変えてから 1 つの操作として残す編集を始める。
    /// 途中は項目の値を直接変えてよく、<see cref="EditTransaction.Commit"/> で 1 つの操作として積む。
    /// <see cref="EditTransaction.Cancel"/> なら始める前の値に戻す（ドラッグ中の右クリック・Esc）。
    /// </summary>
    public EditTransaction Begin(string description, IEnumerable<SceneNode> nodes, bool includeKeys = false)
    {
        SceneNode[] targets = [.. nodes.Distinct()];
        includeKeys |= animation.AutoKey;
        return new EditTransaction(this, description, targets, [.. targets.Select(n => n.CaptureState(includeKeys))], includeKeys);
    }

    /// <summary>
    /// <paramref name="edit"/> の中で <see cref="BatchEdit"/> を使って変えた値とキーを、まとめて 1 つの操作として積む（プラグインの編集）。
    /// 何も変わらなければ積まずに false。<paramref name="edit"/> が例外を投げたら、始める前の値とキーに戻してから、その例外を投げ直す。
    /// 再生中なら止めてから始める。まとめた編集の中で、もう一度まとめた編集は始められない。
    /// </summary>
    public bool Batch(string description, Action<BatchEdit> edit)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(description);
        ArgumentNullException.ThrowIfNull(edit);
        if (_inBatch)
        {
            throw new InvalidOperationException("まとめた編集の中で、もう一度まとめた編集は始められません");
        }
        if (Undo.IsReplaying)
        {
            throw new InvalidOperationException("元に戻す・やり直しの途中では編集できません");
        }
        playback.Pause();
        EditTransaction transaction = Begin(description, [], includeKeys: true);
        var batch = new BatchEdit(transaction);
        _inBatch = true;
        try
        {
            edit(batch);
        }
        catch
        {
            batch.Close();
            transaction.Cancel();
            throw;
        }
        finally
        {
            _inBatch = false;
        }
        batch.Close();
        return transaction.Commit();
    }

    /// <summary>開いたままのまとめた編集（<see cref="BeginBatch"/>）か、<see cref="Batch"/> の途中か。</summary>
    public bool HasOpenBatch => _inBatch;

    /// <summary>
    /// ドラッグのように、何度も変えてから 1 つの操作として残すまとめた編集を始める（プラグインの編集の道具）。
    /// <see cref="BatchSession.Commit"/> で積み、<see cref="BatchSession.Cancel"/> で始める前に戻す。どちらかをするまで、
    /// ほかのまとめた編集は始められない。再生中なら止めてから始める。
    /// </summary>
    public BatchSession BeginBatch(string description)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(description);
        if (_inBatch)
        {
            throw new InvalidOperationException("まとめた編集の途中では、もう一度まとめた編集は始められません");
        }
        if (Undo.IsReplaying)
        {
            throw new InvalidOperationException("元に戻す・やり直しの途中では編集できません");
        }
        playback.Pause();
        EditTransaction transaction = Begin(description, [], includeKeys: true);
        _inBatch = true;
        return new BatchSession(new BatchEdit(transaction), transaction, () => _inBatch = false);
    }

    /// <summary>
    /// 作品のフレームの速さを変える（元に戻せる）。<paramref name="rescaleKeys"/> なら、<paramref name="keyedNodes"/> のキー・
    /// 終わりのフレーム・今のフレームを同じ割合で動かし、動きの秒数を保つ。false ならフレームの番号のまま（動きの速さが変わる）。
    /// 重なって消えたキーの数を返す。
    /// </summary>
    public int ChangeFrameRate(int frameRate, IEnumerable<SceneNode> keyedNodes, bool rescaleKeys)
    {
        var before = new PlaybackTiming(playback.FrameRate, playback.EndFrame, playback.CurrentFrame);
        if (frameRate == before.FrameRate || !PlaybackService.SupportedFrameRates.Contains(frameRate))
        {
            return 0;
        }
        SceneNode[] nodes = rescaleKeys ? [.. keyedNodes.Distinct()] : [];
        NodeState[] statesBefore = [.. nodes.Select(n => n.CaptureState(includeKeys: true))];
        double scale = frameRate / (double)before.FrameRate;
        int Scale(int frame) => (int)Math.Round(frame * scale, MidpointRounding.AwayFromZero);

        playback.Pause();
        int lost = 0;
        using (animation.SuspendTracking())
        {
            playback.FrameRate = frameRate;
            if (rescaleKeys)
            {
                lost = KeyEditing.ScaleTime(nodes, scale);
                playback.EndFrame = Math.Max(1, Scale(before.EndFrame));
                playback.Seek(Scale(before.CurrentFrame));
            }
        }
        animation.ApplyFrame();
        animation.NotifyStateChanged();

        var after = new PlaybackTiming(playback.FrameRate, playback.EndFrame, playback.CurrentFrame);
        NodeState[] statesAfter = [.. nodes.Select(n => n.CaptureState(includeKeys: true))];
        string description = rescaleKeys
            ? $"フレームの速さの変更（{before.FrameRate} → {frameRate} fps、キーの位置も合わせる）"
            : $"フレームの速さの変更（{before.FrameRate} → {frameRate} fps）";
        Undo.Push(new FrameRateChange(this, description, nodes, statesBefore, statesAfter, before, after));
        return lost;
    }

    private void RestoreTiming(SceneNode[] nodes, NodeState[] states, PlaybackTiming timing)
    {
        playback.Pause();
        playback.FrameRate = timing.FrameRate;
        playback.EndFrame = timing.EndFrame;
        playback.Seek(timing.CurrentFrame);
        Restore(nodes, states);
    }

    private readonly record struct PlaybackTiming(int FrameRate, int EndFrame, int CurrentFrame);

    /// <summary>フレームの速さを変える操作 1 つ（キーの位置を合わせたときは、そのキーも戻す）。</summary>
    private sealed class FrameRateChange(
        EditService owner, string description, SceneNode[] nodes, NodeState[] before, NodeState[] after,
        PlaybackTiming timingBefore, PlaybackTiming timingAfter) : IUndoableAction
    {
        public string Description { get; } = description;

        public void Undo() => owner.RestoreTiming(nodes, before, timingBefore);

        public void Redo() => owner.RestoreTiming(nodes, after, timingAfter);
    }

    internal void Push(string description, SceneNode[] nodes, NodeState[] before, NodeState[] after, bool includeKeys) =>
        Undo.Push(new SceneChange(this, description, nodes, before, after, null, DateTime.UtcNow, includeKeys) { ReplayCount = Undo.ReplayCount });

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
        EditService owner, string description, SceneNode[] nodes, NodeState[] before, NodeState[] after, string? mergeKey, DateTime time,
        bool includesKeys) : IUndoableAction
    {
        private NodeState[] _after = after;

        public string Description { get; } = description;

        public IReadOnlyList<SceneNode> Nodes => nodes;

        public string? MergeKey { get; } = mergeKey;

        /// <summary>キーも写したか。</summary>
        public bool IncludesKeys { get; } = includesKeys;

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

/// <summary>描き方の設定を何度も変えてから 1 つの操作として残す編集（<see cref="EditService.BeginSettings"/>）。</summary>
public sealed class SettingsTransaction
{
    private readonly EditService _owner;
    private readonly string _description;
    private readonly RenderSettings _settings;
    private readonly RenderSettingsSnapshot _before;
    private readonly EffectsNode? _effects;
    private readonly NodeState? _effectsBefore;
    private bool _done;

    internal SettingsTransaction(EditService owner, string description, RenderSettings settings, RenderSettingsSnapshot before,
        EffectsNode? effects, NodeState? effectsBefore)
    {
        _owner = owner;
        _description = description;
        _settings = settings;
        _before = before;
        _effects = effects;
        _effectsBefore = effectsBefore;
    }

    /// <summary>始めてから変わっていれば、1 つの操作として積んで true。変わっていなければ積まずに false。2 回目からは何もしない。</summary>
    public bool Commit()
    {
        if (_done)
        {
            return false;
        }
        _done = true;
        RenderSettingsSnapshot after = _settings.Capture();
        NodeState? effectsAfter = _effects?.CaptureState(includeKeys: true);
        if (after == _before && Equals(effectsAfter, _effectsBefore))
        {
            return false;
        }
        _owner.PushSettings(_description, _settings, _before, after, _effects, _effectsBefore, effectsAfter);
        return true;
    }
}

/// <summary>何度も値を変えてから 1 つの操作として残す編集（<see cref="EditService.Begin"/>）。</summary>
public sealed class EditTransaction
{
    private readonly EditService _owner;
    private readonly string _description;
    private readonly List<SceneNode> _nodes;
    private readonly List<NodeState> _before;
    private readonly HashSet<SceneNode> _included;
    private readonly bool _includeKeys;

    internal EditTransaction(EditService owner, string description, SceneNode[] nodes, NodeState[] before, bool includeKeys)
    {
        _owner = owner;
        _description = description;
        _nodes = [.. nodes];
        _before = [.. before];
        _included = [.. nodes];
        _includeKeys = includeKeys;
    }

    /// <summary>まだ終わっていない（確定も取り消しもしていない）。</summary>
    public bool IsActive { get; private set; } = true;

    /// <summary>
    /// この編集で変える項目を足す（その項目を変える前に呼ぶ。今の値を写す）。すでに入っている項目なら何もしない。
    /// 変える項目が前もって分からない編集（<see cref="EditService.Batch"/>）に使う。
    /// </summary>
    public void Include(SceneNode node)
    {
        ArgumentNullException.ThrowIfNull(node);
        if (!IsActive)
        {
            throw new InvalidOperationException("終わった編集には項目を足せません");
        }
        if (_included.Add(node))
        {
            _nodes.Add(node);
            _before.Add(node.CaptureState(_includeKeys));
        }
    }

    /// <summary>変えた結果を 1 つの操作として積む。何も変わっていなければ積まない。積んだら true。</summary>
    public bool Commit()
    {
        if (!IsActive)
        {
            return false;
        }
        IsActive = false;
        NodeState[] after = [.. _nodes.Select(n => n.CaptureState(_includeKeys))];
        if (after.SequenceEqual(_before))
        {
            return false;
        }
        _owner.Push(_description, [.. _nodes], [.. _before], after, _includeKeys);
        return true;
    }

    /// <summary>始める前の値に戻し、何も積まない。</summary>
    public void Cancel()
    {
        if (!IsActive)
        {
            return;
        }
        IsActive = false;
        _owner.Restore(_nodes, _before);
    }
}
