using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Theater3D.Media;

/// <summary>動画の C API が失敗を返したときの例外。</summary>
public sealed class VideoException(int result, string message) : Exception($"{message}（{result}）")
{
    public int Result { get; } = result;

    /// <summary>この大きさ・形式の符号化ができない（tv_video.h の TV_E_NOT_SUPPORTED）。</summary>
    public bool IsNotSupported => Result == -4;
}

/// <summary>
/// 書き出す動画の形式。映像は H.264、音声は AAC（<see cref="AudioSampleRate"/> が 0 なら音声なし）。
/// 幅と高さは偶数（H.264 の決まり）。
/// </summary>
public sealed record VideoSettings(int Width, int Height, int FrameRate, int VideoBitrate, int AudioSampleRate = 0, int AudioChannels = 2,
    int AudioBitrate = 192_000);

/// <summary>動画のファイルの中身（確かめる用）。</summary>
public sealed record VideoProbe(int Width, int Height, double FrameRate, double DurationSeconds, bool HasAudio, int AudioSampleRate, int AudioChannels);

/// <summary>
/// MP4 の書き手（Windows の Media Foundation を包んだ C++ の DLL）。作ってから壊すまで、同じスレッドから呼ぶこと。
/// 映像は BGRA 8 ビット・上の行から、0 フレーム目から順に渡す。音声は 16 ビットの PCM（チャンネルを交互）を、続けて渡す。
/// <see cref="Finish"/> せずに <see cref="Dispose"/> すると、書きかけのファイルが残る（呼び出し側で消すこと）。
/// </summary>
public sealed unsafe partial class VideoWriter : IDisposable
{
    public const int ExpectedApiVersionMajor = 0;
    public const int ExpectedApiVersionMinor = 1;

    private const string Library = "3DTheater.Video.Native";
    private readonly int _channels;
    private nint _handle;

    private VideoWriter(nint handle, int channels)
    {
        _handle = handle;
        _channels = channels;
    }

    public static (int Major, int Minor) ApiVersion
    {
        get
        {
            uint version = tv_get_api_version();
            return ((int)(version >> 16), (int)(version & 0xFFFF));
        }
    }

    private nint Handle
    {
        get
        {
            ObjectDisposedException.ThrowIf(_handle == 0, this);
            return _handle;
        }
    }

    public static VideoWriter Create(string path, VideoSettings settings)
    {
        CheckVersion();
        nint handle;
        fixed (char* p = Path.GetFullPath(path))
        {
            var desc = new TvWriterDesc
            {
                StructSize = (uint)sizeof(TvWriterDesc),
                Width = (uint)settings.Width,
                Height = (uint)settings.Height,
                FrameRate = (uint)settings.FrameRate,
                VideoBitrate = (uint)settings.VideoBitrate,
                AudioSampleRate = (uint)settings.AudioSampleRate,
                AudioChannels = (uint)settings.AudioChannels,
                AudioBitrate = (uint)settings.AudioBitrate,
                Path = p,
            };
            Check(tv_writer_create(&desc, &handle));
        }
        return new VideoWriter(handle, settings.AudioChannels);
    }

    /// <summary>映像の 1 フレーム（BGRA・上の行から）。</summary>
    public void WriteVideo(ReadOnlySpan<byte> bgra, int stride, long frameIndex)
    {
        fixed (byte* p = bgra)
        {
            Check(tv_writer_write_video(Handle, p, stride, frameIndex));
        }
    }

    /// <summary>音声の続き（16 ビット、チャンネルを交互）。</summary>
    public void WriteAudio(ReadOnlySpan<short> interleaved)
    {
        int frames = interleaved.Length / _channels;
        if (frames == 0)
        {
            return;
        }
        fixed (short* p = interleaved)
        {
            Check(tv_writer_write_audio(Handle, p, frames));
        }
    }

    /// <summary>書き終える（ファイルを閉じる）。</summary>
    public void Finish() => Check(tv_writer_finish(Handle));

    public void Dispose()
    {
        if (_handle == 0)
        {
            return;
        }
        tv_writer_destroy(_handle);
        _handle = 0;
    }

    /// <summary>動画のファイルの中身を調べる。</summary>
    public static VideoProbe Probe(string path)
    {
        CheckVersion();
        var info = new TvProbeInfo { StructSize = (uint)sizeof(TvProbeInfo) };
        fixed (char* p = Path.GetFullPath(path))
        {
            Check(tv_probe_file(p, &info, null, 0, 0));
        }
        return ToProbe(info);
    }

    /// <summary>動画の最初のフレームを読む（BGRA・上の行から、幅 × 4 バイトで 1 行）。</summary>
    public static byte[] ReadFirstFrame(string path, out VideoProbe probe)
    {
        probe = Probe(path);
        var pixels = new byte[probe.Width * probe.Height * 4];
        var info = new TvProbeInfo { StructSize = (uint)sizeof(TvProbeInfo) };
        fixed (char* p = Path.GetFullPath(path))
        fixed (byte* buffer = pixels)
        {
            Check(tv_probe_file(p, &info, buffer, probe.Width * 4, pixels.Length));
        }
        return pixels;
    }

    private static VideoProbe ToProbe(in TvProbeInfo info) => new((int)info.Width, (int)info.Height, info.FrameRate, info.DurationSeconds,
        info.HasAudio != 0, (int)info.AudioSampleRate, (int)info.AudioChannels);

    private static void CheckVersion()
    {
        var (major, minor) = ApiVersion;
        if (major != ExpectedApiVersionMajor || minor < ExpectedApiVersionMinor)
        {
            throw new VideoException(-4, $"動画の DLL の版が合いません（DLL: {major}.{minor}、必要: {ExpectedApiVersionMajor}.{ExpectedApiVersionMinor} 以上）");
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
        string message = length > 0 ? new string(buffer, 0, Math.Min(length, 511)) : "動画の書き出しでエラーが発生しました";
        throw new VideoException(result, message);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct TvWriterDesc
    {
        public uint StructSize;
        public uint Width;
        public uint Height;
        public uint FrameRate;
        public uint VideoBitrate;
        public uint AudioSampleRate;
        public uint AudioChannels;
        public uint AudioBitrate;
        public char* Path;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct TvProbeInfo
    {
        public uint StructSize;
        public uint Width;
        public uint Height;
        public uint HasAudio;
        public uint AudioSampleRate;
        public uint AudioChannels;
        public double FrameRate;
        public double DurationSeconds;
    }

    [LibraryImport(Library)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    private static partial uint tv_get_api_version();

    [LibraryImport(Library)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    private static partial int tv_get_last_error_message(char* buffer, int capacity);

    [LibraryImport(Library)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    private static partial int tv_writer_create(TvWriterDesc* desc, nint* writer);

    [LibraryImport(Library)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    private static partial int tv_writer_write_video(nint writer, void* bgra, int stride, long frameIndex);

    [LibraryImport(Library)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    private static partial int tv_writer_write_audio(nint writer, short* samples, int frameCount);

    [LibraryImport(Library)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    private static partial int tv_writer_finish(nint writer);

    [LibraryImport(Library)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    private static partial int tv_writer_destroy(nint writer);

    [LibraryImport(Library)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    private static partial int tv_probe_file(char* path, TvProbeInfo* info, void* firstFrame, int stride, int bufferSize);
}
