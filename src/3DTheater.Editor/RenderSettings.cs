using System.Numerics;

namespace Theater3D.Editor;

/// <summary>
/// 作品の描き方の設定（プロジェクトに保存する）。セルフシャドウと地面の影の ON / OFF（初期値はどちらも ON。MMD と同じ）と、
/// 映像効果・イラスト風の表現・床の映り込み・霧（初期値はどれもなし）。
/// </summary>
public sealed class RenderSettings
{
    /// <summary>地面の影の色（半透明の黒）。</summary>
    public static readonly Vector4 GroundShadowColor = new(0f, 0f, 0f, 0.5f);

    private bool _selfShadow = true;
    private bool _groundShadow = true;
    private VisualEffects _effects = VisualEffects.None;
    private IllustrationStyle _illustration = IllustrationStyle.None;
    private FloorReflection _reflection = FloorReflection.Off;
    private SceneFog _fog = SceneFog.Off;

    /// <summary>どれかの設定が変わった。</summary>
    public event EventHandler? Changed;

    /// <summary>セルフシャドウ（モデルに落ちる影）。受ける材質は、影の中をトゥーンの影の色で描く（MMD と同じ）。</summary>
    public bool SelfShadow
    {
        get => _selfShadow;
        set => Set(ref _selfShadow, value);
    }

    /// <summary>地面の影（高さ 0 の床に落ちる影）。</summary>
    public bool GroundShadow
    {
        get => _groundShadow;
        set => Set(ref _groundShadow, value);
    }

    /// <summary>映像効果（色調補正・ブルーム・周辺減光）。値は範囲に収めて持つ。</summary>
    public VisualEffects Effects
    {
        get => _effects;
        set
        {
            VisualEffects normalized = (value ?? VisualEffects.None).Normalized();
            if (normalized == _effects)
            {
                return;
            }
            _effects = normalized;
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>イラスト風の表現（線画・塗り・影の色・紙の質感）。値は範囲に収めて持つ。</summary>
    public IllustrationStyle Illustration
    {
        get => _illustration;
        set
        {
            IllustrationStyle normalized = (value ?? IllustrationStyle.None).Normalized();
            if (normalized == _illustration)
            {
                return;
            }
            _illustration = normalized;
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>床の映り込み。値は範囲に収めて持つ。</summary>
    public FloorReflection Reflection
    {
        get => _reflection;
        set
        {
            FloorReflection normalized = (value ?? FloorReflection.Off).Normalized();
            if (normalized == _reflection)
            {
                return;
            }
            _reflection = normalized;
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>霧。値は範囲に収めて持つ。</summary>
    public SceneFog Fog
    {
        get => _fog;
        set
        {
            SceneFog normalized = (value ?? SceneFog.Off).Normalized();
            if (normalized == _fog)
            {
                return;
            }
            _fog = normalized;
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>初期の設定に戻す（新しいプロジェクト）。</summary>
    public void Reset()
    {
        SelfShadow = true;
        GroundShadow = true;
        Effects = VisualEffects.None;
        Illustration = IllustrationStyle.None;
        Reflection = FloorReflection.Off;
        Fog = SceneFog.Off;
    }

    private void Set(ref bool field, bool value)
    {
        if (field == value)
        {
            return;
        }
        field = value;
        Changed?.Invoke(this, EventArgs.Empty);
    }
}
