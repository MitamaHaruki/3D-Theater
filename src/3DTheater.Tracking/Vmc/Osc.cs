using System.Buffers.Binary;
using System.Text;

namespace Theater3D.Tracking.Vmc;

/// <summary>
/// OSC（Open Sound Control）の 1 つの知らせ。宛名（「/VMC/Ext/Blend/Val」など）と、値（int・float・文字）の並び。
/// VMC プロトコルは OSC の上の決まり（設計判断記録 98 章）。
/// </summary>
public sealed record OscMessage(string Address, IReadOnlyList<object> Arguments)
{
    public OscMessage(string address, params object[] arguments)
        : this(address, (IReadOnlyList<object>)arguments)
    {
    }

    /// <summary><paramref name="index"/> 番目の値を文字として（無い・違う型なら null）。</summary>
    public string? String(int index) => index < Arguments.Count ? Arguments[index] as string : null;

    /// <summary><paramref name="index"/> 番目の値を数として（int も float にする。無い・違う型なら null）。</summary>
    public float? Float(int index) => index < Arguments.Count
        ? Arguments[index] switch
        {
            float f => f,
            int i => i,
            _ => null,
        }
        : null;

    /// <summary><paramref name="index"/> 番目の値を整数として（無い・違う型なら null）。</summary>
    public int? Int(int index) => index < Arguments.Count && Arguments[index] is int i ? i : null;
}

/// <summary>
/// OSC の書き方・読み方（知らせと、まとめて送る「バンドル」）。数は大きい桁から（ビッグエンディアン）、文字は UTF-8 で、
/// どちらも 4 バイトの区切りに合わせて 0 で埋める。値の型は int（i）・float（f）・文字（s）だけを扱う（VMC プロトコルが使うもの）。
/// </summary>
public static class Osc
{
    private const string BundleTag = "#bundle";

    /// <summary>知らせ 1 つを書く。</summary>
    public static byte[] Write(OscMessage message)
    {
        using var stream = new MemoryStream(64);
        WriteMessage(stream, message);
        return stream.ToArray();
    }

    /// <summary>
    /// 知らせをまとめて 1 つのバンドルに書く（受け手は 1 回で受け取る。時刻の印は「すぐ」）。
    /// </summary>
    public static byte[] WriteBundle(IEnumerable<OscMessage> messages)
    {
        using var stream = new MemoryStream(1024);
        WriteString(stream, BundleTag);
        Span<byte> immediately = stackalloc byte[8];
        BinaryPrimitives.WriteUInt64BigEndian(immediately, 1); // OSC の「すぐ」
        stream.Write(immediately);
        Span<byte> size = stackalloc byte[4];
        foreach (OscMessage message in messages)
        {
            byte[] element = Write(message);
            BinaryPrimitives.WriteInt32BigEndian(size, element.Length);
            stream.Write(size);
            stream.Write(element);
        }
        return stream.ToArray();
    }

    /// <summary>届いたもの（知らせ 1 つか、バンドル）を読み、中の知らせを並べる。読めない所は飛ばす（壊れたものは空）。</summary>
    public static IReadOnlyList<OscMessage> Read(ReadOnlySpan<byte> data)
    {
        var messages = new List<OscMessage>();
        try
        {
            ReadPacket(data, messages, depth: 0);
        }
        catch (FormatException)
        {
            // 途中までに読めたものは使う
        }
        return messages;
    }

    private static void ReadPacket(ReadOnlySpan<byte> data, List<OscMessage> messages, int depth)
    {
        if (data.Length < 4 || depth > 4)
        {
            return;
        }
        if (data[0] == (byte)'#')
        {
            int offset = 0;
            if (ReadString(data, ref offset) != BundleTag)
            {
                return;
            }
            offset += 8; // 時刻の印は使わない（届いたらすぐ使う）
            while (offset + 4 <= data.Length)
            {
                int size = BinaryPrimitives.ReadInt32BigEndian(data[offset..]);
                offset += 4;
                if (size <= 0 || size % 4 != 0 || offset + size > data.Length)
                {
                    throw new FormatException("バンドルの中の大きさが合いません");
                }
                ReadPacket(data.Slice(offset, size), messages, depth + 1);
                offset += size;
            }
            return;
        }
        if (data[0] == (byte)'/')
        {
            messages.Add(ReadMessage(data));
        }
    }

    private static OscMessage ReadMessage(ReadOnlySpan<byte> data)
    {
        int offset = 0;
        string address = ReadString(data, ref offset);
        if (offset >= data.Length)
        {
            return new OscMessage(address, Array.Empty<object>()); // 型の並びが無い（古い書き方）
        }
        string tags = ReadString(data, ref offset);
        if (tags.Length == 0 || tags[0] != ',')
        {
            throw new FormatException("型の並びがありません");
        }
        var arguments = new List<object>(tags.Length - 1);
        foreach (char tag in tags.AsSpan(1))
        {
            switch (tag)
            {
                case 'i':
                    arguments.Add(BinaryPrimitives.ReadInt32BigEndian(Take(data, ref offset, 4)));
                    break;
                case 'f':
                    arguments.Add(BinaryPrimitives.ReadSingleBigEndian(Take(data, ref offset, 4)));
                    break;
                case 's':
                    arguments.Add(ReadString(data, ref offset));
                    break;
                case 'T':
                    arguments.Add(1);
                    break;
                case 'F':
                case 'N':
                    arguments.Add(0);
                    break;
                default:
                    throw new FormatException($"知らない型「{tag}」");
            }
        }
        return new OscMessage(address, arguments);
    }

    private static ReadOnlySpan<byte> Take(ReadOnlySpan<byte> data, ref int offset, int count)
    {
        if (offset + count > data.Length)
        {
            throw new FormatException("値が足りません");
        }
        ReadOnlySpan<byte> slice = data.Slice(offset, count);
        offset += count;
        return slice;
    }

    private static string ReadString(ReadOnlySpan<byte> data, ref int offset)
    {
        int end = data[offset..].IndexOf((byte)0);
        if (end < 0)
        {
            throw new FormatException("文字の終わりがありません");
        }
        string text = Encoding.UTF8.GetString(data.Slice(offset, end));
        offset += Padded(end + 1);
        return text;
    }

    private static void WriteMessage(Stream stream, OscMessage message)
    {
        WriteString(stream, message.Address);
        var tags = new StringBuilder(",", message.Arguments.Count + 1);
        foreach (object argument in message.Arguments)
        {
            tags.Append(argument switch
            {
                int => 'i',
                float => 'f',
                string => 's',
                _ => throw new ArgumentException($"OSC で書けない値です: {argument?.GetType().Name}", nameof(message)),
            });
        }
        WriteString(stream, tags.ToString());
        Span<byte> four = stackalloc byte[4];
        foreach (object argument in message.Arguments)
        {
            switch (argument)
            {
                case int i:
                    BinaryPrimitives.WriteInt32BigEndian(four, i);
                    stream.Write(four);
                    break;
                case float f:
                    BinaryPrimitives.WriteSingleBigEndian(four, f);
                    stream.Write(four);
                    break;
                case string s:
                    WriteString(stream, s);
                    break;
            }
        }
    }

    private static void WriteString(Stream stream, string text)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(text);
        stream.Write(bytes);
        int padding = Padded(bytes.Length + 1) - bytes.Length;
        for (int i = 0; i < padding; i++)
        {
            stream.WriteByte(0);
        }
    }

    private static int Padded(int length) => (length + 3) & ~3;
}
