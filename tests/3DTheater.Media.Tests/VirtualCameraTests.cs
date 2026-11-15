using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.Versioning;

namespace Theater3D.Media.Tests;

/// <summary>
/// 仮想カメラ: 色の変換・受け渡しの決まり・パイプの受け口と、カメラの元の DLL を、ほかのアプリと同じ読み方（Source Reader）で読む確かめ。
/// Windows への登録は要らない（DLL から部品を直接作る）。3D Theater で仮想カメラを使っている間は、パイプの名前がぶつかるので確かめない。
/// </summary>
[TestClass]
[DoNotParallelize] // 同じ名前のパイプを使うので、1 つずつ確かめる
[SupportedOSPlatform("windows")]
public sealed class VirtualCameraTests
{
    private static string DllPath => Path.Combine(AppContext.BaseDirectory, VirtualCameraProtocol.DllName);

    // 左半分が赤・右半分が青、上の 8 行は緑（上下の向きを確かめる）
    private static void Paint(Span<byte> bgra, int width, int height)
    {
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                int i = ((y * width) + x) * 4;
                (byte b, byte g, byte r) = y < 8 ? ((byte)0, (byte)255, (byte)0) : x < width / 2 ? ((byte)0, (byte)0, (byte)255) : ((byte)255, (byte)0, (byte)0);
                bgra[i] = b;
                bgra[i + 1] = g;
                bgra[i + 2] = r;
                bgra[i + 3] = 255;
            }
        }
    }

    private static VirtualCameraServer StartServer(VirtualCameraFrames frames)
    {
        var server = new VirtualCameraServer(frames);
        try
        {
            server.Start();
        }
        catch (IOException)
        {
            server.Dispose();
            Assert.Inconclusive("ほかのアプリ（3D Theater）が仮想カメラを使っているため、パイプを作れません");
        }
        return server;
    }

    [TestMethod]
    public void ConvertsBgraToNv12WithTheChosenColorStandard()
    {
        // 4 × 2: 左の 2 × 2 が白、右の 2 × 2 が赤
        byte[] bgra = new byte[4 * 2 * 4];
        for (int y = 0; y < 2; y++)
        {
            for (int x = 0; x < 4; x++)
            {
                int i = ((y * 4) + x) * 4;
                bool white = x < 2;
                bgra[i] = (byte)(white ? 255 : 0);
                bgra[i + 1] = (byte)(white ? 255 : 0);
                bgra[i + 2] = 255;
                bgra[i + 3] = 255;
            }
        }
        byte[] nv12 = new byte[VirtualCameraProtocol.Nv12Bytes(4, 2)];
        VideoConvert.BgraToNv12(bgra, 16, 4, 2, nv12, YuvMatrix.Bt601);
        Assert.AreEqual(235, nv12[0], "白の明るさ");
        Assert.AreEqual(81, nv12[2], "赤の明るさ（BT.601: 16 + 0.299 × 219）");
        Assert.AreEqual(128, nv12[8], "白の色差 U");
        Assert.AreEqual(128, nv12[9], "白の色差 V");
        Assert.AreEqual(90, nv12[10], "赤の色差 U");
        Assert.AreEqual(240, nv12[11], "赤の色差 V");

        VideoConvert.BgraToNv12(bgra, 16, 4, 2, nv12, YuvMatrix.Bt709);
        Assert.AreEqual(63, nv12[2], "BT.709 の赤の明るさ（16 + 0.2126 × 219）");
        Assert.ThrowsExactly<VideoException>(() => VideoConvert.BgraToNv12(bgra, 16, 3, 2, nv12, YuvMatrix.Bt601), "奇数の幅");
    }

    [TestMethod]
    public void RequestsAndHeadersRoundTrip()
    {
        byte[] buffer = new byte[VirtualCameraProtocol.HeaderSize];
        new VirtualCameraProtocol.Request(1280, 720, 42).Write(buffer);
        Assert.AreEqual(new VirtualCameraProtocol.Request(1280, 720, 42), VirtualCameraProtocol.Request.Read(buffer));
        new VirtualCameraProtocol.FrameHeader(640, 360, VirtualCameraProtocol.FlagNoFrame, 0, 7).Write(buffer);
        Assert.AreEqual(new VirtualCameraProtocol.FrameHeader(640, 360, VirtualCameraProtocol.FlagNoFrame, 0, 7), VirtualCameraProtocol.FrameHeader.Read(buffer));
        Assert.IsNull(VirtualCameraProtocol.Request.Read(buffer), "見出しを頼みとしては読まない");
        new VirtualCameraProtocol.Request(8192, 720, 0).Write(buffer);
        Assert.IsNull(VirtualCameraProtocol.Request.Read(buffer), "大きすぎる");
        Assert.IsTrue(VirtualCameraProtocol.IsValidSize(1920, 1080));
        Assert.IsFalse(VirtualCameraProtocol.IsValidSize(1919, 1080), "奇数");

        // 色の決まりは高さで決める（Windows が色に戻すときの決まり）
        Assert.AreEqual(YuvMatrix.Bt601, VirtualCameraProtocol.MatrixFor(576));
        Assert.AreEqual(YuvMatrix.Bt709, VirtualCameraProtocol.MatrixFor(578));
        Assert.AreEqual(YuvMatrix.Bt709, VirtualCameraProtocol.MatrixFor(1920), "縦長");
    }

    [TestMethod]
    public void ServerAnswersTheSizeAndTheLatestFrame()
    {
        var frames = new VirtualCameraFrames { PreferredSize = (64, 48) };
        using VirtualCameraServer server = StartServer(frames);
        using var client = new NamedPipeClientStream(".", VirtualCameraProtocol.PipeName, PipeDirection.InOut);
        client.Connect(2000);
        byte[] request = new byte[VirtualCameraProtocol.RequestSize];
        byte[] header = new byte[VirtualCameraProtocol.HeaderSize];
        byte[] payload = new byte[VirtualCameraProtocol.Nv12Bytes(64, 48)];

        VirtualCameraProtocol.FrameHeader Ask(int width, int height, ulong last)
        {
            new VirtualCameraProtocol.Request(width, height, last).Write(request);
            client.Write(request);
            client.ReadExactly(header);
            VirtualCameraProtocol.FrameHeader answer = VirtualCameraProtocol.FrameHeader.Read(header) ?? throw new AssertFailedException("見出しが決まりと違います");
            if (answer.PayloadSize > 0)
            {
                client.ReadExactly(payload.AsSpan(0, answer.PayloadSize));
            }
            return answer;
        }

        VirtualCameraProtocol.FrameHeader size = Ask(0, 0, 0);
        Assert.AreEqual((64, 48), (size.Width, size.Height), "大きさを聞くと、配信用の画面の大きさ");
        Assert.AreEqual(VirtualCameraProtocol.FlagNoFrame, size.Flags);

        VirtualCameraProtocol.FrameHeader none = Ask(64, 48, 0);
        Assert.AreEqual(0, none.PayloadSize, "まだ絵が無い");
        Assert.IsTrue(frames.TryGetWanted(out int wantedWidth, out int wantedHeight), "頼まれたので、描く側は描き始める");
        Assert.AreEqual((64, 48), (wantedWidth, wantedHeight));

        Paint(frames.GetWriteBuffer(64, 48), 64, 48);
        frames.Publish(64, 48);
        VirtualCameraProtocol.FrameHeader first = Ask(64, 48, 0);
        Assert.AreEqual(1UL, first.Sequence);
        Assert.AreEqual(payload.Length, first.PayloadSize);
        Assert.AreEqual(81, payload[(20 * 64) + 4], "左は赤（明るさ 81）");
        Assert.AreEqual(145, payload[0], "上は緑（明るさ 16 + 0.587 × 219）");

        VirtualCameraProtocol.FrameHeader again = Ask(64, 48, 1);
        Assert.AreEqual(1UL, again.Sequence, "新しい絵が無ければ、少し待ってから同じ絵を返す");
        Paint(frames.GetWriteBuffer(64, 48), 64, 48);
        frames.Publish(64, 48);
        Assert.AreEqual(2UL, Ask(64, 48, 1).Sequence);
        Assert.AreEqual(0, Ask(32, 24, 0).PayloadSize, "描いていない大きさの絵は無い");

        // 決まりと違う頼みが来たら切る
        request.AsSpan().Clear();
        client.Write(request);
        Assert.AreEqual(0, client.Read(header), "切られた");
    }

    [TestMethod]
    public void VirtualCameraSourceSendsTheAppFrames()
    {
        var frames = new VirtualCameraFrames { PreferredSize = (160, 120) };
        using VirtualCameraServer server = StartServer(frames);
        Paint(frames.GetWriteBuffer(160, 120), 160, 120);
        frames.Publish(160, 120);

        using CameraCapture capture = CameraCapture.OpenActivate(DllPath, VirtualCameraProtocol.Clsid);
        Assert.AreEqual((160, 120), (capture.Width, capture.Height), "作るときにアプリへ聞いた大きさ");
        Assert.AreEqual(30.0, capture.FrameRate, 0.01);

        byte[] bgra = new byte[capture.FrameBytes];
        bool received = false;
        for (int i = 0; i < 90 && !received; i++)
        {
            Assert.IsTrue(capture.Read(bgra, out _));
            received = bgra[((60 * 160) + 20) * 4 + 2] > 200; // 左の赤が届いたか（初めの何枚かは、まだ黒）
        }
        Assert.IsTrue(received, "アプリの絵が届く");
        static int Channel(byte[] p, int x, int y, int c) => p[(((y * 160) + x) * 4) + c];
        Assert.IsLessThan(40, Channel(bgra, 20, 60, 0), "左は赤（青は少ない）");
        Assert.IsGreaterThan(200, Channel(bgra, 140, 60, 0), "右は青");
        Assert.IsLessThan(40, Channel(bgra, 140, 60, 2), "右は赤が少ない");
        Assert.IsGreaterThan(200, Channel(bgra, 80, 2, 1), "上は緑（上下が逆さでない）");
        Assert.IsGreaterThan(0, server.Connections, "カメラの元がつないでいる");
    }

    [TestMethod]
    public void HdFramesKeepTheirColors()
    {
        // HD の大きさは BT.709 で読まれるので、それに合わせて変換する（合わないと緑が 216 ほどに暗くなる）
        var frames = new VirtualCameraFrames { PreferredSize = (1280, 720) };
        using VirtualCameraServer server = StartServer(frames);
        byte[] bgra = frames.GetWriteBuffer(1280, 720);
        for (int i = 0; i < 1280 * 720; i++)
        {
            bool left = (i % 1280) < 640;
            bgra[i * 4] = 0;
            bgra[(i * 4) + 1] = (byte)(left ? 255 : 0);
            bgra[(i * 4) + 2] = (byte)(left ? 0 : 255);
            bgra[(i * 4) + 3] = 255;
        }
        frames.Publish(1280, 720);
        using CameraCapture capture = CameraCapture.OpenActivate(DllPath, VirtualCameraProtocol.Clsid);
        byte[] frame = new byte[capture.FrameBytes];
        bool received = false;
        for (int i = 0; i < 90 && !received; i++)
        {
            Assert.IsTrue(capture.Read(frame, out _));
            received = frame[(((360 * 1280) + 320) * 4) + 1] > 100;
        }
        Assert.IsTrue(received, "アプリの絵が届く");
        Assert.IsGreaterThan(245, frame[(((360 * 1280) + 320) * 4) + 1], "緑が暗くならない");
        Assert.IsGreaterThan(245, frame[(((360 * 1280) + 960) * 4) + 2], "赤が暗くならない");
        Assert.IsLessThan(10, frame[(((360 * 1280) + 960) * 4) + 1], "赤に緑が混ざらない");
    }

    [TestMethod]
    public void WithoutTheAppTheSourceSendsBlack()
    {
        // アプリが動いていなければ、決まった大きさの黒を出す（固まらない）
        using (var probe = new VirtualCameraServer(new VirtualCameraFrames()))
        {
            try
            {
                probe.Start();
            }
            catch (IOException)
            {
                Assert.Inconclusive("ほかのアプリ（3D Theater）が仮想カメラを使っているため、確かめられません");
            }
        }
        using CameraCapture capture = CameraCapture.OpenActivate(DllPath, VirtualCameraProtocol.Clsid);
        Assert.AreEqual((1920, 1080), (capture.Width, capture.Height));
        byte[] bgra = new byte[capture.FrameBytes];
        Assert.IsTrue(capture.Read(bgra, out _));
        Assert.IsLessThan(8, bgra.Max(), "黒");
    }

    [TestMethod]
    public void CancelEndsAWaitingRead()
    {
        // 映像が届くのを待っている読み出しは、Cancel ですぐ戻る。
        // 前は、待っている間にカメラの元を閉じても戻らず、トラッキングを止めるときに 5 秒待たされた（ノートパソコンのカメラで起きた）
        var frames = new VirtualCameraFrames { PreferredSize = (160, 120) };
        using VirtualCameraServer server = StartServer(frames);
        Paint(frames.GetWriteBuffer(160, 120), 160, 120);
        frames.Publish(160, 120);

        for (int trial = 1; trial <= 5; trial++)
        {
            CameraCapture? shared = null;
            Exception? ended = null;
            long returned = 0;
            using var reading = new ManualResetEventSlim();
            var thread = new Thread(() =>
            {
                try
                {
                    // 開く・読む・壊すは、同じスレッドで
                    using CameraCapture capture = CameraCapture.OpenActivate(DllPath, VirtualCameraProtocol.Clsid);
                    byte[] bgra = new byte[capture.FrameBytes];
                    capture.Read(bgra, out _);
                    Volatile.Write(ref shared, capture);
                    reading.Set();
                    while (true)
                    {
                        capture.Read(bgra, out _); // 1 秒に 30 枚なので、ほとんどの時間は映像が届くのを待っている
                    }
                }
                catch (Exception ex)
                {
                    ended = ex;
                }
                finally
                {
                    returned = Stopwatch.GetTimestamp(); // カメラを閉じた後
                    reading.Set();
                }
            }) { IsBackground = true };
            thread.Start();
            Assert.IsTrue(reading.Wait(TimeSpan.FromSeconds(10)), "読み始める");
            Thread.Sleep(100 + (trial * 37));
            long cancelled = Stopwatch.GetTimestamp();
            Volatile.Read(ref shared)?.Cancel();
            Assert.IsTrue(thread.Join(TimeSpan.FromSeconds(5)), $"{trial} 回目: Cancel で、待っている読み出しが戻る");
            Assert.IsInstanceOfType<VideoException>(ended, $"{trial} 回目: やめたことが例外で分かる");
            double milliseconds = Stopwatch.GetElapsedTime(cancelled, returned).TotalMilliseconds;
            Assert.IsLessThan(1000, milliseconds, $"{trial} 回目: カメラを閉じるまで含めて、すぐ終わる（{milliseconds:F0} ms）");
        }
    }
}
