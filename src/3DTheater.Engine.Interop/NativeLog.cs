using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Theater3D.Engine.Interop;

public enum NativeLogLevel
{
    Debug = 0,
    Info = 1,
    Warning = 2,
    Error = 3,
}

/// <summary>ネイティブエンジンが出したログを受け取る。どのスレッドから届くかは決まっていない。</summary>
public static unsafe class NativeLog
{
    private static int s_registered;

    public static event Action<NativeLogLevel, string>? MessageReceived;

    internal static void EnsureRegistered()
    {
        if (Interlocked.Exchange(ref s_registered, 1) == 0)
        {
            NativeMethods.SetLogCallback(&OnNativeLog, 0);
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void OnNativeLog(int level, char* message, nint userData)
    {
        try
        {
            MessageReceived?.Invoke((NativeLogLevel)level, new string(message));
        }
        catch
        {
            // ネイティブ側へ例外を戻すとプロセスが落ちるため、ここで止める
        }
    }
}
