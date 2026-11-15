using Theater3D.Domain;

namespace Theater3D.App.Live;

/// <summary>
/// サイドバーの「キャラクター」「背景」「照明」から呼ぶ、メインの窓の操作（設計判断記録 78 章の 2 段目）。
/// 読み込み・外す・待機モーションなどは、メニューと同じ処理（メインの窓）を使う。
/// </summary>
internal interface ILiveSceneActions
{
    /// <summary>キャラクターを読み込む（PMX。いれば、確かめてから入れ替える）。</summary>
    Task LoadCharacterAsync();

    /// <summary>背景のモデルを、キャラクターにする（今のキャラクターは背景になる）。</summary>
    void MakeCharacter(ModelNode model);

    /// <summary>背景のモデル・アクセサリを読み込む（PMX はトラッキングしないサブキャラクター）。</summary>
    Task LoadBackgroundAsync();

    /// <summary>モデルをシーンから外す（Ctrl+Z で戻せる）。</summary>
    void RemoveModel(ModelNode model);

    /// <summary>モデルに待機モーション・ポーズを読み込む。</summary>
    Task LoadIdleMotionAsync(ModelNode model);

    /// <summary>モデルの待機モーションを外す。</summary>
    void RemoveIdleMotion(ModelNode model);

    /// <summary>モデルの材質の設定を開く。</summary>
    void OpenMaterials(ModelNode model);

    /// <summary>物理をリセットする（剛体を今の姿勢に置き直す）。</summary>
    void ResetPhysics();

    /// <summary>照明を足す。</summary>
    void AddLight(LightType type);

    /// <summary>照明を外す（MMD の照明は外せない）。</summary>
    void RemoveLight(LightNode light);
}
