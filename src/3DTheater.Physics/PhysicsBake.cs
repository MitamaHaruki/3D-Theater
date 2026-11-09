using System.Numerics;
using Theater3D.Animation;

namespace Theater3D.Physics;

/// <summary>
/// モデル 1 体の物理の焼き込み。0 フレーム目から 1 フレームずつ順に計算した全剛体の置き方を、フレームごとに覚えておく。
/// 覚えたフレームを表示するときは計算せずに当てはめるので、再生位置をどう動かしても、同じフレームなら同じ姿勢になる
/// （スクラブ整合性）。画面の物理（<see cref="ModelPhysics"/>）とは別の世界で計算する。
/// 入力（フレームごとのボーン・モーフの値、フレームの速さ、重力）が変わったら <see cref="Invalidate"/> で捨てて、やり直す。
/// </summary>
public sealed class PhysicsBake : IDisposable
{
    private readonly ModelPhysics _physics;
    private readonly ModelRig _rig;
    private readonly PoseState _state;
    private readonly List<PhysicsTransform[]> _frames = [];

    /// <param name="physics">焼き込み専用の物理（画面のものとは別に作ったもの）。このオブジェクトが破棄する。</param>
    public PhysicsBake(ModelPhysics physics, ModelRig rig)
    {
        _physics = physics;
        _rig = rig;
        _state = new PoseState(rig);
    }

    /// <summary>計算済みのフレームの数（0 〜 Count - 1 フレーム目を覚えている）。</summary>
    public int Count => _frames.Count;

    /// <summary>焼き込んだときのフレームの速さ。</summary>
    public int FrameRate { get; private set; } = 30;

    /// <summary>捨ててやり直した回数（覚えた結果が古くなったかを見分ける）。</summary>
    public int Version { get; private set; }

    /// <summary>覚えた結果を捨てる。次に <see cref="BakeNext"/> を呼ぶと 0 フレーム目から計算し直す。</summary>
    public void Invalidate(int frameRate, Vector3 gravity)
    {
        _frames.Clear();
        FrameRate = Math.Max(1, frameRate);
        _physics.SetGravity(gravity);
        _physics.NeedsReset = true;
        Version++;
    }

    /// <summary>
    /// 次のフレーム（<see cref="Count"/> フレーム目）を計算して覚える。<paramref name="input"/> はそのフレームのボーン・モーフの値。
    /// 0 フレーム目は剛体を置き直して落ち着かせ、それ以降は 1 フレーム分ずつ進める（再生と同じ）。
    /// </summary>
    public void BakeNext(PoseInput input)
    {
        PoseEvaluator.EvaluateBeforePhysics(_rig, input, _state);
        if (_frames.Count == 0)
        {
            _physics.Reset(_state);
        }
        else
        {
            _physics.Step(_state, 1f / FrameRate, impulseFrames: 1);
        }
        var transforms = new PhysicsTransform[_physics.BodyCount];
        _physics.CopyTransforms(transforms);
        _frames.Add(transforms);
    }

    /// <summary>
    /// 覚えたフレームなら、その剛体の置き方で物理演算のボーンを置いて true を返す。
    /// <see cref="PoseEvaluator.EvaluateBeforePhysics"/> の後に呼ぶ。
    /// </summary>
    public bool TryApply(int frame, PoseState state)
    {
        if ((uint)frame >= (uint)_frames.Count)
        {
            return false;
        }
        _physics.ApplyTransforms(state, _frames[frame]);
        return true;
    }

    public void Dispose() => _physics.Dispose();
}
