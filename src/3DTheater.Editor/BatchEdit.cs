using System.Numerics;
using Theater3D.Domain;

namespace Theater3D.Editor;

/// <summary>
/// いくつもの項目の値とキーを変えて、まとめて 1 つの「元に戻す」にする編集（<see cref="EditService.Batch"/> の中だけで使える）。
/// プラグインの編集（SDK の ITheaterEditor）に使う。変える前の値は、その項目を初めて変えるときに写す。
/// ロックした項目は変えずに false を返す。値の誤り（負のフレーム・数でない値）は例外にする（まとめた編集ごと取り消す）。
/// 値を変えたときは、利用者が手で変えたときと同じ扱いになる（自動キーが OFF なら未登録の変更、ON なら今のフレームのキー）。
/// </summary>
public sealed class BatchEdit
{
    private readonly EditTransaction _transaction;

    internal BatchEdit(EditTransaction transaction) => _transaction = transaction;

    /// <summary>まだ使える（<see cref="EditService.Batch"/> の途中）。</summary>
    public bool IsOpen { get; private set; } = true;

    /// <summary>ボーンの移動（初期の位置から。親のボーンから見た向き）と回転を変える。</summary>
    public bool SetBonePose(BoneNode bone, Vector3 translation, Quaternion rotation)
    {
        EnsureOpen();
        CheckFinite(translation, rotation);
        if (!Prepare(bone))
        {
            return false;
        }
        bone.Position = translation;
        bone.Orientation = rotation;
        return true;
    }

    /// <summary>モーフの値を変える（0〜1 に収める）。</summary>
    public bool SetMorphWeight(MorphNode morph, float weight)
    {
        EnsureOpen();
        CheckFinite(weight);
        if (!Prepare(morph))
        {
            return false;
        }
        morph.Weight = weight;
        return true;
    }

    /// <summary>
    /// ボーンのキーを置く（同じフレームのキーは置き換える）。補間は、同じフレームにキーがあればその動き方を引き継ぎ、無ければ直線
    /// （キーの登録と同じ）。
    /// </summary>
    public bool SetBoneKey(BoneNode bone, int frame, Vector3 translation, Quaternion rotation)
    {
        EnsureOpen();
        CheckFrame(frame);
        CheckFinite(translation, rotation);
        if (!Prepare(bone))
        {
            return false;
        }
        BoneInterpolation interpolation = BoneInterpolation.Linear;
        KeyInterpolationKind kind = KeyInterpolationKind.Curve;
        if (bone.Keys.TryGet(frame, out BoneKey old))
        {
            interpolation = old.Interpolation;
            kind = old.Kind;
        }
        bone.Keys.Set(new BoneKey(frame, translation, Normalize(rotation), interpolation, kind));
        return true;
    }

    /// <summary>モーフのキーを置く（値は 0〜1 に収める。補間はボーンと同じ決まり）。</summary>
    public bool SetMorphKey(MorphNode morph, int frame, float weight)
    {
        EnsureOpen();
        CheckFrame(frame);
        CheckFinite(weight);
        if (!Prepare(morph))
        {
            return false;
        }
        BezierCurve curve = BezierCurve.Linear;
        KeyInterpolationKind kind = KeyInterpolationKind.Curve;
        if (morph.Keys.TryGet(frame, out MorphKey old))
        {
            curve = old.Curve;
            kind = old.Kind;
        }
        morph.Keys.Set(new MorphKey(frame, Math.Clamp(weight, 0f, 1f), curve, kind));
        return true;
    }

    /// <summary>ボーンのキーを消す。消したら true（キーが無い・ロックしているなら false）。</summary>
    public bool RemoveBoneKey(BoneNode bone, int frame)
    {
        EnsureOpen();
        ArgumentNullException.ThrowIfNull(bone);
        CheckFrame(frame);
        return bone.Keys.TryGet(frame, out _) && Prepare(bone) && bone.Keys.Remove(frame);
    }

    /// <summary>モーフのキーを消す。消したら true（キーが無い・ロックしているなら false）。</summary>
    public bool RemoveMorphKey(MorphNode morph, int frame)
    {
        EnsureOpen();
        ArgumentNullException.ThrowIfNull(morph);
        CheckFrame(frame);
        return morph.Keys.TryGet(frame, out _) && Prepare(morph) && morph.Keys.Remove(frame);
    }

    internal void Close() => IsOpen = false;

    private void EnsureOpen()
    {
        if (!IsOpen)
        {
            throw new InvalidOperationException("この編集はもう終わっています（編集の処理の中だけで使えます）");
        }
    }

    // 変える前の値を写す。ロックしていれば false
    private bool Prepare(SceneNode node)
    {
        ArgumentNullException.ThrowIfNull(node);
        if (!node.CanEdit)
        {
            return false;
        }
        _transaction.Include(node);
        return true;
    }

    private static void CheckFrame(int frame)
    {
        if (frame < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(frame), frame, "フレームは 0 以上にしてください");
        }
    }

    private static void CheckFinite(float value)
    {
        if (!float.IsFinite(value))
        {
            throw new ArgumentException("値が数ではありません（NaN か無限大）", nameof(value));
        }
    }

    private static void CheckFinite(Vector3 translation, Quaternion rotation)
    {
        if (!float.IsFinite(translation.X) || !float.IsFinite(translation.Y) || !float.IsFinite(translation.Z))
        {
            throw new ArgumentException("移動の値が数ではありません（NaN か無限大）", nameof(translation));
        }
        if (!float.IsFinite(rotation.X) || !float.IsFinite(rotation.Y) || !float.IsFinite(rotation.Z) || !float.IsFinite(rotation.W)
            || rotation.LengthSquared() < 1e-12f)
        {
            throw new ArgumentException("回転の値が正しくありません（NaN・無限大・長さ 0）", nameof(rotation));
        }
    }

    private static Quaternion Normalize(Quaternion q) =>
        MathF.Abs(q.LengthSquared() - 1f) <= 1e-6f ? q : Quaternion.Normalize(q);
}

/// <summary>
/// 開いたままのまとめた編集（<see cref="EditService.BeginBatch"/>）。<see cref="Edit"/> で何度も変えてから、
/// <see cref="Commit"/> で 1 つの操作として積むか、<see cref="Cancel"/> で始める前に戻す。Dispose は、まだ終わっていなければ取り消す。
/// </summary>
public sealed class BatchSession : IDisposable
{
    private readonly EditTransaction _transaction;
    private Action? _close;

    internal BatchSession(BatchEdit edit, EditTransaction transaction, Action close)
    {
        Edit = edit;
        _transaction = transaction;
        _close = close;
    }

    /// <summary>変える入口（<see cref="Commit"/>・<see cref="Cancel"/> の後は使えない）。</summary>
    public BatchEdit Edit { get; }

    /// <summary>まだ終わっていない。</summary>
    public bool IsOpen => _close is not null;

    /// <summary>変えた結果を 1 つの操作として積む（何も変わっていなければ積まずに false）。</summary>
    public bool Commit()
    {
        if (!Close())
        {
            return false;
        }
        return _transaction.Commit();
    }

    /// <summary>始める前の値とキーに戻し、何も積まない。</summary>
    public void Cancel()
    {
        if (Close())
        {
            _transaction.Cancel();
        }
    }

    public void Dispose() => Cancel();

    private bool Close()
    {
        if (_close is not { } close)
        {
            return false;
        }
        _close = null;
        Edit.Close();
        close();
        return true;
    }
}
