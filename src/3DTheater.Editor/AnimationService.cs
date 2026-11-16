using Theater3D.Domain;

namespace Theater3D.Editor;

/// <summary>
/// キーフレームとシーンの値をつなぐ。再生位置が変わると、キーのあるボーン・モーフにそのフレームの値を当てはめる。
/// 手で値を変えたときは、自動キーが OFF なら「未登録の変更」として残し、フレームを動かしても値を保つ（指示書 3.3「黙って失わない」）。
/// 未登録の変更は <see cref="RegisterUnregistered"/> でキーにするか、<see cref="DiscardUnregistered"/> で捨てる。
/// 自動キーが ON なら、変えたときに今のフレームへキーを登録する。
/// カメラ・照明・モデルの表示・IK・映像効果は、キーがあるときだけ手で変えた値を未登録の変更にする（キーの無いカメラ・照明を置くたびに
/// 未登録が残らないように）。映像効果の値は作品の描き方の設定（<see cref="Rendering"/>）にあり、キーの値をそこへ当てはめる。
/// </summary>
public sealed class AnimationService
{
    private readonly SceneDocument _scene;
    private readonly PlaybackService _playback;
    private int _applying;
    private bool _autoKey;
    private RenderSettings? _rendering;
    private EffectPose _knownEffects;
    private bool _knownSelfShadow;

    public AnimationService(SceneDocument scene, PlaybackService playback)
    {
        _scene = scene;
        _playback = playback;
        scene.NodeChanged += OnNodeChanged;
        playback.FrameChanged += (_, _) => ApplyFrame();
    }

    /// <summary>自動キーの切り替えか、未登録の変更の数が変わった。</summary>
    public event EventHandler? StateChanged;

    /// <summary>
    /// 作品の描き方の設定（映像効果のキーの当てはめ先）。設定の映像効果の値が手で変わると、映像効果の未登録の変更（自動キーなら登録）にする。
    /// </summary>
    public RenderSettings? Rendering
    {
        get => _rendering;
        set
        {
            if (_rendering is not null)
            {
                _rendering.Changed -= OnRenderingChanged;
            }
            _rendering = value;
            if (value is not null)
            {
                value.Changed += OnRenderingChanged;
                _knownEffects = EffectValues.Capture(value);
                _knownSelfShadow = value.SelfShadow;
            }
        }
    }

    /// <summary>キーの値を当てはめている途中か（この間の値の変化は、手で変えたものではない）。</summary>
    public bool IsApplying => _applying > 0;

    /// <summary>
    /// 手で変えた値に「未登録の変更」の印を付けるか（初期値は付ける）。ライブモードはキーを打たないので付けない
    /// （印が無いので、待機モーションのキーのあるボーンは、次の当てはめでモーションの値に戻る）。
    /// </summary>
    public bool TracksUserEdits { get; set; } = true;

    /// <summary>キーを当てはめるシーン。</summary>
    public SceneDocument Scene => _scene;

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
            .Concat(Cameras.Where(c => c.HasUnregisteredChange))
            .Concat(Lights.Where(l => l.HasUnregisteredChange))
            .Concat(Displays.Where(d => d.HasUnregisteredChange))
            .Concat(Effects.Where(x => x.HasUnregisteredChange))
            .Concat(SelfShadows.Where(x => x.HasUnregisteredChange))
            .Concat(Times.Where(x => x.HasUnregisteredChange))];

    public int UnregisteredCount =>
        Models.Sum(m => m.Bones.Count(b => b.HasUnregisteredChange) + m.Morphs.Count(x => x.HasUnregisteredChange))
        + Cameras.Count(c => c.HasUnregisteredChange) + Lights.Count(l => l.HasUnregisteredChange)
        + Displays.Count(d => d.HasUnregisteredChange) + Effects.Count(x => x.HasUnregisteredChange)
        + SelfShadows.Count(x => x.HasUnregisteredChange) + Times.Count(x => x.HasUnregisteredChange);

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

    private IEnumerable<LightNode> Lights => _scene.AllNodes.OfType<LightNode>();

    private IEnumerable<ModelDisplayNode> Displays => Models.Select(m => m.Display).OfType<ModelDisplayNode>();

    private IEnumerable<EffectsNode> Effects => _scene.Effects is { } effects ? [effects] : [];

    private IEnumerable<SelfShadowNode> SelfShadows => _scene.SelfShadow is { } shadow ? [shadow] : [];

    private IEnumerable<TimeOfDayNode> Times => _scene.TimeOfDay is { } time ? [time] : [];

    /// <summary>
    /// 時刻のキーがあり、時刻がサンライト（MMD の照明）を動かしているか。このときは、サンライトの向き・色・明るさを手で変えると
    /// 未登録の変更にする（次の当てはめで、黙って時刻の値に戻さないため）。
    /// </summary>
    private bool IsDrivenByTime(LightNode light) => _scene.TimeOfDay is { Keys.Count: > 0 } && ReferenceEquals(light, _scene.MainLight);

    /// <summary>
    /// 時刻の項目の時刻の光を、サンライトに当てはめる（時刻のボタン・欄で時刻や昼の太陽の向きを変えたとき。設計判断記録 88 章）。
    /// 時刻のキーがあれば、時刻がサンライトを動かしているので当てはめとして行い、サンライトの未登録の変更も消す（時刻に従わせる）。
    /// キーが無ければ、手でサンライトを変えたのと同じにする（サンライトにキーがあれば未登録の変更、自動キーなら照明のキー）。
    /// </summary>
    public void ApplyTimeToSun()
    {
        if (_scene.TimeOfDay is not { } time || !_scene.MainLight.CanEdit)
        {
            return;
        }
        LightNode sun = _scene.MainLight;
        if (time.Keys.Count == 0)
        {
            SunClock.ApplyToSun(sun, time);
            return;
        }
        bool wasUnregistered = sun.HasUnregisteredChange;
        Apply(() =>
        {
            SunClock.ApplyToSun(sun, time);
            sun.HasUnregisteredChange = false;
        });
        if (wasUnregistered)
        {
            StateChanged?.Invoke(this, EventArgs.Empty);
        }
    }

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
                if (model.Display is { } display)
                {
                    ApplyDisplay(display, frame);
                }
            }
            foreach (CameraNode camera in Cameras)
            {
                ApplyCamera(camera, frame);
            }
            foreach (LightNode light in Lights)
            {
                ApplyLight(light, frame);
            }
            ApplyEffects(frame);
            ApplySelfShadow(frame);
            ApplyTime(frame); // 時刻のキーは、照明のキーより後（サンライトは時刻で決まる）
        }
        finally
        {
            _applying--;
        }
    }

    /// <summary>
    /// ライブモード: 経過時間 <paramref name="seconds"/> のときの値を、モデルごとのモーション（ボーン・モーフ・表示・IK のキー）に当てはめる。
    /// モデルごとに、そのモデルの最後のキーまでの長さで繰り返す（待機モーションのループ。長さの違うモーションも、それぞれの長さで回る）。
    /// キーが 0 フレーム目にしか無いモデル（ポーズ）とキーの無いモデルは触らない。当てはめたモデルがあれば true。
    /// </summary>
    public bool ApplyLive(double seconds)
    {
        bool applied = false;
        _applying++;
        try
        {
            foreach (ModelNode model in Models)
            {
                int length = LastKeyFrame(model);
                if (length <= 0)
                {
                    continue;
                }
                applied = true;
                float frame = LiveLoop.Frame(seconds, _playback.FrameRate, length);
                foreach (BoneNode bone in model.Bones)
                {
                    ApplyBone(bone, frame, resetWithoutKeys: false);
                }
                foreach (MorphNode morph in model.Morphs)
                {
                    ApplyMorph(morph, frame, resetWithoutKeys: false);
                }
                if (model.Display is { } display)
                {
                    ApplyDisplay(display, frame);
                }
            }
        }
        finally
        {
            _applying--;
        }
        return applied;
    }

    /// <summary>
    /// 書き出しのモーションブラー: フレームの途中の時刻 <paramref name="time"/>（小数のフレーム）の値を、キーのある項目に当てはめる。
    /// <paramref name="time"/> と、今描いているフレーム <paramref name="anchorFrame"/> の間に、隣り合うフレーム（間隔 1 以下）のキーの区間が
    /// あれば、その項目は <paramref name="anchorFrame"/> の値にする。MMD でカメラの場面を切り替えるときの打ち方で、途中を補間すると
    /// 2 つの場面が混ざってしまうため。終わったら <see cref="ApplyFrame"/> で今のフレームの値に戻すこと。
    /// </summary>
    public void ApplyFrameAt(float time, int anchorFrame)
    {
        _applying++;
        try
        {
            foreach (ModelNode model in Models)
            {
                foreach (BoneNode bone in model.Bones)
                {
                    ApplyBone(bone, SampleTime(bone.Keys, time, anchorFrame), resetWithoutKeys: false);
                }
                foreach (MorphNode morph in model.Morphs)
                {
                    ApplyMorph(morph, SampleTime(morph.Keys, time, anchorFrame), resetWithoutKeys: false);
                }
                if (model.Display is { } display)
                {
                    ApplyDisplay(display, time); // 段階なので、途中の時刻でも混ざらない
                }
            }
            foreach (CameraNode camera in Cameras)
            {
                ApplyCamera(camera, SampleTime(camera.Keys, time, anchorFrame));
            }
            foreach (LightNode light in Lights)
            {
                ApplyLight(light, SampleTime(light.Keys, time, anchorFrame));
            }
            if (_scene.TimeOfDay is { } timeOfDay)
            {
                ApplyTime(SampleTime(timeOfDay.Keys, time, anchorFrame));
            }
        }
        finally
        {
            _applying--;
        }
    }

    /// <summary>
    /// モーションブラーで値を読む時刻。<paramref name="time"/> と <paramref name="anchorFrame"/> の間に、間隔 1 以下のキーの区間
    /// （場面の切り替え）が入っていれば <paramref name="anchorFrame"/>、そうでなければ <paramref name="time"/>。
    /// </summary>
    public static float SampleTime<TKey>(KeyTrack<TKey> track, float time, int anchorFrame) where TKey : struct, IKeyframe
    {
        if (time == anchorFrame || track.Count < 2)
        {
            return time;
        }
        float lo = Math.Min(time, anchorFrame);
        float hi = Math.Max(time, anchorFrame);
        IReadOnlyList<TKey> keys = track.Keys;
        track.FindSegment(lo, out int before, out _);
        for (int i = Math.Max(before, 0); i + 1 < keys.Count && keys[i].Frame < hi; i++)
        {
            if (keys[i + 1].Frame - keys[i].Frame <= 1 && keys[i + 1].Frame > lo)
            {
                return anchorFrame;
            }
        }
        return time;
    }

    /// <summary>
    /// 「登録」の対象: 選んだボーン・モーフ・カメラ・照明・表示・IK・映像効果・セルフ影と、選んだモデルやまとまりの中の未登録の変更。
    /// （モデルを選んで登録しても、動かしていない全ボーンにキーは作らない）
    /// </summary>
    public IReadOnlyList<SceneNode> RegistrationTargets(IEnumerable<SceneNode> selection)
    {
        var targets = new List<SceneNode>();
        var seen = new HashSet<SceneNode>();
        foreach (SceneNode node in selection)
        {
            if (node is BoneNode or MorphNode or CameraNode or LightNode or ModelDisplayNode or EffectsNode or SelfShadowNode or TimeOfDayNode)
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
                    case LightNode light:
                        ClearUnregistered(light);
                        light.Keys.Set(NewLightKey(light, frame));
                        count++;
                        break;
                    case ModelDisplayNode display:
                        ClearUnregistered(display);
                        display.Keys.Set(new DisplayKey(frame, display.Shown, display.Ik));
                        count++;
                        break;
                    case EffectsNode effects when _rendering is not null:
                        ClearUnregistered(effects);
                        effects.Keys.Set(NewEffectKey(effects, EffectValues.Capture(_rendering), frame));
                        count++;
                        break;
                    case SelfShadowNode shadow when _rendering is not null:
                        ClearUnregistered(shadow);
                        shadow.Keys.Set(NewSelfShadowKey(shadow, _rendering.SelfShadow, frame));
                        count++;
                        break;
                    case TimeOfDayNode time:
                        ClearUnregistered(time);
                        time.Keys.Set(NewTimeKey(time, frame));
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
                    case LightNode light:
                        ApplyLight(light, frame);
                        break;
                    case ModelDisplayNode display:
                        ApplyDisplay(display, frame);
                        break;
                    case EffectsNode:
                        ApplyEffects(frame);
                        break;
                    case SelfShadowNode:
                        ApplySelfShadow(frame);
                        break;
                }
            }
            ApplyTime(frame); // 時刻・サンライトを捨てたときは、時刻のキーの値に戻す（キーが無ければ何もしない）
        }
        finally
        {
            _applying--;
        }
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>モデルのいちばん後ろのキーのフレーム（キーが無ければ 0。表示・IK のキーも含む）。</summary>
    public static int LastKeyFrame(ModelNode model) => Math.Max(Math.Max(
        model.Bones.Select(b => b.Keys.LastFrame).DefaultIfEmpty(0).Max(),
        model.Morphs.Select(m => m.Keys.LastFrame).DefaultIfEmpty(0).Max()),
        model.Display?.Keys.LastFrame ?? 0);

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
        if (camera.Keys.TryGet(frame, out CameraKey existing))
        {
            interpolation = existing.Interpolation;
            kind = existing.Kind;
        }
        CameraPose pose = camera.Pose;
        return new CameraKey(frame, pose.Target, pose.Rotation, pose.Distance, pose.FieldOfView, interpolation, kind, pose.Orthographic);
    }

    private static void ApplyCamera(CameraNode camera, float frame)
    {
        if (!camera.HasUnregisteredChange && KeyframeEvaluator.TryEvaluate(camera.Keys, frame, out CameraPose pose))
        {
            camera.ApplyPose(pose);
        }
    }

    private static LightKey NewLightKey(LightNode light, int frame)
    {
        // 同じフレームにキーがあれば、その動き方を引き継ぐ。無ければ直線
        BezierCurve curve = BezierCurve.Linear;
        KeyInterpolationKind kind = KeyInterpolationKind.Curve;
        if (light.Keys.TryGet(frame, out LightKey existing))
        {
            curve = existing.Curve;
            kind = existing.Kind;
        }
        return new LightKey(frame, light.Pose, curve, kind);
    }

    private static EffectKey NewEffectKey(EffectsNode effects, EffectPose pose, int frame)
    {
        // 同じフレームにキーがあれば、その動き方を引き継ぐ。無ければ直線
        BezierCurve curve = BezierCurve.Linear;
        KeyInterpolationKind kind = KeyInterpolationKind.Curve;
        if (effects.Keys.TryGet(frame, out EffectKey existing))
        {
            curve = existing.Curve;
            kind = existing.Kind;
        }
        return new EffectKey(frame, pose, curve, kind);
    }

    /// <summary>
    /// セルフ影のキー: 使うかは今の設定から。モードと影範囲（このアプリでは使わない値）は、同じフレームのキーがあればそれ、無ければ
    /// 前のキーから引き継ぐ（使うならモード 0 を 1 にする）。
    /// </summary>
    private static SelfShadowKey NewSelfShadowKey(SelfShadowNode shadow, bool enabled, int frame)
    {
        byte mode = 1;
        float distance = SelfShadowKey.DefaultDistance;
        if (KeyframeEvaluator.TryEvaluate(shadow.Keys, frame, out SelfShadowKey near))
        {
            mode = near.Mode;
            distance = near.Distance;
        }
        return new SelfShadowKey(frame, enabled ? (mode == 0 ? (byte)1 : mode) : (byte)0, distance);
    }

    private static TimeKey NewTimeKey(TimeOfDayNode time, int frame)
    {
        // 同じフレームにキーがあれば、その動き方を引き継ぐ。無ければ直線
        BezierCurve curve = BezierCurve.Linear;
        KeyInterpolationKind kind = KeyInterpolationKind.Curve;
        if (time.Keys.TryGet(frame, out TimeKey existing))
        {
            curve = existing.Curve;
            kind = existing.Kind;
        }
        return new TimeKey(frame, time.Hour, curve, kind);
    }

    // 時刻のキーの値を時刻の項目へ入れ、サンライトをその時刻の光にする（_applying の中で呼ぶ。未登録の変更がある方は変えない）
    private void ApplyTime(float frame)
    {
        if (_scene.TimeOfDay is not { HasUnregisteredChange: false } time || !KeyframeEvaluator.TryEvaluate(time.Keys, frame, out float hour))
        {
            return;
        }
        time.Hour = hour;
        if (_scene.MainLight is { HasUnregisteredChange: false } sun)
        {
            SunClock.ApplyToSun(sun, time);
        }
    }

    // セルフ影のキーの値を、描き方の設定へ入れる（_applying の中で呼ぶ）
    private void ApplySelfShadow(float frame)
    {
        if (_rendering is null || _scene.SelfShadow is not { HasUnregisteredChange: false } shadow
            || !KeyframeEvaluator.TryEvaluate(shadow.Keys, frame, out SelfShadowKey key))
        {
            return;
        }
        _rendering.SelfShadow = key.Enabled;
        _knownSelfShadow = _rendering.SelfShadow;
    }

    // 映像効果のキーの値を、描き方の設定へ入れる（_applying の中で呼ぶ）
    private void ApplyEffects(float frame)
    {
        if (_rendering is null || _scene.Effects is not { HasUnregisteredChange: false } effects
            || !KeyframeEvaluator.TryEvaluate(effects.Keys, frame, out EffectPose pose))
        {
            return;
        }
        EffectValues.Apply(_rendering, pose);
        _knownEffects = EffectValues.Capture(_rendering);
    }

    // 描き方の設定が変わった: 映像効果の値・セルフシャドウの ON / OFF が手で変わったなら、未登録の変更（自動キーなら登録）にする
    private void OnRenderingChanged(object? sender, EventArgs e)
    {
        if (_rendering is null)
        {
            return;
        }
        EffectPose now = EffectValues.Capture(_rendering);
        if (now != _knownEffects)
        {
            _knownEffects = now;
            if (_applying == 0 && _scene.Effects is { } effects && (_autoKey || effects.Keys.Count > 0))
            {
                OnUserEdit(effects);
            }
        }
        if (_rendering.SelfShadow != _knownSelfShadow)
        {
            _knownSelfShadow = _rendering.SelfShadow;
            if (_applying == 0 && _scene.SelfShadow is { } shadow && (_autoKey || shadow.Keys.Count > 0))
            {
                OnUserEdit(shadow);
            }
        }
    }

    private static void ApplyDisplay(ModelDisplayNode display, float frame)
    {
        if (!display.HasUnregisteredChange && KeyframeEvaluator.TryEvaluate(display.Keys, frame, out DisplayKey key))
        {
            display.Apply(key);
        }
    }

    private static void ApplyLight(LightNode light, float frame)
    {
        if (!light.HasUnregisteredChange && KeyframeEvaluator.TryEvaluate(light.Keys, frame, out LightPose pose))
        {
            light.ApplyPose(pose);
        }
    }

    /// <summary>照明の値のうち、キーで動かすもの（手で変えると未登録の変更・自動キーになる）。</summary>
    private static bool IsKeyedLightProperty(string? name) => name is nameof(SceneNode.Position) or nameof(SceneNode.Rotation)
        or nameof(LightNode.Color) or nameof(LightNode.Intensity) or nameof(LightNode.Range) or nameof(LightNode.SpotAngle)
        or nameof(LightNode.SpotSoftness);

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
                or nameof(CameraNode.FieldOfView) or nameof(CameraNode.TargetDistance) or nameof(CameraNode.Orthographic):
                if (_autoKey || camera.Keys.Count > 0)
                {
                    OnUserEdit(camera);
                }
                break;
            case CameraNode camera when e.PropertyName == nameof(CameraNode.Keys):
                Apply(() => ApplyCamera(camera, _playback.CurrentFrame));
                break;
            case LightNode light when IsKeyedLightProperty(e.PropertyName):
                if (_autoKey || light.Keys.Count > 0 || IsDrivenByTime(light))
                {
                    OnUserEdit(light);
                }
                break;
            case LightNode light when e.PropertyName == nameof(LightNode.Keys):
                Apply(() => ApplyLight(light, _playback.CurrentFrame));
                break;
            case ModelDisplayNode display when e.PropertyName is nameof(ModelDisplayNode.Shown) or nameof(ModelDisplayNode.Ik):
                if (_autoKey || display.Keys.Count > 0)
                {
                    OnUserEdit(display);
                }
                break;
            case ModelDisplayNode display when e.PropertyName == nameof(ModelDisplayNode.Keys):
                Apply(() => ApplyDisplay(display, _playback.CurrentFrame));
                break;
            case EffectsNode when e.PropertyName == nameof(EffectsNode.Keys):
                Apply(() => ApplyEffects(_playback.CurrentFrame));
                break;
            case SelfShadowNode when e.PropertyName == nameof(SelfShadowNode.Keys):
                Apply(() => ApplySelfShadow(_playback.CurrentFrame));
                break;
            case TimeOfDayNode time when e.PropertyName == nameof(TimeOfDayNode.Hour):
                if (_autoKey || time.Keys.Count > 0)
                {
                    OnUserEdit(time);
                }
                break;
            case TimeOfDayNode when e.PropertyName == nameof(TimeOfDayNode.Keys):
                Apply(() => ApplyTime(_playback.CurrentFrame));
                break;
        }
    }

    private void OnUserEdit(SceneNode node)
    {
        if (!TracksUserEdits)
        {
            return;
        }
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
        node is BoneNode { HasUnregisteredChange: true } or MorphNode { HasUnregisteredChange: true } or CameraNode { HasUnregisteredChange: true }
            or LightNode { HasUnregisteredChange: true } or ModelDisplayNode { HasUnregisteredChange: true }
            or EffectsNode { HasUnregisteredChange: true } or SelfShadowNode { HasUnregisteredChange: true }
            or TimeOfDayNode { HasUnregisteredChange: true };

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
            case LightNode light:
                light.HasUnregisteredChange = value;
                break;
            case ModelDisplayNode display:
                display.HasUnregisteredChange = value;
                break;
            case EffectsNode effects:
                effects.HasUnregisteredChange = value;
                break;
            case SelfShadowNode shadow:
                shadow.HasUnregisteredChange = value;
                break;
            case TimeOfDayNode time:
                time.HasUnregisteredChange = value;
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
