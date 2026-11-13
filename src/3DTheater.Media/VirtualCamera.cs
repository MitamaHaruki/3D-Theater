using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Theater3D.Media;

/// <summary>NV12 にするときの色の決まり。</summary>
public enum YuvMatrix
{
    /// <summary>HD の動画の決まり（動画の書き出し）。</summary>
    Bt709 = 0,

    /// <summary>カメラの映像として多くのアプリが読む決まり（仮想カメラ）。</summary>
    Bt601 = 1,
}

/// <summary>色の形の変換（C++ の DLL で行う）。</summary>
public static unsafe partial class VideoConvert
{
    /// <summary>
    /// BGRA（上の行から）を NV12（明るさ 16〜235・色差 16〜240）にする。幅と高さは偶数。
    /// <paramref name="nv12"/> には width × height × 3 / 2 バイト以上を渡す。
    /// </summary>
    public static void BgraToNv12(ReadOnlySpan<byte> bgra, int stride, int width, int height, Span<byte> nv12, YuvMatrix matrix)
    {
        VideoNative.CheckVersion(3, "色の変換");
        if ((long)stride * height > bgra.Length)
        {
            throw new ArgumentException("BGRA の絵が小さすぎます", nameof(bgra));
        }
        fixed (byte* source = bgra)
        fixed (byte* destination = nv12)
        {
            VideoNative.Check(tv_convert_bgra_to_nv12(source, stride, width, height, destination, nv12.Length, (int)matrix));
        }
    }

    [LibraryImport(VideoNative.Library)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    private static partial int tv_convert_bgra_to_nv12(byte* bgra, int stride, int width, int height, byte* nv12, int nv12Size, int matrix);
}

/// <summary>
/// 仮想カメラ（Windows 11 の仕組み）。作ってから壊すまで、この利用者のほかのアプリのカメラの一覧に「3D Theater」が出る。
/// カメラの元（<see cref="VirtualCameraProtocol.DllName"/>）を、先に管理者の権限で登録しておくこと。
/// 作ったスレッドで壊すこと（COM の決まり）。作るのに 1 秒ほどかかることがあるので、画面のスレッドでは作らない。
/// </summary>
public sealed unsafe partial class VirtualCameraDevice : IDisposable
{
    private nint _handle;

    private VirtualCameraDevice(nint handle) => _handle = handle;

    /// <summary>この Windows で仮想カメラを作れるか（Windows 11 以降）。</summary>
    public static bool IsSupported
    {
        get
        {
            try
            {
                VideoNative.CheckVersion(3, "仮想カメラ");
                return tv_virtual_camera_supported() != 0;
            }
            catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or VideoException)
            {
                return false;
            }
        }
    }

    /// <summary>仮想カメラを作って出す。</summary>
    public static VirtualCameraDevice Create(string friendlyName = VirtualCameraProtocol.FriendlyName, string clsid = VirtualCameraProtocol.Clsid)
    {
        VideoNative.CheckVersion(3, "仮想カメラ");
        nint handle;
        fixed (char* name = friendlyName)
        fixed (char* id = clsid)
        {
            VideoNative.Check(tv_virtual_camera_create(name, id, &handle));
        }
        return new VirtualCameraDevice(handle);
    }

    /// <summary>仮想カメラをしまう（ほかのアプリの一覧から消える）。</summary>
    public void Dispose()
    {
        if (_handle != 0)
        {
            tv_virtual_camera_destroy(_handle);
            _handle = 0;
        }
    }

    [LibraryImport(VideoNative.Library)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    private static partial int tv_virtual_camera_supported();

    [LibraryImport(VideoNative.Library)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    private static partial int tv_virtual_camera_create(char* friendlyName, char* clsid, nint* camera);

    [LibraryImport(VideoNative.Library)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    private static partial int tv_virtual_camera_destroy(nint camera);
}

/// <summary>映像の C++ の DLL の共通の確かめ。</summary>
internal static unsafe partial class VideoNative
{
    public const string Library = "3DTheater.Video.Native";

    /// <summary>DLL の版が、<paramref name="minor"/> 以上か確かめる。</summary>
    public static void CheckVersion(int minor, string purpose)
    {
        var (major, actual) = VideoWriter.ApiVersion;
        if (major != VideoWriter.ExpectedApiVersionMajor || actual < minor)
        {
            throw new VideoException(-4, $"動画の DLL の版が合いません（DLL: {major}.{actual}、{purpose}に必要: {VideoWriter.ExpectedApiVersionMajor}.{minor} 以上）");
        }
    }

    public static void Check(int result)
    {
        if (result >= 0)
        {
            return;
        }
        char* buffer = stackalloc char[512];
        int length = tv_get_last_error_message(buffer, 512);
        string message = length > 0 ? new string(buffer, 0, Math.Min(length, 511)) : "映像の処理でエラーが発生しました";
        throw new VideoException(result, message);
    }

    [LibraryImport(Library)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    private static partial int tv_get_last_error_message(char* buffer, int capacity);
}
