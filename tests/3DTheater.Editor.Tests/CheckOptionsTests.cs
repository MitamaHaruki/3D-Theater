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
}
