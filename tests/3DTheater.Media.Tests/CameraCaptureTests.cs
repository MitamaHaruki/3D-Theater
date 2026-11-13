namespace Theater3D.Media.Tests;

/// <summary>カメラ・動画のファイルから映像を読む。カメラが無くても確かめられるよう、書いた動画をカメラの代わりに読む。</summary>
[TestClass]
public sealed class CameraCaptureTests
{
    private string _folder = string.Empty;

    [TestInitialize]
    public void Setup()
    {
        _folder = Path.Combine(Path.GetTempPath(), "3DTheater.Media.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_folder);
    }

    [TestCleanup]
    public void Cleanup()
    {
        try
        {
            Directory.Delete(_folder, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    // 幅は 16 の倍数にしない（解凍した映像は 1 行を揃えて持つことがあり、1 行の長さを取り違えると行がずれる）
    private const int VideoWidth = 170;
    private const int VideoHeight = 96;

    // フレーム i は、左から i × 16 画素までが白、残りが黒（どのフレームを読んだか分かるように）。上の 16 行は赤（上下の向きを確かめる）
    private static byte[] Frame(int width, int height, int index)
    {
        var pixels = new byte[width * height * 4];
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                int i = ((y * width) + x) * 4;
                byte v = (byte)(x < index * 16 ? 255 : 0);
                pixels[i] = pixels[i + 1] = pixels[i + 2] = v;
                if (y < 16)
                {
                    pixels[i] = pixels[i + 1] = 0;
                    pixels[i + 2] = 255;
                }
                pixels[i + 3] = 255;
            }
        }
        return pixels;
    }

    private string WriteVideo(int frames)
    {
        string path = Path.Combine(_folder, "カメラの代わり.mp4");
        using (VideoWriter writer = VideoWriter.Create(path, new VideoSettings(VideoWidth, VideoHeight, 30, 2_000_000)))
        {
            for (int i = 0; i < frames; i++)
            {
                writer.WriteVideo(Frame(VideoWidth, VideoHeight, i + 1), VideoWidth * 4, i);
            }
            writer.Finish();
        }
        return path;
    }

    private static int WhiteColumns(byte[] bgra, int width, int height) => WhiteColumnsAt(bgra, width, height / 2);

    private static int WhiteColumnsAt(byte[] bgra, int width, int y)
    {
        int count = 0;
        for (int x = 0; x < width; x++)
        {
            if (bgra[((y * width) + x) * 4 + 1] > 128)
            {
                count++;
            }
        }
        return count;
    }

    [TestMethod]
    public void VideoFilesAreReadFrameByFrameAndLooped()
    {
        string path = WriteVideo(4);
        using CameraCapture capture = CameraCapture.OpenFile(path, loop: true);
        Assert.AreEqual((VideoWidth, VideoHeight), (capture.Width, capture.Height));
        Assert.IsTrue(capture.IsFile);
        Assert.AreEqual(30, capture.FrameRate, 0.01);

        var frame = new byte[capture.FrameBytes];
        var widths = new List<int>();
        var times = new List<long>();
        for (int i = 0; i < 6; i++)
        {
            Assert.IsTrue(capture.Read(frame, out long time), "繰り返すので終わらない");
            widths.Add(WhiteColumns(frame, VideoWidth, VideoHeight));
            times.Add(time);
            if (i == 0)
            {
                // 上の行は赤（上下が逆さまでない）、下の行は赤くない。行ごとにずれていれば、どの行でも同じ所が白い
                int top = ((4 * VideoWidth) + 100) * 4;
                int bottom = ((90 * VideoWidth) + 100) * 4;
                Assert.IsGreaterThan(200, (int)frame[top + 2], "上の行は赤");
                Assert.IsLessThan(80, (int)frame[top + 1]);
                Assert.IsLessThan(80, (int)frame[bottom + 2], "下の行は赤くない");
                for (int y = 20; y < VideoHeight; y += 15)
                {
                    Assert.AreEqual(16, WhiteColumnsAt(frame, VideoWidth, y), 3, $"{y} 行目も同じ幅だけ白い（行がずれていない）");
                }
            }
        }
        // 白い幅は 16・32・48・64、その後は先頭へ戻る（符号化のにじみがあるので、おおよそ）
        int[] expected = [16, 32, 48, 64, 16, 32];
        for (int i = 0; i < expected.Length; i++)
        {
            Assert.AreEqual(expected[i], widths[i], 3, $"{i} 回目に読んだフレーム");
        }
        Assert.IsLessThan(times[3], times[2], "時刻は進む");
    }

    [TestMethod]
    public void EndOfFileWithoutLoopAndBadArguments()
    {
        string path = WriteVideo(3);
        using CameraCapture capture = CameraCapture.OpenFile(path, loop: false);
        var frame = new byte[capture.FrameBytes];
        int read = 0;
        while (capture.Read(frame, out _))
        {
            read++;
            Assert.IsLessThan(10, read, "終わりで止まる");
        }
        Assert.AreEqual(3, read);
        Assert.ThrowsExactly<ArgumentException>(() => capture.Read(new byte[16], out _), "小さすぎる場所");
        Assert.ThrowsExactly<VideoException>(() => CameraCapture.OpenFile(Path.Combine(_folder, "無い.mp4")));
        Assert.ThrowsExactly<VideoException>(() => CameraCapture.OpenCamera(@"\\?\存在しないカメラ"));
    }

    [TestMethod]
    public void CamerasCanBeListed()
    {
        // カメラが無い PC でも、空の一覧が返る（名前と目印が空でないこと）
        IReadOnlyList<CameraDevice> cameras = CameraCapture.ListCameras();
        Assert.IsTrue(cameras.All(c => c.Name.Length > 0 && c.Id.Length > 0));
    }
}
