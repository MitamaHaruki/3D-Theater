using Theater3D.Domain;

namespace Theater3D.Editor.Tests;

/// <summary>再生位置を外の時計（音声）に合わせることと、音声の項目。</summary>
[TestClass]
public sealed class PlaybackClockTests
{
    private sealed class FakeClock : IPlaybackClock
    {
        public double? Frame { get; set; }

        public bool TryGetFramePosition(out double frame)
        {
            frame = Frame ?? 0;
            return Frame is not null;
        }
    }

    private static readonly TimeSpan Tick = TimeSpan.FromSeconds(1.0 / 60);

    [TestMethod]
    public void PlaybackFollowsTheClockInsteadOfTheWallTime()
    {
        var clock = new FakeClock { Frame = 0 };
        var playback = new PlaybackService { Clock = clock };
        playback.Play();

        clock.Frame = 10.6;
        playback.Advance(Tick);
        Assert.AreEqual(10, playback.CurrentFrame, "時計の位置へ進める（実際の時間を足していかない）");

        clock.Frame = 9.9;
        playback.Advance(Tick);
        Assert.AreEqual(10, playback.CurrentFrame, "時計が少し遅れて見えても戻さない");

        clock.Frame = null; // 音声の範囲の外
        playback.Advance(TimeSpan.FromSeconds(0.1));
        Assert.AreEqual(13, playback.CurrentFrame, "時計が答えなければ実際の時間で進める");
    }

    [TestMethod]
    public void ClockPastTheEndLoopsOrStops()
    {
        var clock = new FakeClock();
        var playback = new PlaybackService { Clock = clock, EndFrame = 100 };
        playback.Seek(95);
        playback.Play();

        clock.Frame = 103;
        playback.Advance(Tick);
        Assert.AreEqual(2, playback.CurrentFrame, "ループなら先頭からの続きへ");

        playback.Loop = false;
        playback.Seek(95);
        playback.Play();
        clock.Frame = 120;
        playback.Advance(Tick);
        Assert.AreEqual(100, playback.CurrentFrame);
        Assert.IsFalse(playback.IsPlaying, "ループでなければ終わりで止まる");
    }

    [TestMethod]
    public void AudioNodeKeepsItsSettingsAndIsUndoable()
    {
        SceneDocument scene = SceneDocument.CreateDefault();
        var audio = new AudioNode("曲", @"C:\曲.wav", TimeSpan.FromSeconds(10.01));
        scene.Add(scene.AudioGroup, audio);

        Assert.AreSame(audio, scene.Audio);
        Assert.AreEqual(301, audio.EndFrame(30), "10.01 秒を 30 fps で数えると 301 フレーム目まで");
        audio.StartFrame = -30;
        Assert.AreEqual(271, audio.EndFrame(30));

        NodeState before = audio.CaptureState();
        audio.StartFrame = 15;
        audio.Volume = 3; // 1 までに抑える
        Assert.AreEqual(1f, audio.Volume);
        audio.RestoreState(before);
        Assert.AreEqual(-30, audio.StartFrame);

        scene.ResetToDefault();
        Assert.IsNull(scene.Audio, "新しいプロジェクトでは音声を外す");
        Assert.IsTrue(scene.FixedNodes.ContainsKey("group.audio"));
    }

    [TestMethod]
    public void AudioIsSavedInTheProjectWithARelativePath()
    {
        var (scene, playback, animation, _) = AnimationFixtures.Create();
        var audio = new AudioNode("曲", @"D:\作品\音声\曲.wav", TimeSpan.FromSeconds(30)) { StartFrame = 12, Volume = 0.5f, IsVisible = false };
        scene.Add(scene.AudioGroup, audio);

        ProjectFile project = ProjectSerializer.Deserialize(
            ProjectSerializer.Serialize(ProjectCapture.Capture(scene, playback, animation, @"D:\作品", [])), new Formats.LoadReport())!;

        AudioEntry entry = project.Audio!;
        Assert.AreEqual(@"音声\曲.wav", entry.File, "プロジェクトのフォルダーから見た場所");
        Assert.AreEqual(@"D:\作品\音声\曲.wav", entry.AbsoluteFile);
        Assert.AreEqual(12, entry.StartFrame);
        Assert.AreEqual(0.5f, entry.Volume);
        Assert.IsFalse(entry.Visible);

        scene.Remove(audio);
        var missing = new AudioEntry { File = "どこか.wav", StartFrame = 3 };
        Assert.AreSame(missing, ProjectCapture.Capture(scene, playback, animation, null, [], missing).Audio, "見つからなかった音声は、そのまま書き戻す");
    }
}
