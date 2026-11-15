namespace Theater3D.App.Shell;

/// <summary>
/// 値を出し直せる設定の窓（影・床の映り込み・環境の画像・霧・映像効果・イラスト風。設計判断記録 78 章の 6 段目）。
/// ライブモードの設定画面は、窓の中身を借りて出したままにするので、元に戻す・シーンの切り替えなど、ほかの所で値が変わったときに出し直す
/// （エディットモードでは、窓を開いている間はほかを操作できないので要らない）。
/// </summary>
internal interface IShowsCurrentValues
{
    /// <summary>設定の今の値を出し直す（値を入れ直す処理は動かさない）。</summary>
    void ShowCurrentValues();
}
