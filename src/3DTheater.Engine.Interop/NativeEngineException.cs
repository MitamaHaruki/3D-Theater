namespace Theater3D.Engine.Interop;

/// <summary>tt_engine.h の TtResult と同じ値。0 以上は成功、負の値は失敗。</summary>
public enum NativeResult
{
    Ok = 0,
    NotReady = 1,
    InvalidArgument = -1,
    InvalidState = -2,
    WrongThread = -3,
    NotSupported = -4,
    OutOfMemory = -5,
    DeviceLost = -6,
    Graphics = -7,
    Unknown = -100,
}

/// <summary>ネイティブエンジンが失敗を返したときの例外。<see cref="Result"/> で種類を見分ける。</summary>
public sealed class NativeEngineException(NativeResult result, string message) : Exception($"{message}（{result}）")
{
    public NativeResult Result { get; } = result;
}

internal static unsafe class NativeError
{
    private const int MessageCapacity = 512;

    internal static NativeResult Check(int result)
    {
        if (result < 0)
        {
            throw Create(result);
        }
        return (NativeResult)result;
    }

    // エラーの説明はスレッドごとに保存されるので、失敗した呼び出しと同じスレッドで取り出す
    private static NativeEngineException Create(int result)
    {
        char* buffer = stackalloc char[MessageCapacity];
        int length = NativeMethods.GetLastErrorMessage(buffer, MessageCapacity);
        string message = length > 0
            ? new string(buffer, 0, Math.Min(length, MessageCapacity - 1))
            : "ネイティブエンジンでエラーが発生しました";
        return new NativeEngineException((NativeResult)result, message);
    }
}
