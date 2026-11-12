using Theater3D.Formats;

namespace Theater3D.Editor.Tests;

/// <summary>エディットモードとライブモードの分け方（拡張子・起動のときのモードの決め方・ファイルの関連付け）。</summary>
[TestClass]
public sealed class AppModeTests
{
    [TestMethod]
    public void ModeIsDecidedByTheExtension()
    {
        Assert.AreEqual(AppMode.Edit, AppModes.ModeOfFile(@"C:\作品\踊り.3dtproj"));
        Assert.AreEqual(AppMode.Live, AppModes.ModeOfFile(@"C:\配信\部屋.3dtlive"));
        Assert.AreEqual(AppMode.Live, AppModes.ModeOfFile(@"C:\配信\部屋.3DTLIVE"), "大文字でも同じ");
        Assert.IsNull(AppModes.ModeOfFile(@"C:\モデル\ミク.pmx"));
        Assert.IsNull(AppModes.ModeOfFile(@"C:\配信\部屋"));

        Assert.AreEqual(".3dtproj", AppModes.Extension(AppMode.Edit));
        Assert.AreEqual(".3dtlive", AppModes.Extension(AppMode.Live));
        StringAssert.Contains(AppModes.FileFilter(AppMode.Live), "*.3dtlive");
        Assert.DoesNotContain("*.3dtproj", AppModes.FileFilter(AppMode.Live), "ライブの画面では、エディットのファイルを並べない");
        StringAssert.Contains(AppModes.AnyFileFilter, "*.3dtproj;*.3dtlive");
    }

    [TestMethod]
    public void StartupModeComesFromTheFileOrTheOption()
    {
        var none = AppModes.Decide([], []);
        Assert.IsNull(none.Mode, "何も無ければ選んでもらう");
        Assert.IsNull(none.Note);
        Assert.IsNull(AppModes.Decide([@"C:\モデル\ミク.pmx"], ["--safe-mode"]).Mode, "モデルだけなら選んでもらう");
        Assert.AreEqual(AppMode.Live, AppModes.Decide([], ["--live"]).Mode);
        Assert.AreEqual(AppMode.Edit, AppModes.Decide([@"C:\モデル\ミク.pmx"], ["--EDIT"]).Mode, "大文字でも同じ");
        Assert.AreEqual(AppMode.Live, AppModes.Decide([@"C:\配信\部屋.3dtlive"], []).Mode, "ダブルクリックで開いたファイル");
        Assert.AreEqual(AppMode.Edit, AppModes.Decide([@"C:\モデル\ミク.pmx", @"C:\作品\a.3dtproj", @"C:\配信\b.3dtlive"], []).Mode,
            "プロジェクトファイルが 2 つあれば、先のもの");
    }

    [TestMethod]
    public void FileWinsOverAConflictingOption()
    {
        var (mode, note) = AppModes.Decide([@"C:\配信\部屋.3dtlive"], ["--edit"]);
        Assert.AreEqual(AppMode.Live, mode);
        Assert.IsNotNull(note);
        StringAssert.Contains(note, "--edit");
        StringAssert.Contains(note, "部屋.3dtlive");

        Assert.IsNull(AppModes.Decide([@"C:\配信\部屋.3dtlive"], ["--live"]).Note, "食い違わなければ知らせない");
    }

    [TestMethod]
    public void LiveFilesRecordTheirMode()
    {
        var live = new ProjectFile { Mode = AppModes.LiveName };
        string json = ProjectSerializer.Serialize(live);
        StringAssert.Contains(json, "\"mode\":\"live\"");
        ProjectFile? read = ProjectSerializer.Deserialize(json, new LoadReport());
        Assert.IsNotNull(read);
        Assert.AreEqual(AppMode.Live, AppModes.ModeOfProject(read));

        string edit = ProjectSerializer.Serialize(new ProjectFile());
        Assert.DoesNotContain("\"mode\"", edit, "エディットは今までと同じ中身（書かない）");
        Assert.AreEqual(AppMode.Edit, AppModes.ModeOfProject(ProjectSerializer.Deserialize(edit, new LoadReport())!));
        Assert.AreEqual(AppMode.Live, AppModes.ModeOfProject(new ProjectFile { Mode = "LIVE" }));
    }

    [TestMethod]
    public void AssociationEntriesOpenEachTypeWithTheApp()
    {
        const string exe = @"C:\Program Files\3D Theater\3DTheater.exe";
        IReadOnlyList<RegistryValue> entries = FileAssociations.Entries(exe);
        Assert.HasCount(10, entries);

        foreach (var (extension, progId, description) in FileAssociations.Types)
        {
            CollectionAssert.Contains(entries.ToList(), new RegistryValue(extension, null, progId), "拡張子 → 種類");
            CollectionAssert.Contains(entries.ToList(), new RegistryValue($@"{extension}\OpenWithProgids", progId, string.Empty));
            CollectionAssert.Contains(entries.ToList(), new RegistryValue(progId, null, description));
            CollectionAssert.Contains(entries.ToList(), new RegistryValue($@"{progId}\shell\open\command", null, "\"C:\\Program Files\\3D Theater\\3DTheater.exe\" \"%1\""),
                "空白のある場所でも渡せるよう、アプリとファイルを引用符で囲む");
        }
        CollectionAssert.AreEquivalent(new[] { ".3dtproj", ".3dtlive" }, FileAssociations.Types.Select(t => t.Extension).ToArray());
        Assert.AreEqual(2, FileAssociations.Types.Select(t => t.ProgId).Distinct().Count(), "モードごとに別の種類");
        Assert.IsTrue(entries.All(e => !e.Key.StartsWith('\\')), "場所は Software\\Classes から見た相対");
    }
}
