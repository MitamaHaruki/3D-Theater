namespace Plugin.Core.Tests;

[TestClass]
public sealed class PluginManifestTests
{
    private const string Valid = """
        {
          // コメントと、最後のカンマも許す
          "id": "example.scene-info",
          "name": "シーンの情報",
          "version": "1.2.3",
          "host": "3DTheater",
          "sdkVersion": "1.0",
          "assembly": "lib/Example.dll",
          "entryType": "Example.ScenePlugin",
          "description": "説明",
          "author": "作者",
          "requires": ["commands", "panels"],
        }
        """;

    [TestMethod]
    public void ReadsAllFields()
    {
        PluginManifest manifest = PluginManifest.Parse(Valid);
        Assert.AreEqual("example.scene-info", manifest.Id);
        Assert.AreEqual("シーンの情報", manifest.Name);
        Assert.AreEqual(new Version(1, 2, 3), manifest.Version);
        Assert.AreEqual("3DTheater", manifest.Host);
        Assert.AreEqual(new Version(1, 0), manifest.SdkVersion);
        Assert.AreEqual("lib/Example.dll", manifest.Assembly);
        Assert.AreEqual("Example.ScenePlugin", manifest.EntryType);
        Assert.AreEqual("説明", manifest.Description);
        CollectionAssert.AreEqual(new[] { "commands", "panels" }, manifest.Requires.ToArray());
    }

    [TestMethod]
    [DataRow("\"id\": \"example.scene-info\",", "", "id", DisplayName = "id が無い")]
    [DataRow("\"id\": \"example.scene-info\",", "\"id\": \"日本語の id\",", "使えない文字", DisplayName = "id に使えない文字")]
    [DataRow("\"version\": \"1.2.3\",", "\"version\": \"いち\",", "版の形", DisplayName = "版が読めない")]
    [DataRow("\"assembly\": \"lib/Example.dll\",", "\"assembly\": \"../outside.dll\",", "フォルダーの中", DisplayName = "フォルダーの外の DLL")]
    [DataRow("\"assembly\": \"lib/Example.dll\",", "\"assembly\": \"C:/outside.dll\",", "フォルダーの中", DisplayName = "絶対パスの DLL")]
    [DataRow("\"requires\": [\"commands\", \"panels\"],", "\"requires\": \"commands\",", "文字列の並び", DisplayName = "requires が並びでない")]
    [DataRow("\"entryType\": \"Example.ScenePlugin\",", "\"entryType\": 3,", "entryType", DisplayName = "entryType が文字列でない")]
    public void RejectsMissingOrBrokenFields(string original, string replacement, string expected)
    {
        var ex = Assert.ThrowsExactly<PluginManifestException>(() => PluginManifest.Parse(Valid.Replace(original, replacement, StringComparison.Ordinal)));
        StringAssert.Contains(ex.Message, expected);
    }

    [TestMethod]
    public void RejectsTextThatIsNotJson()
    {
        Assert.ThrowsExactly<PluginManifestException>(() => PluginManifest.Parse("{ id: "));
        Assert.ThrowsExactly<PluginManifestException>(() => PluginManifest.Parse("[1, 2]"));
    }

    [TestMethod]
    public void CompatibilityChecksHostSdkAndCapabilities()
    {
        var host = new PluginHostInfo("3DTheater", new Version(1, 2), ["commands", "panels", "scene.read"]);
        PluginManifest manifest = PluginManifest.Parse(Valid);
        Assert.IsNull(host.CheckCompatibility(manifest), "対象・版・機能が合えば使える");
        Assert.IsNull(host.CheckCompatibility(manifest with { Host = "3dtheater" }), "対象の名前は大文字と小文字を問わない");
        StringAssert.Contains(host.CheckCompatibility(manifest with { Host = "DrawingCanvas" }), "DrawingCanvas");
        StringAssert.Contains(host.CheckCompatibility(manifest with { SdkVersion = new Version(2, 0) }), "主の版");
        StringAssert.Contains(host.CheckCompatibility(manifest with { SdkVersion = new Version(1, 3) }), "新しい SDK");
        Assert.IsNull(host.CheckCompatibility(manifest with { SdkVersion = new Version(1, 1) }), "古い副の版は使える");
        StringAssert.Contains(host.CheckCompatibility(manifest with { Requires = ["commands", "export.exr"] }), "export.exr");
    }
}
