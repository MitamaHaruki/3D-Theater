using System.Numerics;

namespace Theater3D.Editor;

/// <summary>
/// 作品の描き方の設定（プロジェクトに保存する）。今はセルフシャドウと地面の影の ON / OFF。初期値はどちらも ON（MMD と同じ）。
/// </summary>
public sealed class RenderSettings
{
    /// <summary>地面の影の色（半透明の黒）。</summary>
    public static readonly Vector4 GroundShadowColor = new(0f, 0f, 0f, 0.5f);

    private bool _selfShadow = true;
    private bool _groundShadow = true;

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

    /// <summary>初期の設定に戻す（新しいプロジェクト）。</summary>
    public void Reset()
    {
        SelfShadow = true;
        GroundShadow = true;
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
