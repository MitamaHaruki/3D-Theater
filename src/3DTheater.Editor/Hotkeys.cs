using System.Globalization;

namespace Theater3D.Editor;

/// <summary>キーと一緒に押す Ctrl・Alt・Shift・Windows キー（Windows の RegisterHotKey と同じ値）。</summary>
[Flags]
public enum HotkeyModifiers
{
    None = 0,
    Alt = 1,
    Control = 2,
    Shift = 4,
    Windows = 8,
}

/// <summary>キーの組み合わせ（一緒に押すキーと、仮想キーの番号）。</summary>
public readonly record struct HotkeyGesture(HotkeyModifiers Modifiers, int Key)
{
    /// <summary>使えるキーか（<see cref="HotkeyKeys"/> の一覧にあるか）。</summary>
    public bool IsValid => HotkeyKeys.NameOf(Key) is not null;

    /// <summary>Ctrl・Alt・Shift・Windows キーのどれかと組み合わせているか（組み合わせないと、ほかのアプリでそのキーを使えなくなる）。</summary>
    public bool HasModifier => Modifiers != HotkeyModifiers.None;

    /// <summary>「Ctrl+Shift+1」の形（保存にも使う）。</summary>
    public override string ToString()
    {
        var parts = new List<string>(5);
        if (Modifiers.HasFlag(HotkeyModifiers.Control))
        {
            parts.Add("Ctrl");
        }
        if (Modifiers.HasFlag(HotkeyModifiers.Alt))
        {
            parts.Add("Alt");
        }
        if (Modifiers.HasFlag(HotkeyModifiers.Shift))
        {
            parts.Add("Shift");
        }
        if (Modifiers.HasFlag(HotkeyModifiers.Windows))
        {
            parts.Add("Win");
        }
        parts.Add(HotkeyKeys.NameOf(Key) ?? $"#{Key}");
        return string.Join("+", parts);
    }

    /// <summary>「Ctrl+Shift+1」の形を読む（大文字小文字・空白は問わない）。読めなければ null。</summary>
    public static HotkeyGesture? Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }
        string[] parts = [.. text.Split('+', StringSplitOptions.TrimEntries)];
        HotkeyModifiers modifiers = HotkeyModifiers.None;
        for (int i = 0; i < parts.Length - 1; i++)
        {
            HotkeyModifiers? modifier = parts[i].ToUpperInvariant() switch
            {
                "CTRL" or "CONTROL" => HotkeyModifiers.Control,
                "ALT" => HotkeyModifiers.Alt,
                "SHIFT" => HotkeyModifiers.Shift,
                "WIN" or "WINDOWS" => HotkeyModifiers.Windows,
                _ => null,
            };
            if (modifier is not { } m)
            {
                return null;
            }
            modifiers |= m;
        }
        int? key = HotkeyKeys.Find(parts[^1]);
        return key is { } k ? new HotkeyGesture(modifiers, k) : null;
    }
}

/// <summary>使えるキー（仮想キーの番号と名前）。文字・数字・F1〜F24・テンキー・いくつかの操作キー。</summary>
public static class HotkeyKeys
{
    private static readonly Dictionary<int, string> Names = Build();
    private static readonly Dictionary<string, int> Keys = Names.ToDictionary(p => p.Value.ToUpperInvariant(), p => p.Key);

    /// <summary>キーの名前（使えないキーなら null）。</summary>
    public static string? NameOf(int key) => Names.TryGetValue(key, out string? name) ? name : null;

    /// <summary>名前からキーの番号（無ければ null）。</summary>
    public static int? Find(string name) => Keys.TryGetValue(name.Trim().ToUpperInvariant(), out int key) ? key : null;

    private static Dictionary<int, string> Build()
    {
        var names = new Dictionary<int, string>();
        for (char c = 'A'; c <= 'Z'; c++)
        {
            names[c] = c.ToString();
        }
        for (int i = 0; i <= 9; i++)
        {
            names[0x30 + i] = i.ToString(CultureInfo.InvariantCulture);
            names[0x60 + i] = $"テンキー{i}";
        }
        for (int i = 1; i <= 24; i++)
        {
            names[0x6F + i] = $"F{i}";
        }
        names[0x6A] = "テンキー*";
        names[0x6B] = "テンキー+";
        names[0x6D] = "テンキー-";
        names[0x6E] = "テンキー.";
        names[0x6F] = "テンキー/";
        names[0x20] = "Space";
        names[0x2D] = "Insert";
        names[0x2E] = "Delete";
        names[0x24] = "Home";
        names[0x23] = "End";
        names[0x21] = "PageUp";
        names[0x22] = "PageDown";
        names[0x25] = "Left";
        names[0x26] = "Up";
        names[0x27] = "Right";
        names[0x28] = "Down";
        return names;
    }
}

/// <summary>キーで動かすもの。</summary>
public enum HotkeyAction
{
    /// <summary>表情のプリセットを、キャラクターに出す。</summary>
    Expression,

    /// <summary>モデル（小物）を出す・隠す。</summary>
    ModelVisibility,

    /// <summary>モデルのモーフを 1 つ、最大にする。</summary>
    Morph,

    /// <summary>顔のトラッキングを始める・止める（前に使った映像で）。</summary>
    TrackingToggle,

    /// <summary>顔のトラッキングの「正面にする」。</summary>
    TrackingCalibrate,

    /// <summary>仮想カメラを入れる・切る。</summary>
    VirtualCameraToggle,

    /// <summary>カメラを切り替える（プレビューと、配信用の画面・仮想カメラの両方に映す。設計判断記録 78 章の 7 段目）。</summary>
    CameraSwitch,
}

/// <summary>キーの押し方。</summary>
public enum HotkeyMode
{
    /// <summary>押すたびに入・切。</summary>
    Toggle,

    /// <summary>押している間だけ。</summary>
    Hold,
}

/// <summary>キーの説明と決まり。</summary>
public static class HotkeyActions
{
    public static IReadOnlyList<HotkeyAction> All { get; } = Enum.GetValues<HotkeyAction>();

    public static string DisplayName(HotkeyAction action) => action switch
    {
        HotkeyAction.Expression => "表情",
        HotkeyAction.ModelVisibility => "小物の表示",
        HotkeyAction.Morph => "モーフ",
        HotkeyAction.TrackingToggle => "トラッキングの開始・停止",
        HotkeyAction.TrackingCalibrate => "トラッキングの「正面にする」",
        HotkeyAction.VirtualCameraToggle => "仮想カメラの入・切",
        HotkeyAction.CameraSwitch => "カメラの切り替え",
        _ => action.ToString(),
    };

    /// <summary>押し方（入・切 / 押している間）を選べるか。トラッキング・仮想カメラは押したときに 1 回動くだけ。</summary>
    public static bool HasMode(HotkeyAction action) => action is HotkeyAction.Expression or HotkeyAction.ModelVisibility or HotkeyAction.Morph;

    /// <summary>モデルを選ぶか。</summary>
    public static bool UsesModel(HotkeyAction action) => action is HotkeyAction.Expression or HotkeyAction.ModelVisibility or HotkeyAction.Morph;

    /// <summary>名前（表情のプリセット・モーフ）を選ぶか。</summary>
    public static bool UsesName(HotkeyAction action) => action is HotkeyAction.Expression or HotkeyAction.Morph;

    /// <summary>カメラを選ぶか（<see cref="HotkeyBinding.Model"/> にカメラの Id を持つ。null ならライブカメラ）。</summary>
    public static bool UsesCamera(HotkeyAction action) => action is HotkeyAction.CameraSwitch;

    public static string DisplayName(HotkeyMode mode) => mode == HotkeyMode.Hold ? "押している間だけ" : "押すたびに入・切";
}

/// <summary>
/// キー 1 つの決まり。<see cref="Model"/> はシーンのモデルの Id（表情は null ならトラッキングのキャラクター。
/// カメラの切り替えはカメラの Id で、null ならライブカメラ）、<see cref="Name"/> は表情のプリセットかモーフの名前。
/// </summary>
public sealed record HotkeyBinding(Guid Id, HotkeyGesture? Gesture, HotkeyAction Action, HotkeyMode Mode, Guid? Model, string? Name)
{
    public static HotkeyBinding Create(HotkeyAction action = HotkeyAction.Expression) => new(Guid.NewGuid(), null, action, HotkeyMode.Toggle, null, null);

    /// <summary>押し方（押し方を選べないものは、いつも 1 回動くだけ）。</summary>
    public HotkeyMode EffectiveMode => HotkeyActions.HasMode(Action) ? Mode : HotkeyMode.Toggle;
}

/// <summary>キー操作の一覧（ライブのファイルに保存する）。</summary>
public sealed class HotkeySettings
{
    public const int MaxBindings = 64;

    private readonly List<HotkeyBinding> _bindings = [];

    /// <summary>足した・消した・変えた。</summary>
    public event EventHandler? Changed;

    public IReadOnlyList<HotkeyBinding> Bindings => _bindings;

    public HotkeyBinding? Find(Guid id) => _bindings.Find(b => b.Id == id);

    public bool Add(HotkeyBinding binding)
    {
        if (_bindings.Count >= MaxBindings || Find(binding.Id) is not null)
        {
            return false;
        }
        _bindings.Add(binding);
        Changed?.Invoke(this, EventArgs.Empty);
        return true;
    }

    /// <summary>同じ Id のものを置き換える（無ければ何もしない）。</summary>
    public void Replace(HotkeyBinding binding)
    {
        int index = _bindings.FindIndex(b => b.Id == binding.Id);
        if (index < 0 || _bindings[index] == binding)
        {
            return;
        }
        _bindings[index] = binding;
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void Remove(Guid id)
    {
        if (_bindings.RemoveAll(b => b.Id == id) > 0)
        {
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }

    public void SetAll(IEnumerable<HotkeyBinding> bindings)
    {
        _bindings.Clear();
        _bindings.AddRange(bindings.Take(MaxBindings));
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void Reset() => SetAll([]);

    /// <summary>ほかのキーと同じ組み合わせか（先にあるほうだけが効く）。</summary>
    public bool IsDuplicate(HotkeyBinding binding) =>
        binding.Gesture is { } gesture && _bindings.TakeWhile(b => b.Id != binding.Id).Any(b => b.Gesture == gesture);
}

/// <summary>
/// キーで出した表情・モーフ・小物の表示の、今の入・切となめらかな出し入れ（<see cref="FadeSeconds"/> で 0 ⇔ 1）。
/// 表情は、同じキャラクターでは 1 つだけを入れたままにする（入・切の表情を入れると、ほかの入・切の表情は切る）。
/// </summary>
public sealed class HotkeyState
{
    public const float FadeSeconds = 0.2f;

    private readonly Dictionary<Guid, (bool On, float Strength)> _entries = [];

    /// <summary>入っているか。</summary>
    public bool IsOn(Guid id) => _entries.TryGetValue(id, out var entry) && entry.On;

    /// <summary>今の強さ（0〜1。出し入れの途中も）。</summary>
    public float Strength(Guid id) => _entries.TryGetValue(id, out var entry) ? entry.Strength : 0f;

    /// <summary>強さが 0 より大きいもの（出し入れの途中も）。</summary>
    public IEnumerable<(Guid Id, float Strength)> Visible => _entries.Where(p => p.Value.Strength > 0).Select(p => (p.Key, p.Value.Strength));

    /// <summary>入れる・切る。<paramref name="instant"/> なら、なめらかにせずにすぐ変える（小物の表示）。</summary>
    public void Set(Guid id, bool on, bool instant = false)
    {
        float strength = instant ? (on ? 1f : 0f) : Strength(id);
        _entries[id] = (on, strength);
    }

    public void Clear() => _entries.Clear();

    /// <summary>時間を進める。強さが変わったら true。</summary>
    public bool Update(double seconds)
    {
        if (seconds <= 0)
        {
            return false;
        }
        float step = (float)(seconds / FadeSeconds);
        bool changed = false;
        foreach (Guid id in _entries.Keys.ToArray())
        {
            var (on, strength) = _entries[id];
            float next = on ? Math.Min(1f, strength + step) : Math.Max(0f, strength - step);
            if (next != strength)
            {
                changed = true;
            }
            if (!on && next == 0)
            {
                _entries.Remove(id);
            }
            else
            {
                _entries[id] = (on, next);
            }
        }
        return changed;
    }
}

/// <summary>
/// キーで出した表情・モーフを重ねる計算（利用者と相談して「表情を優先」に決めた）。
/// 表情が使うモーフ（プリセットで 0 より大きいもの）は、強さの分だけ表情の値に寄せる（トラッキングやキーの値より優先）。
/// プリセットで 0 のモーフは使わない（口などは、トラッキングのまま動く）。
/// </summary>
public static class LiveExpressions
{
    /// <summary>表情が「使う」モーフとみなす値。</summary>
    public const float UsedThreshold = 0.001f;

    /// <summary>
    /// 出している表情・モーフ（モーフの名前 → 値と、強さ）を重ねて、モーフの名前ごとに（値, 強さ）を返す。
    /// 同じモーフを複数が使うときは、値 × 強さ が大きいほう。
    /// </summary>
    public static Dictionary<string, (float Value, float Strength)> Compose(IEnumerable<(IReadOnlyDictionary<string, float> Values, float Strength)> parts)
    {
        var result = new Dictionary<string, (float Value, float Strength)>(StringComparer.Ordinal);
        foreach (var (values, strength) in parts)
        {
            if (strength <= 0)
            {
                continue;
            }
            float s = Math.Min(strength, 1f);
            foreach (var (name, value) in values)
            {
                if (value <= UsedThreshold)
                {
                    continue;
                }
                if (!result.TryGetValue(name, out var current) || value * s > current.Value * current.Strength)
                {
                    result[name] = (Math.Clamp(value, 0f, 1f), s);
                }
            }
        }
        return result;
    }

    /// <summary>
    /// トラッキングのまばたき・ウィンクを弱める割合（0〜1）。目のモーフを使う表情（「笑い」など目を閉じる表情）を出している間は、
    /// その強さの分だけまばたきを止める。
    /// </summary>
    public static float EyeSuppression(IEnumerable<(IReadOnlyDictionary<string, float> Values, float Strength)> parts, Func<string, bool> isEyeMorph)
    {
        float suppression = 0;
        foreach (var (values, strength) in parts)
        {
            if (values.Any(p => p.Value > UsedThreshold && isEyeMorph(p.Key)))
            {
                suppression = Math.Max(suppression, Math.Clamp(strength, 0f, 1f));
            }
        }
        return suppression;
    }

    /// <summary>重ねた後の値（今の値を、強さの分だけ表情の値に寄せる）。</summary>
    public static float Blend(float current, float value, float strength) => current + ((value - current) * Math.Clamp(strength, 0f, 1f));
}
