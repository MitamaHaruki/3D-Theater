namespace Theater3D.Editor.Tests;

/// <summary>メニューの項目の名前を、ライブモードの設定画面のボタンや見出しに使う形にする。</summary>
[TestClass]
public sealed class MenuTextTests
{
    [TestMethod]
    public void AccessKeysAreRemoved()
    {
        Assert.AreEqual("ファイル", MenuText.Clean("ファイル(_F)"));
        Assert.AreEqual("開く...", MenuText.Clean("開く(_O)..."));
        Assert.AreEqual("名前を付けて保存...", MenuText.Clean("名前を付けて保存(_A)..."));
        Assert.AreEqual("Save As", MenuText.Clean("Save _As"));
        Assert.AreEqual("影の設定...", MenuText.Clean("影の設定..."), "印の無い名前はそのまま");
    }

    [TestMethod]
    public void EscapedUnderscoresStay()
    {
        // プラグインの名前の「_」は、メニューでは「__」と書いてある
        Assert.AreEqual("my_plugin", MenuText.Clean("my__plugin"));
        Assert.AreEqual("a_b(_X)", MenuText.Clean("a__b(__X)"), "「(__X)」はアクセスキーではない");
        Assert.AreEqual(string.Empty, MenuText.Clean(null));
        Assert.AreEqual(string.Empty, MenuText.Clean(string.Empty));
    }
}
