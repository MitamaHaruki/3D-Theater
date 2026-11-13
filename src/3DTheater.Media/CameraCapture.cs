using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Theater3D.Media;

/// <summary>カメラ 1 つ（名前と、開くときに使う目印）。</summary>
public sealed record CameraDevice(string Name, string Id);

/// <summary>
/// カメラか動画のファイルから映像を読む（Windows の Media Foundation を包んだ C++ の DLL）。
/// 映像は BGRA 8 ビット・上の行から。<see cref="Read"/> は次のフレームが届くまで待つ。
/// 作ってから壊すまで、同じスレッドから呼ぶこと（COM の決まり。トラッキングでは読み込み用のスレッドで使う）。
/// </summary>
public sealed unsafe partial class CameraCapture : IDisposable
{
    private const string Library = "3DTheater.Video.Native";
    private const int EndOfStream = -8;
    private const int MinimumApiMinor = 2;
    private nint _handle;

    private CameraCapture(nint handle)
    {
        _handle = handle;
        var info = new TvCaptureInfo { StructSize = (uint)sizeof(TvCaptureInfo) };
        Check(tv_capture_get_info(handle, &info));
        Width = (int)info.Width;
        Height = (int)info.Height;
        FrameRate = info.FrameRate;
        IsFile = info.IsFile != 0;
    }

    public int Width { get; }

    public int Height { get; }

    /// <summary>1 秒あたりのフレーム数（分からなければ 0）。</summary>
    public double FrameRate { get; }

    /// <summary>動画のファイル（確かめ用）か。</summary>
    public bool IsFile { get; }

    /// <summary>1 フレームの大きさ（バイト）。</summary>
    public int FrameBytes => Width * Height * 4;

    /// <summary>今つながっているカメラの一覧。</summary>
    public static IReadOnlyList<CameraDevice> ListCameras()
    {
        CheckVersion();
        int count = tv_camera_list(null, 0);
        Check(count);
        if (count == 0)
        {
            return [];
        }
        var items = new TvCameraInfo[count];
        for (int i = 0; i < count; i++)
        {
            items[i].StructSize = (uint)sizeof(TvCameraInfo);
        }
        fixed (TvCameraInfo* p = items)
        {
            int again = tv_camera_list(p, count);
            Check(again);
            count = Math.Min(count, again);
        }
        var cameras = new List<CameraDevice>(count);
        for (int i = 0; i < count; i++)
        {
            cameras.Add(new CameraDevice(Text(ref items[i].Name), Text(ref items[i].Id)));
        }
        return cameras;
    }

    /// <summary>カメラを開く。大きさとフレームの速さは、カメラが出せる形のうち、いちばん近いもの。</summary>
    public static CameraCapture OpenCamera(string id, int width = 1280, int height = 720, int frameRate = 30)
    {
        CheckVersion();
        nint handle;
        fixed (char* p = id)
        {
            Check(tv_capture_open_camera(p, width, height, frameRate, &handle));
        }
        return Wrap(handle);
    }

    /// <summary>動画のファイルを開く（カメラの代わりの確かめ用）。<paramref name="loop"/> なら終わりで先頭に戻る。</summary>
    public static CameraCapture OpenFile(string path, bool loop = true)
    {
        CheckVersion();
        nint handle;
        fixed (char* p = path)
        {
            Check(tv_capture_open_file(p, loop ? 1 : 0, &handle));
        }
        return Wrap(handle);
    }

    /// <summary>
    /// 次のフレームを待って <paramref name="bgra"/> に書く（1 行は幅 × 4 バイト）。時刻は 100 ナノ秒単位。
    /// 動画のファイルの終わり（繰り返さないとき）なら false。
    /// </summary>
    public bool Read(Span<byte> bgra, out long timestamp)
    {
        ObjectDisposedException.ThrowIf(_handle == 0, this);
        if (bgra.Length < FrameBytes)
        {
            throw new ArgumentException("フレームを書き込む場所が小さすぎます", nameof(bgra));
        }
        long time;
        int result;
        fixed (byte* p = bgra)
        {
            result = tv_capture_read(_handle, p, Width * 4, bgra.Length, &time);
        }
        timestamp = time;
        if (result == EndOfStream)
        {
            return false;
        }
        Check(result);
        return true;
    }

    /// <summary>待っている <see cref="Read"/> をやめさせる（ほかのスレッドから呼んでよい。この後の読み出しは例外になる）。</summary>
    public void Cancel()
    {
        nint handle = _handle;
        if (handle != 0)
        {
            tv_capture_cancel(handle);
        }
    }

    public void Dispose()
    {
        if (_handle != 0)
        {
            tv_capture_destroy(_handle);
            _handle = 0;
        }
    }

    private static CameraCapture Wrap(nint handle)
    {
        try
        {
            return new CameraCapture(handle);
        }
        catch
        {
            tv_capture_destroy(handle);
            throw;
        }
    }

    private static string Text(ref FixedText1024 text)
    {
        ReadOnlySpan<char> span = text;
        int end = span.IndexOf('\0');
        return new string(end < 0 ? span : span[..end]);
    }

    private static string Text(ref FixedText256 text)
    {
        ReadOnlySpan<char> span = text;
        int end = span.IndexOf('\0');
        return new string(end < 0 ? span : span[..end]);
    }

    private static void CheckVersion()
    {
        var (major, minor) = VideoWriter.ApiVersion;
        if (major != VideoWriter.ExpectedApiVersionMajor || minor < MinimumApiMinor)
        {
            throw new VideoException(-4, $"動画の DLL の版が合いません（DLL: {major}.{minor}、カメラの読み込みに必要: {VideoWriter.ExpectedApiVersionMajor}.{MinimumApiMinor} 以上）");
        }
    }

    private static void Check(int result)
    {
        if (result >= 0)
        {
            return;
        }
        char* buffer = stackalloc char[512];
        int length = tv_get_last_error_message(buffer, 512);
        string message = length > 0 ? new string(buffer, 0, Math.Min(length, 511)) : "映像の読み込みでエラーが発生しました";
        throw new VideoException(result, message);
    }

    [InlineArray(256)]
    private struct FixedText256
    {
        private char _first;
    }

    [InlineArray(1024)]
    private struct FixedText1024
    {
        private char _first;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct TvCameraInfo
    {
        public uint StructSize;
        public FixedText256 Name;
        public FixedText1024 Id;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct TvCaptureInfo
    {
        public uint StructSize;
        public uint Width;
        public uint Height;
        public uint IsFile;
        public double FrameRate;
    }

    [LibraryImport(Library)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    private static partial int tv_get_last_error_message(char* buffer, int capacity);

    [LibraryImport(Library)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    private static partial int tv_camera_list(TvCameraInfo* items, int capacity);

    [LibraryImport(Library)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    private static partial int tv_capture_open_camera(char* id, int width, int height, int frameRate, nint* capture);

    [LibraryImport(Library)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    private static partial int tv_capture_open_file(char* path, int loop, nint* capture);

    [LibraryImport(Library)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    private static partial int tv_capture_get_info(nint capture, TvCaptureInfo* info);

    [LibraryImport(Library)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    private static partial int tv_capture_read(nint capture, void* bgra, int stride, int bufferSize, long* timestamp);

    [LibraryImport(Library)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    private static partial int tv_capture_cancel(nint capture);

    [LibraryImport(Library)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    private static partial int tv_capture_destroy(nint capture);
}
