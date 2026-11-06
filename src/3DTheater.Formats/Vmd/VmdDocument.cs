using System.Numerics;
using System.Text;

namespace Theater3D.Formats.Vmd;

// VMD（MMD のモーション）の中身をそのまま持つ。座標は PMX と同じ左手系・Y が上で、変換しない。
// 名前は Shift-JIS の決まった長さのバイト列で、MMD は「バイト列が同じか」で対応を決める。
// そのため名前は読めた文字列と元のバイト列の両方を残す（長い名前は途中で切れていることがある）。

/// <summary>VMD に書かれた名前（ボーン・モーフ・モデル）。</summary>
public readonly struct VmdName : IEquatable<VmdName>
{
    private readonly byte[] _bytes;
    private readonly string _text;

    public VmdName(byte[] bytes, string text)
    {
        _bytes = bytes;
        _text = text;
    }

    /// <summary>名前のバイト列（最初の 0 より前だけ）。</summary>
    public ReadOnlySpan<byte> Bytes => _bytes ?? [];

    public string Text => _text ?? string.Empty;

    /// <summary>モデルの名前と同じ名前か。MMD と同じく、Shift-JIS にして決まった長さで切ったバイト列で比べる。</summary>
    public bool Matches(string name, int maxBytes) => VmdText.Encode(name, maxBytes).AsSpan().SequenceEqual(Bytes);

    public bool Equals(VmdName other) => Bytes.SequenceEqual(other.Bytes);

    public override bool Equals(object? obj) => obj is VmdName other && Equals(other);

    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.AddBytes(Bytes);
        return hash.ToHashCode();
    }

    public override string ToString() => Text;
}

/// <summary>ボーンのキー 1 つ。<see cref="Interpolation"/> は前のキーからこのキーまでの動き方（64 バイトのまま）。</summary>
public sealed record VmdBoneKey(VmdName Bone, int Frame, Vector3 Translation, Quaternion Rotation, byte[] Interpolation)
{
    /// <summary>X・Y・Z の移動と回転の 4 本の曲線の制御点（0〜127）。MMD の決まった位置から取り出す。</summary>
    public (byte X1, byte Y1, byte X2, byte Y2) Curve(int channel) =>
        (Interpolation[channel], Interpolation[channel + 4], Interpolation[channel + 8], Interpolation[channel + 12]);
}

public sealed record VmdMorphKey(VmdName Morph, int Frame, float Weight);

/// <summary>カメラのキー。<see cref="Distance"/> は注視点からの距離（MMD では負の値で手前に置く）。回転はラジアン。</summary>
public sealed record VmdCameraKey(int Frame, float Distance, Vector3 Target, Vector3 Rotation, byte[] Interpolation, int FieldOfView, bool Orthographic);

public sealed record VmdLightKey(int Frame, Vector3 Color, Vector3 Direction);

public sealed record VmdShadowKey(int Frame, byte Mode, float Distance);

public readonly record struct VmdIkState(VmdName Bone, bool Enabled);

/// <summary>モデルの表示と IK の有効・無効のキー。</summary>
public sealed record VmdVisibilityKey(int Frame, bool Visible, IReadOnlyList<VmdIkState> IkStates);

public sealed class VmdDocument
{
    /// <summary>1（古い形式、モデル名 10 バイト）か 2（モデル名 20 バイト）。</summary>
    public int Version { get; init; } = 2;

    public VmdName ModelName { get; init; }

    public IReadOnlyList<VmdBoneKey> BoneKeys { get; init; } = [];

    public IReadOnlyList<VmdMorphKey> MorphKeys { get; init; } = [];

    public IReadOnlyList<VmdCameraKey> CameraKeys { get; init; } = [];

    public IReadOnlyList<VmdLightKey> LightKeys { get; init; } = [];

    public IReadOnlyList<VmdShadowKey> ShadowKeys { get; init; } = [];

    public IReadOnlyList<VmdVisibilityKey> VisibilityKeys { get; init; } = [];

    /// <summary>カメラ・照明用のモーション（ボーンもモーフも無く、カメラか照明のキーがある）か。</summary>
    public bool IsCameraMotion => BoneKeys.Count == 0 && MorphKeys.Count == 0 && (CameraKeys.Count > 0 || LightKeys.Count > 0);

    /// <summary>いちばん後ろのキーのフレーム（キーが無ければ 0）。</summary>
    public int LastFrame => new[]
    {
        BoneKeys.Count > 0 ? BoneKeys.Max(k => k.Frame) : 0,
        MorphKeys.Count > 0 ? MorphKeys.Max(k => k.Frame) : 0,
        CameraKeys.Count > 0 ? CameraKeys.Max(k => k.Frame) : 0,
        LightKeys.Count > 0 ? LightKeys.Max(k => k.Frame) : 0,
    }.Max();
}

/// <summary>VMD・VPD の文字（Shift-JIS）の扱い。</summary>
public static class VmdText
{
    public const int BoneNameBytes = 15;
    public const int MorphNameBytes = 15;
    public const int ModelNameBytes = 20;
    public const int IkNameBytes = 20;

    private static readonly Lazy<Encoding> ShiftJisLazy = new(() =>
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        return Encoding.GetEncoding(932, EncoderFallback.ReplacementFallback, DecoderFallback.ReplacementFallback);
    });

    public static Encoding ShiftJis => ShiftJisLazy.Value;

    /// <summary>名前を Shift-JIS にし、<paramref name="maxBytes"/> バイトで切る（MMD が書くときと同じ。2 バイト文字の途中で切れることもある）。</summary>
    public static byte[] Encode(string name, int maxBytes)
    {
        byte[] bytes = ShiftJis.GetBytes(name);
        return bytes.Length <= maxBytes ? bytes : bytes[..maxBytes];
    }

    /// <summary>決まった長さの欄から、最初の 0 までを名前として読む。途中で切れた 2 バイト文字は捨てる。</summary>
    public static VmdName Decode(ReadOnlySpan<byte> field)
    {
        int end = field.IndexOf((byte)0);
        ReadOnlySpan<byte> bytes = end < 0 ? field : field[..end];
        int usable = bytes.Length;
        // 最後のバイトが 2 バイト文字の 1 バイト目だけなら、文字にせずに落とす（バイト列は比べるために残す）
        int i = 0;
        while (i < bytes.Length)
        {
            if (IsLeadByte(bytes[i]))
            {
                if (i + 1 >= bytes.Length)
                {
                    usable = i;
                    break;
                }
                i += 2;
            }
            else
            {
                i++;
            }
        }
        return new VmdName(bytes.ToArray(), ShiftJis.GetString(bytes[..usable]));
    }

    private static bool IsLeadByte(byte b) => b is (>= 0x81 and <= 0x9F) or (>= 0xE0 and <= 0xFC);
}
