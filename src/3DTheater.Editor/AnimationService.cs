using Theater3D.Domain;

namespace Theater3D.Editor;

/// <summary>
/// キーフレームとシーンの値をつなぐ。再生位置が変わると、キーのあるボーン・モーフにそのフレームの値を当てはめる。
/// 手で値を変えたときは、自動キーが OFF なら「未登録の変更」として残し、フレームを動かしても値を保つ（指示書 3.3「黙って失わない」）。
/// 未登録の変更は <see cref="RegisterUnregistered"/> でキーにするか、<see cref="DiscardUnregistered"/> で捨てる。
/// 自動キーが ON なら、変えたときに今のフレームへキーを登録する。
/// カメラは、キーがあるときだけ手で動かした値を未登録の変更にする（キーの無いカメラを置くたびに未登録が残らないように）。
/// </summary>
public sealed class AnimationService
{
    private readonly SceneDocument _scene;
    private readonly PlaybackService _playback;
    private int _applying;
    private bool _autoKey;

    public AnimationService(SceneDocument scene, PlaybackService playback)
    {
        _scene = scene;
        _playback = playback;
        scene.NodeChanged += OnNodeChanged;
        playback.FrameChanged += (_, _) => ApplyFrame();
    }

    /// <summary>自動キーの切り替えか、未登録の変更の数が変わった。</summary>
    public event EventHandler? StateChanged;

    /// <summary>自動キーフレーム。初期値は OFF（指示書 3.3）。</summary>
    public bool AutoKey
    {
        get => _autoKey;
        set
        {
            if (_autoKey == value)
            {
                return;
            }
            _autoKey = value;
            StateChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>未登録の変更があるボーン・モーフ（項目の印が元。元に戻すで印が戻っても数が合う）。</summary>
    public IReadOnlyList<SceneNode> Unregistered =>
        [.. Models.SelectMany(m => m.Bones.Where(b => b.HasUnregisteredChange).Cast<SceneNode>()
            .Concat(m.Morphs.Where(x => x.HasUnregisteredChange)))
            .Concat(Cameras.Where(c => c.HasUnregisteredChange))];

    public int UnregisteredCount =>
        Models.Sum(m => m.Bones.Count(b => b.HasUnregisteredChange) + m.Morphs.Count(x => x.HasUnregisteredChange))
        + Cameras.Count(c => c.HasUnregisteredChange);

    /// <summary>
    /// この間は、値やキーが変わっても「手で変えた」とみなさず、キーの当てはめもしない（元に戻すで値を書き戻すときに使う）。
    /// </summary>
    public IDisposable SuspendTracking()
    {
        _applying++;
        return new Scope(() => _applying--);
    }

    /// <summary>未登録の変更の数などが外から変わったことを知らせる。</summary>
    public void NotifyStateChanged() => StateChanged?.Invoke(this, EventArgs.Empty);

    public int CurrentFrame => _playback.CurrentFrame;

    private IEnumerable<ModelNode> Models => _scene.ModelsGroup.Children.OfType<ModelNode>();

    private IEnumerable<CameraNode> Cameras => _scene.AllNodes.OfType<CameraNode>();

    /// <summary>今のフレームの値を、キーのある項目に当てはめる（未登録の変更がある項目はそのまま）。</summary>
    public void ApplyFrame()
    {
        _applying++;
        try
        {
            float frame = _playback.CurrentFrame;
            foreach (ModelNode model in Models)
            {
                foreach (BoneNode bone in model.Bones)
                {
                    ApplyBone(bone, frame, resetWithoutKeys: false);
                }
                foreach (MorphNode morph in model.Morphs)
                {
                    ApplyMorph(morph, frame, resetWithoutKeys: false);
                }
            }
            foreach (CameraNode camera in Cameras)
            {
                ApplyCamera(camera, frame);
            }
        }
        finally
        {
            _applying--;
        }
    }

    /// <summary>
    /// 「登録」の対象: 選んだボーン・モーフと、選んだモデルやまとまりの中の未登録の変更。
    /// （モデルを選んで登録しても、動かしていない全ボーンにキーは作らない）
    /// </summary>
    public IReadOnlyList<SceneNode> RegistrationTargets(IEnumerable<SceneNode> selection)
    {
        var targets = new List<SceneNode>();
        var seen = new HashSet<SceneNode>();
        foreach (SceneNode node in selection)
        {
            if (node is BoneNode or MorphNode or CameraNode)
            {
                if (seen.Add(node))
                {
                    targets.Add(node);
                }
                continue;
            }
            foreach (SceneNode child in node.DescendantsAndSelf().Where(IsUnregistered))
            {
                if (seen.Add(child))
                {
                    targets.Add(child);
                }
            }
        }
        return targets;
    }

    /// <summary>今のフレームに、指定したボーン・モーフの今の値でキーを置く。ロックした項目は飛ばす。置いた数を返す。</summary>
    public int Register(IEnumerable<SceneNode> nodes)
    {
        int frame = _playback.CurrentFrame;
        int count = 0;
        _applying++;
        try
        {
            foreach (SceneNode node in nodes.ToList())
            {
                if (!node.CanEdit)
                {
                    continue;
                }
                switch (node)
                {
                    case BoneNode bone:
                        ClearUnregistered(bone);
                        bone.Keys.Set(NewBoneKey(bone, frame));
                        count++;
                        break;
                    case MorphNode morph:
                        ClearUnregistered(morph);
                        morph.Keys.Set(NewMorphKey(morph, frame));
                        count++;
                        break;
                    case CameraNode camera:
                        ClearUnregistered(camera);
                        camera.Keys.Set(NewCameraKey(camera, frame));
                        count++;
                        break;
                }
            }
        }
        finally
        {
            _applying--;
        }
        if (count > 0)
        {
            StateChanged?.Invoke(this, EventArgs.Empty);
        }
        return count;
    }

    public int RegisterUnregistered() => Register(Unregistered);

    /// <summary>未登録の変更を捨て、キーの値（キーが無ければ初期の値）に戻す。</summary>
    public void DiscardUnregistered()
    {
        IReadOnlyList<SceneNode> nodes = Unregistered;
        if (nodes.Count == 0)
        {
            return;
        }
        float frame = _playback.CurrentFrame;
        _applying++;
        try
        {
            foreach (SceneNode node in nodes)
            {
                ClearUnregistered(node);
                switch (node)
                {
                    case BoneNode bone:
                        ApplyBone(bone, frame, resetWithoutKeys: true);
                        break;
                    case MorphNode morph:
                        ApplyMorph(morph, frame, resetWithoutKeys: true);
                        break;
                    case CameraNode camera:
                        ApplyCamera(camera, frame);
                        break;
                }
            }
        }
        finally
        {
            _applying--;
        }
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>モデルのいちばん後ろのキーのフレーム（キーが無ければ 0）。</summary>
    public static int LastKeyFrame(ModelNode model) => Math.Max(
        model.Bones.Select(b => b.Keys.LastFrame).DefaultIfEmpty(0).Max(),
        model.Morphs.Select(m => m.Keys.LastFrame).DefaultIfEmpty(0).Max());

    private static BoneKey NewBoneKey(BoneNode bone, int frame)
    {
        // 同じフレームにキーがあれば、その動き方を引き継ぐ。無ければ直線
        BoneInterpolation interpolation = BoneInterpolation.Linear;
        KeyInterpolationKind kind = KeyInterpolationKind.Curve;
        if (bone.Keys.FindSegment(frame, out int before, out int after) && before == after && bone.Keys.Keys[before].Frame == frame)
        {
            interpolation = bone.Keys.Keys[before].Interpolation;
            kind = bone.Keys.Keys[before].Kind;
        }
        return new BoneKey(frame, bone.Position, bone.Orientation, interpolation, kind);
    }

    private static MorphKey NewMorphKey(MorphNode morph, int frame)
    {
        BezierCurve curve = BezierCurve.Linear;
        KeyInterpolationKind kind = KeyInterpolationKind.Curve;
        if (morph.Keys.FindSegment(frame, out int before, out int after) && before == after && morph.Keys.Keys[before].Frame == frame)
        {
            curve = morph.Keys.Keys[before].Curve;
            kind = morph.Keys.Keys[before].Kind;
        }
        return new MorphKey(frame, morph.Weight, curve, kind);
    }

    private static CameraKey NewCameraKey(CameraNode camera, int frame)
    {
        CameraInterpolation interpolation = CameraInterpolation.Linear;
        KeyInterpolationKind kind = KeyInterpolationKind.Curve;
        bool orthographic = false;
        if (camera.Keys.TryGet(frame, out CameraKey existing))
        {
            interpolation = existing.Interpolation;
            kind = existing.Kind;
            orthographic = existing.Orthographic;
        }
        CameraPose pose = camera.Pose;
        return new CameraKey(frame, pose.Target, pose.Rotation, pose.Distance, pose.FieldOfView, interpolation, kind, orthographic);
    }

    private static void ApplyCamera(CameraNode camera, float frame)
    {
        if (!camera.HasUnregisteredChange && KeyframeEvaluator.TryEvaluate(camera.Keys, frame, out CameraPose pose))
        {
            camera.ApplyPose(pose);
        }
    }

    private static void ApplyBone(BoneNode bone, float frame, bool resetWithoutKeys)
    {
        if (bone.HasUnregisteredChange)
        {
            return;
        }
        if (KeyframeEvaluator.TryEvaluate(bone.Keys, frame, out var translation, out var rotation))
        {
            bone.Position = translation;
            bone.Orientation = rotation;
        }
        else if (resetWithoutKeys)
        {
            bone.Position = System.Numerics.Vector3.Zero;
            bone.Orientation = System.Numerics.Quaternion.Identity;
        }
    }

    private static void ApplyMorph(MorphNode morph, float frame, bool resetWithoutKeys)
    {
        if (morph.HasUnregisteredChange)
        {
            return;
        }
        if (KeyframeEvaluator.TryEvaluate(morph.Keys, frame, out float weight))
        {
            morph.Weight = weight;
        }
        else if (resetWithoutKeys)
        {
            morph.Weight = 0;
        }
    }

    private void OnNodeChanged(object? sender, SceneNodeChangedEventArgs e)
    {
        if (_applying > 0)
        {
            return;
        }
        switch (e.Node)
        {
            case BoneNode bone when e.PropertyName is nameof(SceneNode.Position) or nameof(SceneNode.Rotation):
                OnUserEdit(bone);
                break;
            case MorphNode morph when e.PropertyName == nameof(MorphNode.Weight):
                OnUserEdit(morph);
                break;
            case BoneNode bone when e.PropertyName == nameof(BoneNode.Keys):
                Apply(() => ApplyBone(bone, _playback.CurrentFrame, resetWithoutKeys: false));
                break;
            case MorphNode morph when e.PropertyName == nameof(MorphNode.Keys):
                Apply(() => ApplyMorph(morph, _playback.CurrentFrame, resetWithoutKeys: false));
                break;
            case CameraNode camera when e.PropertyName is nameof(SceneNode.Position) or nameof(SceneNode.Rotation)
                or nameof(CameraNode.FieldOfView) or nameof(CameraNode.TargetDistance):
                if (_autoKey || camera.Keys.Count > 0)
                {
                    OnUserEdit(camera);
                }
                break;
            case CameraNode camera when e.PropertyName == nameof(CameraNode.Keys):
                Apply(() => ApplyCamera(camera, _playback.CurrentFrame));
                break;
        }
    }

    private void OnUserEdit(SceneNode node)
    {
        if (_autoKey && node.CanEdit)
        {
            Register([node]);
            return;
        }
        if (!IsUnregistered(node))
        {
            Apply(() => SetUnregisteredFlag(node, true));
            StateChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    private static bool IsUnregistered(SceneNode node) =>
        node is BoneNode { HasUnregisteredChange: true } or MorphNode { HasUnregisteredChange: true } or CameraNode { HasUnregisteredChange: true };

    private static void ClearUnregistered(SceneNode node)
    {
        if (IsUnregistered(node))
        {
            SetUnregisteredFlag(node, false);
        }
    }

    private static void SetUnregisteredFlag(SceneNode node, bool value)
    {
        switch (node)
        {
            case BoneNode bone:
                bone.HasUnregisteredChange = value;
                break;
            case MorphNode morph:
                morph.HasUnregisteredChange = value;
                break;
            case CameraNode camera:
                camera.HasUnregisteredChange = value;
                break;
        }
    }

    private sealed class Scope(Action dispose) : IDisposable
    {
        private Action? _dispose = dispose;

        public void Dispose()
        {
            _dispose?.Invoke();
            _dispose = null;
        }
    }

    private void Apply(Action action)
    {
        _applying++;
        try
        {
            action();
        }
        finally
        {
            _applying--;
        }
    }
}
