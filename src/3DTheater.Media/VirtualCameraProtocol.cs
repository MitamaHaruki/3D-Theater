using System.Buffers.Binary;

namespace Theater3D.Media;

/// <summary>
/// 仮想カメラの受け渡しの決まり（src/3DTheater.VirtualCamera/include/tvc_protocol.h と同じ値）。
/// カメラの元の DLL（Windows のカメラの仕組みの中で動く）は、アプリが作った名前付きパイプにつなぎ、「この大きさの次の絵」を頼む
/// （<see cref="Request"/>）。アプリは見出し（<see cref="FrameHeader"/>）と、NV12（明るさ 16〜235。色の決まりは <see cref="MatrixFor"/>）の中身を返す。
/// </summary>
public static class VirtualCameraProtocol
{
    /// <summary>パイプの名前（\\.\pipe\ の後ろ）。</summary>
    public const string PipeName = "3DTheater.VirtualCamera.1";

    /// <summary>カメラの元（COM の部品）の CLSID。</summary>
    public const string Clsid = "{3FE7E0FA-DDA4-4859-9D35-C93F349735E4}";

    /// <summary>ほかのアプリのカメラの一覧に出る名前（Windows が後ろに「Windows Virtual Camera」などを付けることがある）。</summary>
    public const string FriendlyName = "3D Theater";

    /// <summary>カメラの元の DLL のファイル名。</summary>
    public const string DllName = "3DTheater.VirtualCamera.dll";

    public const uint RequestMagic = 0x51435654; // "TVCQ"
    public const uint FrameMagic = 0x46435654;   // "TVCF"
    public const uint Version = 1;
    public const int RequestSize = 24;
    public const int HeaderSize = 32;

    /// <summary>頼まれた大きさの絵がまだ無い（中身なし）。</summary>
    public const uint FlagNoFrame = 1;

    public const int MinSize = 16;
    public const int MaxSize = 4096;
    public const int FrameRate = 30;

    /// <summary>この高さ以上は BT.709 にする。</summary>
    public const int HdMinHeight = 577;

    public static int Nv12Bytes(int width, int height) => width * height * 3 / 2;

    /// <summary>
    /// NV12 にするときの色の決まり。Windows はカメラの映像を色に戻すとき、形式に書いた決まりを見ずに、高さが 576 より大きければ BT.709、
    /// それ以下なら BT.601 で戻す（確かめて分かった。幅には関係しない）。それに合わせないと、緑が暗くなるなど色がずれる。
    /// </summary>
    public static YuvMatrix MatrixFor(int height) => height >= HdMinHeight ? YuvMatrix.Bt709 : YuvMatrix.Bt601;

    /// <summary>仮想カメラで使える大きさか（16〜4096 の偶数）。</summary>
    public static bool IsValidSize(int width, int height) =>
        width is >= MinSize and <= MaxSize && height is >= MinSize and <= MaxSize && width % 2 == 0 && height % 2 == 0;

    /// <summary>DLL からの頼み。大きさが 0 × 0 なら、アプリの今の大きさを聞くだけ。</summary>
    public readonly record struct Request(int Width, int Height, ulong LastSequence)
    {
        public bool IsSizeQuery => Width == 0 && Height == 0;

        public void Write(Span<byte> destination)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(destination, RequestMagic);
            BinaryPrimitives.WriteUInt32LittleEndian(destination[4..], Version);
            BinaryPrimitives.WriteUInt32LittleEndian(destination[8..], (uint)Width);
            BinaryPrimitives.WriteUInt32LittleEndian(destination[12..], (uint)Height);
            BinaryPrimitives.WriteUInt64LittleEndian(destination[16..], LastSequence);
        }

        /// <summary>読む（決まりと違えば null）。</summary>
        public static Request? Read(ReadOnlySpan<byte> source)
        {
            if (source.Length < RequestSize || BinaryPrimitives.ReadUInt32LittleEndian(source) != RequestMagic
                || BinaryPrimitives.ReadUInt32LittleEndian(source[4..]) != Version)
            {
                return null;
            }
            uint width = BinaryPrimitives.ReadUInt32LittleEndian(source[8..]);
            uint height = BinaryPrimitives.ReadUInt32LittleEndian(source[12..]);
            if (width > MaxSize || height > MaxSize)
            {
                return null;
            }
            return new Request((int)width, (int)height, BinaryPrimitives.ReadUInt64LittleEndian(source[16..]));
        }
    }

    /// <summary>アプリからの返事の見出し。続けて <see cref="PayloadSize"/> バイトの NV12 が来る。</summary>
    public readonly record struct FrameHeader(int Width, int Height, uint Flags, int PayloadSize, ulong Sequence)
    {
        public void Write(Span<byte> destination)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(destination, FrameMagic);
            BinaryPrimitives.WriteUInt32LittleEndian(destination[4..], Version);
            BinaryPrimitives.WriteUInt32LittleEndian(destination[8..], (uint)Width);
            BinaryPrimitives.WriteUInt32LittleEndian(destination[12..], (uint)Height);
            BinaryPrimitives.WriteUInt32LittleEndian(destination[16..], Flags);
            BinaryPrimitives.WriteUInt32LittleEndian(destination[20..], (uint)PayloadSize);
            BinaryPrimitives.WriteUInt64LittleEndian(destination[24..], Sequence);
        }

        public static FrameHeader? Read(ReadOnlySpan<byte> source)
        {
            if (source.Length < HeaderSize || BinaryPrimitives.ReadUInt32LittleEndian(source) != FrameMagic
                || BinaryPrimitives.ReadUInt32LittleEndian(source[4..]) != Version)
            {
                return null;
            }
            return new FrameHeader((int)BinaryPrimitives.ReadUInt32LittleEndian(source[8..]), (int)BinaryPrimitives.ReadUInt32LittleEndian(source[12..]),
                BinaryPrimitives.ReadUInt32LittleEndian(source[16..]), (int)BinaryPrimitives.ReadUInt32LittleEndian(source[20..]),
                BinaryPrimitives.ReadUInt64LittleEndian(source[24..]));
        }
    }
}
