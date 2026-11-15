using Theater3D.App.Logging;
using Theater3D.Domain;
using Theater3D.Editor;

namespace Theater3D.App.Live;

/// <summary>描く直前のポーズに重ねる、キーで出した表情・モーフ（モーフの番号・値・強さ）。トラッキングより後に重ね、表情を優先する。</summary>
public sealed record ExpressionOverlay(IReadOnlyList<(int Morph, float Value, float Strength)> Morphs)
{
    /// <summary>ポーズの入力に重ねる（今の値を、強さの分だけ表情の値に寄せる）。</summary>
    public void ApplyTo(Animation.PoseInput input)
    {
        foreach (var (morph, value, strength) in Morphs)
        {
            if ((uint)morph < (uint)input.MorphWeights.Length)
            {
                input.MorphWeights[morph] = LiveExpressions.Blend(input.MorphWeights[morph], value, strength);
            }
        }
    }
}

/// <summary>
/// ライブモードのキー操作。<see cref="HotkeySettings"/> のキーを Windows に登録し（ほかのアプリを使っている間も効く）、押されたら動かす。
/// - 表情・モーフ: 場面の値は書き換えず、描く直前のポーズに重ねる（<see cref="ExpressionOverlay"/>。0.2 秒でなめらかに出し入れ）。
///   表情は同じキャラクターでは 1 つだけを入れたままにする。目を閉じる表情の間は、トラッキングのまばたき・ウィンクを止める。
/// - 小物の表示: 場面の「表示」は書き換えず、その場だけ入れ替える（保存しない。元に戻すの履歴にも入れない）。
/// - トラッキングの開始・停止（前に使った映像で）・「正面にする」・仮想カメラの入・切: 押したときに 1 回動かす。
/// - カメラの切り替え: 押したときに、プレビューと配信（配信用の画面・仮想カメラ）の両方を、決めたカメラにする
///   （キーは配信の最中にわざわざ押すものなので、カメラ情報の「配信用画面に適用」を待たない。設計判断記録 78 章の 7 段目）。
/// </summary>
public sealed class HotkeyService : IDisposable
{
    private readonly EditorContext _context;
    private readonly HotkeySettings _settings;
    private readonly HotkeyState _state = new();
    private readonly GlobalHotkeys _keys = new();
    private readonly Dictionary<int, Guid> _ids = [];
    private readonly Dictionary<Guid, string> _status = [];
    private readonly HashSet<Guid> _overlaid = [];
    private int _suspended;
    private bool _dirty;
    private bool _disposed;

    internal HotkeyService(EditorContext context)
    {
        _context = context;
        _settings = context.LiveHotkeys;
        _keys.Pressed += (_, id) => OnPressed(id);
        _keys.Released += (_, id) => OnReleased(id);
        _settings.Changed += (_, _) =>
        {
            ForgetRemoved();
            RegisterAll();
        };
        context.MorphPresets.Changed += (_, _) => Refresh();
        context.Scene.NodeChanged += (_, e) =>
        {
            if ((e.Node == context.Scene.ModelsGroup || e.Node == context.Scene.CamerasGroup) && e.PropertyName == nameof(SceneNode.Children))
            {
                Refresh(); // モデル・カメラを足した・外した
            }
        };
        context.RenderLoop.Ticking += (_, step) => OnTick(step);
    }

    /// <summary>登録の様子・入っているものが変わった（画面のスレッドで来る）。</summary>
    public event EventHandler? StateChanged;

    /// <summary>知らせたいこと（状態表示に出す。true ならうまくいかなかった）。</summary>
    public event EventHandler<(string Text, bool IsError)>? Notice;

    /// <summary>キーの様子（「効きます」・「ほかのアプリが同じキーを使っています」など）。</summary>
    public string StatusOf(Guid id) => _status.TryGetValue(id, out string? status) ? status : string.Empty;

    /// <summary>表情・モーフ・小物の表示を入れているか。</summary>
    public bool IsOn(Guid id) => _state.IsOn(id);

    /// <summary>キーを決めている間は、全部のキーを外す（決めようとして押したキーが、ここで受け取られないように）。</summary>
    public void Suspend()
    {
        if (_suspended++ == 0)
        {
            RegisterAll();
        }
    }

    public void Resume()
    {
        if (_suspended > 0 && --_suspended == 0)
        {
            RegisterAll();
        }
    }

    /// <summary>キーを押したのと同じに動かす（パネルの「試す」。押している間だけのものは、少しの間だけ出す）。</summary>
    public void Try(Guid id)
    {
        if (_settings.Find(id) is not { } binding)
        {
            return;
        }
        Press(binding);
        if (binding.EffectiveMode == HotkeyMode.Hold)
        {
            var timer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(1.5) };
            timer.Tick += (_, _) =>
            {
                timer.Stop();
                if (_settings.Find(id) is { } still)
                {
                    Release(still);
                }
            };
            timer.Start();
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;
        _keys.Dispose();
    }

    // ===== 登録 =====
    private void RegisterAll()
    {
        if (_disposed)
        {
            return;
        }
        _keys.UnregisterAll();
        _ids.Clear();
        _status.Clear();
        int next = 1;
        foreach (HotkeyBinding binding in _settings.Bindings)
        {
            if (binding.Gesture is not { IsValid: true } gesture)
            {
                _status[binding.Id] = "キーを決めてください";
                continue;
            }
            if (_suspended > 0)
            {
                _status[binding.Id] = "キーを決めている間は止めています";
                continue;
            }
            if (_settings.IsDuplicate(binding))
            {
                _status[binding.Id] = "同じキーが上にもあります（上のほうだけが効きます）";
                continue;
            }
            int id = next++;
            if (_keys.Register(id, gesture))
            {
                _ids[id] = binding.Id;
                _status[binding.Id] = Problem(binding) ?? (gesture.HasModifier ? "効きます" : "効きます（Ctrl・Alt・Shift と組み合わせると、ほかのアプリでこのキーを使えます）");
            }
            else
            {
                _status[binding.Id] = "ほかのアプリが同じキーを使っています（別のキーにしてください）";
                AppLog.Warning($"キー {gesture} は、ほかのアプリが使っているため登録できませんでした");
            }
        }
        _dirty = true;
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    // 登録はそのままで、様子（モデル・プリセットが見つかるか）だけを決め直す
    private void Refresh()
    {
        foreach (int id in _ids.Keys)
        {
            if (_settings.Find(_ids[id]) is { Gesture: { } gesture } binding)
            {
                _status[binding.Id] = Problem(binding) ?? (gesture.HasModifier ? "効きます" : "効きます（Ctrl・Alt・Shift と組み合わせると、ほかのアプリでこのキーを使えます）");
            }
        }
        _dirty = true;
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    // 動かす先が見つからなければ、その説明
    private string? Problem(HotkeyBinding binding)
    {
        switch (binding.Action)
        {
            case HotkeyAction.Expression:
                if (binding.Name is null)
                {
                    return "表情のプリセットを選んでください";
                }
                if (_context.MorphPresets.Find(binding.Name) is null)
                {
                    return $"表情のプリセット「{binding.Name}」がありません";
                }
                return ResolveModel(binding) is null ? "表情を出すモデルがありません" : null;
            case HotkeyAction.ModelVisibility:
                return ResolveModel(binding) is null ? "モデルを選んでください" : null;
            case HotkeyAction.Morph:
                if (ResolveModel(binding) is not { } model)
                {
                    return "モデルを選んでください";
                }
                if (binding.Name is null)
                {
                    return "モーフを選んでください";
                }
                return model.Morphs.Any(m => m.Name == binding.Name) ? null : $"モーフ「{binding.Name}」がありません";
            case HotkeyAction.CameraSwitch:
                return ResolveCamera(binding) is null ? "カメラを選んでください" : null;
            default:
                return null;
        }
    }

    // ===== 押した・離した =====
    private void OnPressed(int id)
    {
        if (!_ids.TryGetValue(id, out Guid bindingId) || _settings.Find(bindingId) is not { } binding)
        {
            return;
        }
        Press(binding);
        if (binding.EffectiveMode == HotkeyMode.Hold)
        {
            _keys.WatchRelease(id);
        }
    }

    private void OnReleased(int id)
    {
        if (_ids.TryGetValue(id, out Guid bindingId) && _settings.Find(bindingId) is { } binding)
        {
            Release(binding);
        }
    }

    private void Press(HotkeyBinding binding)
    {
        string key = binding.Gesture?.ToString() ?? "（試す）";
        switch (binding.Action)
        {
            case HotkeyAction.Expression:
            case HotkeyAction.Morph:
            case HotkeyAction.ModelVisibility:
                bool on = binding.EffectiveMode == HotkeyMode.Hold || !_state.IsOn(binding.Id);
                if (on && binding.Action == HotkeyAction.Expression && binding.EffectiveMode == HotkeyMode.Toggle)
                {
                    // 同じキャラクターの、ほかの入・切の表情は切る（表情を切り替える）
                    Guid? model = ResolveModel(binding)?.Id;
                    foreach (HotkeyBinding other in _settings.Bindings)
                    {
                        if (other.Id != binding.Id && other.Action == HotkeyAction.Expression && other.EffectiveMode == HotkeyMode.Toggle
                            && _state.IsOn(other.Id) && ResolveModel(other)?.Id == model)
                        {
                            _state.Set(other.Id, false);
                        }
                    }
                }
                _state.Set(binding.Id, on, instant: binding.Action == HotkeyAction.ModelVisibility);
                AppLog.Info($"キー {key}: {Describe(binding)}を{(on ? "出しました" : "戻しました")}");
                break;
            case HotkeyAction.TrackingToggle:
                ToggleTracking();
                break;
            case HotkeyAction.TrackingCalibrate:
                if (_context.Tracking?.IsRunning == true && _context.TrackingDriver is { } driver)
                {
                    driver.RequestCalibration();
                    Notice?.Invoke(this, ("今の顔を正面として覚え直します（カメラの方をまっすぐ見てください）", false));
                }
                else
                {
                    Notice?.Invoke(this, ("トラッキングを始めてから「正面にする」を使ってください", true));
                }
                break;
            case HotkeyAction.VirtualCameraToggle:
                _ = ToggleVirtualCameraAsync();
                break;
            case HotkeyAction.CameraSwitch:
                SwitchCamera(binding, key);
                break;
        }
        _dirty = true;
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    private void Release(HotkeyBinding binding)
    {
        if (binding.EffectiveMode != HotkeyMode.Hold || !_state.IsOn(binding.Id))
        {
            return;
        }
        _state.Set(binding.Id, false, instant: binding.Action == HotkeyAction.ModelVisibility);
        AppLog.Info($"キー {binding.Gesture?.ToString() ?? "（試す）"}: {Describe(binding)}を戻しました（キーを離した）");
        _dirty = true;
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    private void ToggleTracking()
    {
        if (_context.Tracking is not { } tracking)
        {
            return;
        }
        if (tracking.IsRunning)
        {
            tracking.Stop();
            Notice?.Invoke(this, ("顔のトラッキングを止めました", false));
        }
        else if (tracking.Restart())
        {
            Notice?.Invoke(this, ($"顔のトラッキングを始めました（{tracking.SourceName}）", false));
        }
        else
        {
            Notice?.Invoke(this, ("トラッキングのパネルで、一度映像を選んで始めてください（その映像で始め直します）", true));
        }
    }

    private async Task ToggleVirtualCameraAsync()
    {
        if (_context.VirtualCamera is not { IsBusy: false } camera)
        {
            return;
        }
        try
        {
            if (camera.IsOn)
            {
                await camera.StopAsync();
                Notice?.Invoke(this, ("仮想カメラをしまいました", false));
            }
            else if (VirtualCameraService.IsSupported && VirtualCameraSetup.Check() == VirtualCameraRegistration.Registered)
            {
                await camera.StartAsync();
                Notice?.Invoke(this, ("仮想カメラを出しました", false));
            }
            else
            {
                // 登録には管理者の確認が要るので、配信の途中で窓を出さない
                Notice?.Invoke(this, ("仮想カメラは、ライブのメニューから一度入れてください（初めは管理者の確認が要ります）", true));
            }
        }
        catch (Exception ex) when (ex is System.IO.IOException or Media.VideoException or InvalidOperationException)
        {
            Notice?.Invoke(this, ($"仮想カメラを出せませんでした: {ex.Message}", true));
        }
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    // プレビューと配信の両方を、決めたカメラにする
    private void SwitchCamera(HotkeyBinding binding, string key)
    {
        if (ResolveCamera(binding) is not { } camera)
        {
            Notice?.Invoke(this, ("切り替えるカメラが見つかりません（設定画面の「キー操作」で、カメラを選び直してください）", true));
            return;
        }
        Guid? id = ReferenceEquals(camera, _context.Scene.MainCamera) ? null : camera.Id;
        _context.LiveCameras.Preview = id;
        _context.LiveCameras.OnAir = id;
        AppLog.Info($"キー {key}: カメラを「{camera.Name}」に切り替えました（プレビューと配信）");
        Notice?.Invoke(this, ($"カメラを「{camera.Name}」に切り替えました", false));
    }

    // カメラの切り替えのカメラ（null ならライブカメラ。外したカメラなら null）
    private CameraNode? ResolveCamera(HotkeyBinding binding) =>
        binding.Model is { } id ? _context.Scene.AddedCameras.FirstOrDefault(c => c.Id == id) : _context.Scene.MainCamera;

    private string Describe(HotkeyBinding binding)
    {
        string target = ResolveModel(binding)?.Name ?? "（モデルなし）";
        return binding.Action switch
        {
            HotkeyAction.Expression => $"表情「{binding.Name}」（{target}）",
            HotkeyAction.Morph => $"モーフ「{binding.Name}」（{target}）",
            HotkeyAction.ModelVisibility => $"「{target}」の表示の入れ替え",
            _ => HotkeyActions.DisplayName(binding.Action),
        };
    }

    // 表情でモデルを決めていなければ、トラッキングのキャラクター（決めたモデルか、頭のボーンを持つ最初のモデル）
    private ModelNode? ResolveModel(HotkeyBinding binding)
    {
        ModelNode[] models = [.. _context.Scene.ModelsGroup.Children.OfType<ModelNode>()];
        if (binding.Model is { } id)
        {
            return models.FirstOrDefault(m => m.Id == id);
        }
        if (binding.Action != HotkeyAction.Expression)
        {
            return null;
        }
        return (_context.LiveTracking.TargetModelId is { } target ? models.FirstOrDefault(m => m.Id == target) : null)
            ?? models.FirstOrDefault(m => FaceRigNames.Find([.. m.Bones.Select(b => b.Name)], FaceRigNames.Head) >= 0);
    }

    // 消したキーの、入れたままの状態を外す
    private void ForgetRemoved()
    {
        foreach (var (id, _) in _state.Visible.ToArray())
        {
            if (_settings.Find(id) is null)
            {
                _state.Set(id, false, instant: true);
            }
        }
    }

    // ===== 当てはめ =====
    private void OnTick(TimeSpan step)
    {
        if (_state.Update(step.TotalSeconds) || _dirty)
        {
            Apply();
        }
    }

    private void Apply()
    {
        _dirty = false;
        var flipped = new HashSet<Guid>();
        var parts = new Dictionary<Guid, List<(IReadOnlyDictionary<string, float> Values, float Strength)>>();
        foreach (var (id, strength) in _state.Visible)
        {
            if (_settings.Find(id) is not { } binding || ResolveModel(binding) is not { } model)
            {
                continue;
            }
            switch (binding.Action)
            {
                case HotkeyAction.ModelVisibility:
                    flipped.Add(model.Id);
                    break;
                case HotkeyAction.Expression when binding.Name is { } presetName && _context.MorphPresets.Find(presetName) is { } preset:
                    Add(model.Id, preset.Values, strength);
                    break;
                case HotkeyAction.Morph when binding.Name is { } morphName:
                    Add(model.Id, new Dictionary<string, float> { [morphName] = 1f }, strength);
                    break;
            }
        }

        bool changed = false;
        foreach (ModelNode node in _context.Scene.ModelsGroup.Children.OfType<ModelNode>())
        {
            if (_context.GetLoaded(node) is not { } loaded)
            {
                continue;
            }
            bool flip = flipped.Contains(node.Id);
            if (loaded.VisibilityFlipped != flip)
            {
                loaded.VisibilityFlipped = flip;
                changed = true;
            }
            if (parts.TryGetValue(node.Id, out var list))
            {
                loaded.ExpressionOverlay = Build(node, list, out float eyeSuppression);
                loaded.TrackingEyeScale = 1f - eyeSuppression;
                loaded.IsPoseDirty = true;
                _overlaid.Add(node.Id);
                changed = true;
            }
            else if (_overlaid.Remove(node.Id))
            {
                loaded.ExpressionOverlay = null;
                loaded.TrackingEyeScale = 1f;
                loaded.IsPoseDirty = true;
                changed = true;
            }
        }
        if (changed)
        {
            _context.RenderLoop.InvalidateAll();
        }

        void Add(Guid model, IReadOnlyDictionary<string, float> values, float strength)
        {
            if (!parts.TryGetValue(model, out var list))
            {
                parts[model] = list = [];
            }
            list.Add((values, strength));
        }
    }

    private static ExpressionOverlay? Build(ModelNode model, List<(IReadOnlyDictionary<string, float> Values, float Strength)> parts, out float eyeSuppression)
    {
        Dictionary<string, (float Value, float Strength)> composed = LiveExpressions.Compose(parts);
        var eyeNames = model.Morphs.Where(m => m.Category == MorphCategory.Eye).Select(m => m.Name).ToHashSet(StringComparer.Ordinal);
        eyeSuppression = LiveExpressions.EyeSuppression(parts, eyeNames.Contains);
        var entries = new List<(int, float, float)>();
        foreach (MorphNode morph in model.Morphs)
        {
            if (composed.TryGetValue(morph.Name, out var part))
            {
                entries.Add((morph.Index, part.Value, part.Strength));
            }
        }
        return entries.Count > 0 ? new ExpressionOverlay(entries) : null;
    }
}
