namespace Theater3D.Editor.Tests;

/// <summary>ライブモードのシーンの名前（ファイルの名前になる。設計判断記録 78 章の 4 段目）。</summary>
[TestClass]
public sealed class SceneNamesTests
{
    [TestMethod]
    public void UsableNamesAreTrimmed()
    {
        Assert.AreEqual("自室", SceneNames.Validate("  自室 ").Name);
        Assert.AreEqual("カラオケルーム 2", SceneNames.Validate("カラオケルーム 2").Name);
        Assert.AreEqual("v1.2 の部屋", SceneNames.Validate("v1.2 の部屋").Name, "途中の「.」は使える");
        Assert.AreEqual("CONCERT", SceneNames.Validate("CONCERT").Name, "Windows の名前を含むだけなら使える");
        Assert.IsNull(SceneNames.Validate("自室").Problem);
    }

    [TestMethod]
    public void UnusableNamesSayWhy()
    {
        Assert.AreEqual("名前を入れてください", SceneNames.Validate("   ").Problem);
        Assert.AreEqual("名前を入れてください", SceneNames.Validate(null).Problem);
        foreach (string bad in new[] { "自室/2", "a:b", "何?", "<部屋>", "a\\b", "a|b", "a*b", "\"a\"", "a\tb" })
        {
            (string? name, string? problem) = SceneNames.Validate(bad);
            Assert.IsNull(name, bad);
            Assert.IsNotNull(problem, bad);
        }
        Assert.IsNotNull(SceneNames.Validate("部屋.").Problem, "終わりの「.」");
        Assert.IsNotNull(SceneNames.Validate("con").Problem, "Windows が使う名前（大文字・小文字は問わない）");
        Assert.IsNotNull(SceneNames.Validate("LPT1.old").Problem, "拡張子が付いていても使えない");
        Assert.IsNotNull(SceneNames.Validate(new string('あ', SceneNames.MaxLength + 1)).Problem, "長すぎる");
        Assert.IsNull(SceneNames.Validate(new string('あ', SceneNames.MaxLength)).Problem);
    }
}
