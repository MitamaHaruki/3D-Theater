using System.Diagnostics;
using System.Numerics;
using Theater3D.App.Logging;
using Theater3D.Domain;
using Theater3D.Editor;
using Theater3D.Formats.Pmx;
using Theater3D.Tracking;

namespace Theater3D.App.Live;

/// <summary>
/// 顔のトラッキングの結果を、キャラクターに当てはめる（ライブモード）。画面の更新ごとに最新の結果を読み、
/// <see cref="FaceRetarget"/> で求めたボーンの回転とモーフの重みを、そのモデルの「顔の重ね」（<see cref="FaceOverlay"/>）にする。
/// 場面の値（ボーン・モーフの項目）は書き換えない（元に戻すの履歴・保存・待機モーションの邪魔をしない）。
/// トラッキングを始めたとき・「正面にする」を押したときは、続く 15 枚の顔の平均を正面として覚える。
/// 止めた・見失ったときは、なめらかに正面へ戻してから重ねを外す。
/// </summary>
public sealed class TrackingDriver
{
    private const int CalibrationFrames = 15;
    private static readonly TimeSpan EaseOutTime = TimeSpan.FromSeconds(1.5);

    private readonly EditorContext _context;
    private readonly TrackingService _tracking;
    private readonly FaceRetarget _retarget;
    private readonly List<FaceSignal> _calibration = [];
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private long _lastSequence;
    private bool _calibrating;
    private ModelNode? _target;
    private FaceRig? _rig;
    private long _stoppedAt = -1;

    internal TrackingDriver(EditorContext context, TrackingService tracking, FaceRetargetSettings settings)
    {
        _context = context;
        _tracking = tracking;
        _retarget = new FaceRetarget(settings);
        settings.Changed += (_, _) => _rig = null; // 対象のモデルが変わったかもしれない
        context.Scene.NodeChanged += (_, e) =>
        {
            if (e.Node == context.Scene.ModelsGroup && e.PropertyName == nameof(SceneNode.Children))
            {
                _rig = null; // モデルを足した・外した
            }
        };
        tracking.StateChanged += (_, _) =>
        {
            if (tracking.IsRunning)
            {
                RequestCalibration(); // 始めたら、まず正面を覚える
            }
        };
        context.RenderLoop.Ticking += (_, _) => OnTick();
    }

    /// <summary>当てはめの様子（パネルに出す）。</summary>
    public string Status { get; private set; } = "トラッキングを始めると、キャラクターに当てはめます";

    /// <summary>今キャラクターに当てはめている値（パネルに出す）。</summary>
    public string Applied { get; private set; } = string.Empty;

    /// <summary>キャラクターに顔の動きを重ねているか（止めた後、正面に戻しきるまで true）。</summary>
    public bool IsActive => _target is not null && _context.GetLoaded(_target)?.FaceOverlay is not null;

    /// <summary>次の 15 枚の顔の平均を、正面として覚え直す。</summary>
    public void RequestCalibration()
    {
        _calibration.Clear();
        _calibrating = true;
        Status = "正面合わせ中（カメラの方をまっすぐ見てください）";
    }

    private void OnTick()
    {
        bool running = _tracking.IsRunning;
        if (!running && _stoppedAt < 0 && _target is null)
        {
            return;
        }
        double now = _clock.Elapsed.TotalSeconds;
        ModelNode? target = ResolveTarget();
        if (target is null || _rig is null || _context.GetLoaded(target) is not { } loaded)
        {
            return;
        }

        FaceSignal? signal = null;
        if (running)
        {
            _stoppedAt = -1;
            TrackingSnapshot snapshot = _tracking.Latest;
            if (snapshot.Sequence == _lastSequence)
            {
                return; // 新しい絵がまだ無い
            }
            _lastSequence = snapshot.Sequence;
            signal = ToSignal(snapshot.Result);
            if (_calibrating)
            {
                if (signal is not null)
                {
                    _calibration.Add(signal);
                }
                if (_calibration.Count < CalibrationFrames)
                {
                    return;
                }
                _retarget.Calibrate(FaceRetarget.Average(_calibration));
                _calibrating = false;
                Status = _rig.Describe(target.Name);
                AppLog.Info($"顔のトラッキングの正面を覚えました（{CalibrationFrames} 枚の平均）。{Status}");
            }
            if (!_retarget.IsCalibrated)
            {
                return;
            }
        }
        else if (_stoppedAt < 0)
        {
            _stoppedAt = Stopwatch.GetTimestamp(); // 止めた: 正面へなめらかに戻す
        }

        FaceOutput output = _retarget.Compute(signal, now);
        loaded.FaceOverlay = _rig.Build(output);
        Applied = Describe(output);
        if (!running && Stopwatch.GetElapsedTime(_stoppedAt) > EaseOutTime)
        {
            loaded.FaceOverlay = null; // 戻しきったので外す
            _stoppedAt = -1;
            _target = null;
            _lastSequence = 0;
            Status = "止まっています";
            Applied = string.Empty;
        }
        loaded.IsPoseDirty = true;
        _context.RenderLoop.InvalidateAll();
    }

    // 当てはめるモデル（決めたモデルか、頭のボーンを持つ最初のモデル）。変わったら前のモデルの重ねを外す
    private ModelNode? ResolveTarget()
    {
        ModelNode[] models = [.. _context.Scene.ModelsGroup.Children.OfType<ModelNode>()];
        Guid? id = _retarget.Settings.TargetModelId;
        ModelNode? target = (id is { } wanted ? models.FirstOrDefault(m => m.Id == wanted) : null)
            ?? models.FirstOrDefault(m => FaceRigNames.Find([.. m.Bones.Select(b => b.Name)], FaceRigNames.Head) >= 0);
        if (!ReferenceEquals(target, _target) || _rig is null)
        {
            if (_target is not null && !ReferenceEquals(target, _target) && _context.GetLoaded(_target) is { } previous)
            {
                previous.FaceOverlay = null;
                previous.IsPoseDirty = true;
            }
            _target = target;
            _rig = target is not null && _context.GetLoaded(target) is { } loaded ? FaceRig.Create(target, loaded.Document) : null;
            if (_rig is not null && !_calibrating)
            {
                Status = _rig.Describe(target!.Name);
            }
            else if (target is null)
            {
                Status = "当てはめるモデルがありません（頭のボーンを持つモデルを読み込んでください）";
            }
        }
        return target;
    }

    private static string Describe(FaceOutput o)
    {
        static float Angle(Quaternion q) => 2 * MathF.Acos(Math.Clamp(MathF.Abs(q.W), 0f, 1f)) * 180f / MathF.PI;
        float M(string key) => o.Morphs.TryGetValue(key, out float v) ? v : 0f;
        return string.Format(System.Globalization.CultureInfo.CurrentCulture,
            "キャラクター: 頭 {0:F0}°・まばたき {1:F2}・ウィンク 左 {2:F2} 右 {3:F2}（キャラクターの左右）・あ {4:F2} い {5:F2} う {6:F2} え {7:F2} お {8:F2}・口角 {9:F2}・眉 上 {10:F2} 寄 {11:F2}",
            Angle(o.UpperBody * o.Neck * o.Head), M(FaceRetarget.Blink), M(FaceRetarget.WinkLeft), M(FaceRetarget.WinkRight),
            M(FaceRetarget.A), M(FaceRetarget.I), M(FaceRetarget.U), M(FaceRetarget.E), M(FaceRetarget.O), M(FaceRetarget.Smile),
            M(FaceRetarget.BrowUp), M(FaceRetarget.BrowAngry));
    }

    private static FaceSignal? ToSignal(FaceTrackingResult? result)
    {
        if (result is not { Found: true } r)
        {
            return null;
        }
        var blend = new Dictionary<string, float>(FaceBlendshapes.Count);
        for (int i = 0; i < FaceBlendshapes.Count && i < r.Blendshapes.Length; i++)
        {
            blend[FaceBlendshapes.Names[i]] = r.Blendshapes[i];
        }
        // 顔の真ん中は鼻先、大きさは右目尻から左目尻までの 3 倍（顔の範囲とほぼ同じ。回しても変わりにくい）
        Vector3 nose = r.Landmarks[HeadPose.NoseTip];
        float eyes = Vector2.Distance(new Vector2(r.Landmarks[HeadPose.RightEyeOuter].X, r.Landmarks[HeadPose.RightEyeOuter].Y),
            new Vector2(r.Landmarks[HeadPose.LeftEyeOuter].X, r.Landmarks[HeadPose.LeftEyeOuter].Y));
        return new FaceSignal(r.Pose.Yaw, r.Pose.Pitch, r.Pose.Roll, new Vector2(nose.X, nose.Y), eyes * 3f, blend);
    }
}

/// <summary>描く直前のポーズに重ねる、顔のトラッキングの分（ボーンに掛ける回転と、モーフの重み）。</summary>
public sealed record FaceOverlay(IReadOnlyList<(int Bone, Quaternion Rotation)> Bones, IReadOnlyList<(int Morph, float Weight)> Morphs)
{
    /// <summary>ポーズの入力に重ねる（回転は今の回転の内側に掛け、モーフは大きいほう）。</summary>
    public void ApplyTo(Animation.PoseInput input)
    {
        foreach (var (bone, rotation) in Bones)
        {
            if ((uint)bone < (uint)input.Rotations.Length)
            {
                input.Rotations[bone] = Quaternion.Normalize(input.Rotations[bone] * rotation);
            }
        }
        foreach (var (morph, weight) in Morphs)
        {
            if ((uint)morph < (uint)input.MorphWeights.Length)
            {
                input.MorphWeights[morph] = Math.Max(input.MorphWeights[morph], weight);
            }
        }
    }
}

/// <summary>キャラクターの当てはめ先（ボーンとモーフの番号）。</summary>
public sealed class FaceRig
{
    private readonly int _upperBody;
    private readonly int _neck;
    private readonly int _head;
    private readonly int _eyes;
    private readonly Dictionary<string, int> _morphs;

    private FaceRig(int upperBody, int neck, int head, int eyes, Dictionary<string, int> morphs)
    {
        _upperBody = upperBody;
        _neck = neck;
        _head = head;
        _eyes = eyes;
        _morphs = morphs;
    }

    public static FaceRig Create(ModelNode model, PmxDocument document)
    {
        string[] bones = new string[model.Bones.Count == 0 ? 0 : model.Bones.Max(b => b.Index) + 1];
        foreach (BoneNode b in model.Bones)
        {
            bones[b.Index] = b.Name;
        }
        string[] morphNames = new string[model.Morphs.Count == 0 ? 0 : model.Morphs.Max(m => m.Index) + 1];
        foreach (MorphNode m in model.Morphs)
        {
            morphNames[m.Index] = m.Name;
        }
        var morphs = new Dictionary<string, int>();
        foreach (var (key, candidates) in FaceRigNames.Morphs)
        {
            int index = FaceRigNames.Find(morphNames, candidates);
            if (index >= 0)
            {
                morphs[key] = index;
            }
        }
        // 片目を閉じるモーフ: どちらの目かは、動かす頂点の位置で決める（モデルの +X が左）
        foreach (string candidate in FaceRigNames.Winks)
        {
            int index = FaceRigNames.Find(morphNames, [candidate]);
            if (index < 0 || index >= document.Morphs.Length)
            {
                continue;
            }
            float side = Side(document, index, 0);
            string key = side > 0 ? FaceRetarget.WinkLeft : side < 0 ? FaceRetarget.WinkRight
                : candidate.EndsWith('右') ? FaceRetarget.WinkRight : FaceRetarget.WinkLeft;
            morphs.TryAdd(key, index);
        }
        return new FaceRig(FaceRigNames.Find(bones, FaceRigNames.UpperBody), FaceRigNames.Find(bones, FaceRigNames.Neck),
            FaceRigNames.Find(bones, FaceRigNames.Head), FaceRigNames.Find(bones, FaceRigNames.Eyes), morphs);
    }

    // モーフが動かす頂点の、重みを付けた X の平均（グループのモーフは中のモーフをたどる）
    private static float Side(PmxDocument document, int morph, int depth)
    {
        if (depth > 4 || morph < 0 || morph >= document.Morphs.Length)
        {
            return 0;
        }
        PmxMorph m = document.Morphs[morph];
        float sum = 0;
        float weight = 0;
        foreach (PmxVertexOffset offset in m.VertexOffsets)
        {
            if ((uint)offset.Vertex < (uint)document.Vertices.Length)
            {
                float w = offset.Translation.Length();
                sum += document.Vertices[offset.Vertex].Position.X * w;
                weight += w;
            }
        }
        float own = weight > 0 ? sum / weight : 0;
        foreach (PmxGroupOffset group in m.GroupOffsets)
        {
            own += Side(document, group.Morph, depth + 1) * group.Weight;
        }
        return own;
    }

    /// <summary>当てはめた先の説明（パネルに出す）。</summary>
    public string Describe(string modelName)
    {
        var parts = new List<string>();
        void Add(int index, string name)
        {
            if (index >= 0)
            {
                parts.Add(name);
            }
        }
        Add(_upperBody, "上半身");
        Add(_neck, "首");
        Add(_head, "頭");
        Add(_eyes, "両目");
        string[] missing = [.. FaceRetarget.MorphKeys.Where(k => !_morphs.ContainsKey(k))];
        return $"当てはめ: {modelName}（ボーン {string.Join("・", parts)}・モーフ {_morphs.Count} 個）" +
            (missing.Length > 0 ? $"。モデルに無いもの: {string.Join("・", missing)}" : string.Empty);
    }

    public FaceOverlay Build(FaceOutput output)
    {
        var bones = new List<(int, Quaternion)>(4);
        bool hasNeck = _neck >= 0;
        if (_upperBody >= 0)
        {
            bones.Add((_upperBody, output.UpperBody));
        }
        if (_head >= 0)
        {
            // 首のボーンが無いモデルは、首の分も頭で回す
            bones.Add((_head, hasNeck ? output.Head : Quaternion.Normalize(output.Neck * output.Head)));
        }
        if (hasNeck)
        {
            bones.Add((_neck, output.Neck));
        }
        if (_eyes >= 0)
        {
            bones.Add((_eyes, output.Eyes));
        }
        var morphs = new List<(int, float)>(_morphs.Count);
        foreach (var (key, index) in _morphs)
        {
            if (output.Morphs.TryGetValue(key, out float weight))
            {
                morphs.Add((index, weight));
            }
        }
        return new FaceOverlay(bones, morphs);
    }
}
