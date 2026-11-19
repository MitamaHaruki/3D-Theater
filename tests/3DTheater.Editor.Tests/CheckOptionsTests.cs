namespace Theater3D.Editor.Tests;

/// <summary>確かめのための起動の指定（--track-camera・--exit-after）。</summary>
[TestClass]
public sealed class CheckOptionsTests
{
    [TestMethod]
    public void OptionsAreRead()
    {
        Assert.IsTrue(CheckOptions.Parse([]).IsEmpty);
        Assert.IsTrue(CheckOptions.Parse(["--live", "--safe-mode"]).IsEmpty, "ほかの指定は見ない");

        CheckOptions first = CheckOptions.Parse(["--live", "--track-camera"]);
        Assert.IsTrue(first.TrackCamera);
        Assert.IsNull(first.CameraName, "名前が無ければ初めのカメラ");
        Assert.IsNull(first.ExitAfter);

        CheckOptions named = CheckOptions.Parse(["--TRACK-CAMERA=Surface Camera Front", "--exit-after=300"]);
        Assert.AreEqual("Surface Camera Front", named.CameraName, "空白の入った名前（コマンドで引用符に入れたもの）");
        Assert.AreEqual(TimeSpan.FromMinutes(5), named.ExitAfter);
        Assert.IsEmpty(named.Problems);

        Assert.AreEqual("Front", CheckOptions.Parse(["--track-camera=\"Front\""]).CameraName);
        Assert.IsNull(CheckOptions.Parse(["--track-camera="]).CameraName);
    }

    [TestMethod]
    public void BadSecondsAreReported()
    {
        foreach (string bad in new[] { "--exit-after", "--exit-after=", "--exit-after=abc", "--exit-after=5", "--exit-after=-30", "--exit-after=1.5", "--exit-after=999999" })
        {
            CheckOptions options = CheckOptions.Parse([bad]);
            Assert.IsNull(options.ExitAfter, bad);
            Assert.HasCount(1, options.Problems, bad);
            StringAssert.Contains(options.Problems[0], bad);
        }
        Assert.AreEqual(TimeSpan.FromSeconds(10), CheckOptions.Parse(["--exit-after=10"]).ExitAfter, "10 秒から");
    }

    [TestMethod]
    public void SceneFolderIsRead()
    {
        CheckOptions options = CheckOptions.Parse([@"--scene-folder=C:\確かめ\シーン"]);
        Assert.AreEqual(@"C:\確かめ\シーン", options.SceneFolder);
        Assert.IsFalse(options.IsEmpty);
        Assert.AreEqual(@"D:\a b", CheckOptions.Parse([@"--scene-folder=""D:\a b"""]).SceneFolder, "空白の入った場所（引用符）");
        foreach (string bad in new[] { "--scene-folder", "--scene-folder=", "--scene-folder=シーン", @"--scene-folder=.\シーン" })
        {
            CheckOptions read = CheckOptions.Parse([bad]);
            Assert.IsNull(read.SceneFolder, bad);
            Assert.HasCount(1, read.Problems, bad);
        }
    }

    [TestMethod]
    public void PreviewQualityAndWindowAreRead()
    {
        CheckOptions options = CheckOptions.Parse(["--preview-quality=light", "--window=maximized"]);
        Assert.AreEqual(PreviewQuality.Light, options.Quality);
        Assert.AreEqual(new CheckWindow(true), options.Window);
        Assert.IsFalse(options.IsEmpty);
        Assert.IsEmpty(options.Problems);

        Assert.AreEqual(PreviewQuality.High, CheckOptions.Parse(["--PREVIEW-QUALITY=High"]).Quality, "大文字でもよい");
        Assert.AreEqual(PreviewQuality.Standard, CheckOptions.Parse(["--preview-quality=ふつう"]).Quality, "日本語の名前でもよい");
        Assert.AreEqual(new CheckWindow(false, 1280, 800), CheckOptions.Parse(["--window=1280x800"]).Window);
        Assert.AreEqual(new CheckWindow(false, 1920, 1080), CheckOptions.Parse(["--window=1920×1080"]).Window, "× でもよい");
        Assert.IsFalse(CheckOptions.Parse(["--window=640x480"]).Window!.Maximized, "いちばん小さい大きさ");

        foreach (string bad in new[] { "--preview-quality", "--preview-quality=best", "--window", "--window=big", "--window=1280", "--window=320x200",
            "--window=1280x-800", "--window=99999x800" })
        {
            CheckOptions read = CheckOptions.Parse([bad]);
            Assert.IsNull(read.Quality, bad);
            Assert.IsNull(read.Window, bad);
            Assert.HasCount(1, read.Problems, bad);
            StringAssert.Contains(read.Problems[0], bad);
        }
    }

    [TestMethod]
    public void VmcReceivingIsRead()
    {
        // VMC プロトコルで受け取る（設計判断記録 98 章）: ポートを書かなければ 39539
        CheckOptions options = CheckOptions.Parse(["--track-vmc"]);
        Assert.AreEqual(39539, options.VmcPort, "VMC プロトコルの受け手の初めのポート");
        Assert.IsFalse(options.IsEmpty);
        Assert.IsEmpty(options.Problems);
        Assert.AreEqual(40000, CheckOptions.Parse(["--Track-Vmc=40000"]).VmcPort);
        Assert.IsNull(CheckOptions.None.VmcPort);
        foreach (string bad in new[] { "--track-vmc=80", "--track-vmc=abc", "--track-vmc=70000" })
        {
            CheckOptions read = CheckOptions.Parse([bad]);
            Assert.IsNull(read.VmcPort, bad);
            Assert.HasCount(1, read.Problems, bad);
        }
    }

    [TestMethod]
    public void CameraDistanceIsRead()
    {
        CheckOptions options = CheckOptions.Parse(["--camera-distance=15"]);
        Assert.AreEqual(15f, options.CameraDistance);
        Assert.IsFalse(options.IsEmpty);
        Assert.IsEmpty(options.Problems);
        Assert.AreEqual(12.5f, CheckOptions.Parse(["--Camera-Distance=\"12.5\""]).CameraDistance, "大文字・引用符・小数でもよい");
        Assert.IsNull(CheckOptions.None.CameraDistance);

        foreach (string bad in new[] { "--camera-distance", "--camera-distance=near", "--camera-distance=1", "--camera-distance=5000", "--camera-distance=NaN" })
        {
            CheckOptions read = CheckOptions.Parse([bad]);
            Assert.IsNull(read.CameraDistance, bad);
            Assert.HasCount(1, read.Problems, bad);
            StringAssert.Contains(read.Problems[0], bad);
        }
    }
}
