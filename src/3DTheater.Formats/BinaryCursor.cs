using System.Buffers.Binary;
using System.Numerics;
using System.Text;

namespace Theater3D.Formats;

/// <summary>
/// バイト列を先頭から順に読む（すべてリトルエンディアン）。
/// 読もうとした長さがファイルの残りより長ければ、どこを読んでいたかを添えて <see cref="ModelFormatException"/> を投げる。
/// </summary>
internal sealed class BinaryCursor(byte[] data)
{
    private int _position;

    public int Position => _position;

    public int Remaining => data.Length - _position;

    /// <summary>今読んでいる場所の説明（エラーの文に使う。例:「頂点 12 番」）。</summary>
    public string Section { get; set; } = "ヘッダー";

    public ReadOnlySpan<byte> Take(int length)
    {
        if (length < 0 || length > Remaining)
        {
            throw new ModelFormatException(
                $"{Section}を読んでいる途中でファイルが終わりました（位置 {_position:N0} バイト目、残り {Remaining:N0} バイトに {length:N0} バイトが必要）。ファイルが壊れているか、途中までしか保存されていない可能性があります");
        }
        var span = new ReadOnlySpan<byte>(data, _position, length);
        _position += length;
        return span;
    }

    public void Skip(int length) => Take(length);

    public byte ReadByte() => Take(1)[0];

    public sbyte ReadSByte() => unchecked((sbyte)Take(1)[0]);

    public short ReadInt16() => BinaryPrimitives.ReadInt16LittleEndian(Take(2));

    public ushort ReadUInt16() => BinaryPrimitives.ReadUInt16LittleEndian(Take(2));

    public int ReadInt32() => BinaryPrimitives.ReadInt32LittleEndian(Take(4));

    public uint ReadUInt32() => BinaryPrimitives.ReadUInt32LittleEndian(Take(4));

    public float ReadSingle() => BinaryPrimitives.ReadSingleLittleEndian(Take(4));

    public Vector2 ReadVector2() => new(ReadSingle(), ReadSingle());

    public Vector3 ReadVector3() => new(ReadSingle(), ReadSingle(), ReadSingle());

    public Vector4 ReadVector4() => new(ReadSingle(), ReadSingle(), ReadSingle(), ReadSingle());

    /// <summary>前に 4 バイトの長さが付いた文字列。</summary>
    public string ReadText(Encoding encoding)
    {
        int length = ReadInt32();
        if (length < 0 || length > Remaining)
        {
            throw new ModelFormatException($"{Section}の文字列の長さ（{length:N0}）が正しくありません（位置 {_position - 4:N0} バイト目）");
        }
        return encoding.GetString(Take(length));
    }

    /// <summary>
    /// 要素の数。1 要素が少なくとも <paramref name="minimumBytesPerItem"/> バイトあるとして、残りに収まらない数なら壊れているとみなす。
    /// </summary>
    public int ReadCount(string what, int minimumBytesPerItem)
    {
        int count = ReadInt32();
        if (count < 0)
        {
            throw new ModelFormatException($"{what}の数（{count:N0}）が負になっています（位置 {_position - 4:N0} バイト目）。ファイルが壊れている可能性があります");
        }
        if ((long)count * minimumBytesPerItem > Remaining)
        {
            throw new ModelFormatException(
                $"{what}の数（{count:N0}）に対して、ファイルの残り（{Remaining:N0} バイト）が足りません（位置 {_position - 4:N0} バイト目）。ファイルが途中で切れているか、壊れている可能性があります");
        }
        return count;
    }
}
