using System.Buffers.Binary;
using System.Globalization;
using System.IO.Compression;
using System.Text;
using Theater3D.Formats.Vmd;

namespace Theater3D.Formats.XFile;

/// <summary>.x の字の種類（テキストとバイナリで共通）。</summary>
internal enum XTokenKind
{
    Name,
    String,
    Integer,
    Float,
    OpenBrace,
    CloseBrace,
    Separator,
    Guid,
    Template,
    Other,
}

internal readonly record struct XToken(XTokenKind Kind, string? Text = null, double Number = 0);

/// <summary>.x の頭（16 バイト）。形式（txt・bin・tzip・bzip）と小数の大きさ（32・64 ビット）。</summary>
internal readonly record struct XHeader(string Format, int FloatBits)
{
    public const int Size = 16;

    public bool IsCompressed => Format is "tzip" or "bzip";

    public bool IsBinary => Format is "bin " or "bzip";

    /// <summary>画面に出す形式の名前。</summary>
    public string DisplayName => Format switch
    {
        "txt " => "テキスト",
        "bin " => "バイナリ",
        "tzip" => "圧縮したテキスト",
        "bzip" => "圧縮したバイナリ",
        _ => Format.Trim(),
    };

    public static XHeader? Parse(ReadOnlySpan<byte> data)
    {
        if (data.Length < Size || data[0] != 'x' || data[1] != 'o' || data[2] != 'f' || data[3] != ' ')
        {
            return null;
        }
        string format = Encoding.ASCII.GetString(data[8..12]);
        string bits = Encoding.ASCII.GetString(data[12..16]);
        if (format is not ("txt " or "bin " or "tzip" or "bzip") || bits is not ("0032" or "0064"))
        {
            return null;
        }
        return new XHeader(format, bits == "0064" ? 64 : 32);
    }
}

/// <summary>.x を字に分ける（テキスト・バイナリ）。圧縮した形式は、先に <see cref="MsZip"/> でほどく。</summary>
internal static class XFileTokenizer
{
    public static List<XToken> Tokenize(ReadOnlySpan<byte> body, XHeader header) =>
        header.IsBinary ? Binary(body, header.FloatBits) : Text(body);

    // ===== テキスト =====
    private static List<XToken> Text(ReadOnlySpan<byte> s)
    {
        var tokens = new List<XToken>();
        int i = 0;
        while (i < s.Length)
        {
            byte c = s[i];
            if (c is (byte)' ' or (byte)'\t' or (byte)'\r' or (byte)'\n' or 0)
            {
                i++;
                continue;
            }
            if (c == '#' || (c == '/' && i + 1 < s.Length && s[i + 1] == '/'))
            {
                while (i < s.Length && s[i] != '\n')
                {
                    i++;
                }
                continue;
            }
            switch (c)
            {
                case (byte)'{':
                    tokens.Add(new XToken(XTokenKind.OpenBrace));
                    i++;
                    continue;
                case (byte)'}':
                    tokens.Add(new XToken(XTokenKind.CloseBrace));
                    i++;
                    continue;
                case (byte)',' or (byte)';':
                    tokens.Add(new XToken(XTokenKind.Separator));
                    i++;
                    continue;
                case (byte)'[' or (byte)']' or (byte)'(' or (byte)')':
                    tokens.Add(new XToken(XTokenKind.Other));
                    i++;
                    continue;
                case (byte)'"':
                {
                    int end = s[(i + 1)..].IndexOf((byte)'"');
                    if (end < 0)
                    {
                        throw new ModelFormatException("文字列の終わり（\"）がありません");
                    }
                    tokens.Add(new XToken(XTokenKind.String, VmdText.ShiftJis.GetString(s.Slice(i + 1, end))));
                    i += end + 2;
                    continue;
                }
                case (byte)'<':
                {
                    int end = s[i..].IndexOf((byte)'>');
                    if (end < 0)
                    {
                        throw new ModelFormatException("GUID の終わり（>）がありません");
                    }
                    tokens.Add(new XToken(XTokenKind.Guid, Encoding.ASCII.GetString(s.Slice(i + 1, end - 1))));
                    i += end + 1;
                    continue;
                }
            }
            if (c is (byte)'-' or (byte)'+' || (c >= '0' && c <= '9') || (c == '.' && i + 1 < s.Length && s[i + 1] is >= (byte)'0' and <= (byte)'9'))
            {
                int start = i;
                bool isFloat = false;
                i++;
                while (i < s.Length && (s[i] is >= (byte)'0' and <= (byte)'9' or (byte)'.' or (byte)'e' or (byte)'E'
                       || (s[i] is (byte)'-' or (byte)'+' && s[i - 1] is (byte)'e' or (byte)'E')))
                {
                    isFloat |= s[i] is (byte)'.' or (byte)'e' or (byte)'E';
                    i++;
                }
                string text = Encoding.ASCII.GetString(s[start..i]);
                if (!double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out double value))
                {
                    throw new ModelFormatException($"数として読めません: {text}");
                }
                tokens.Add(new XToken(isFloat ? XTokenKind.Float : XTokenKind.Integer, Number: value));
                continue;
            }
            {
                // 名前（英字・数字・_・- と、日本語などの 2 バイト文字）
                int start = i;
                while (i < s.Length && s[i] is not ((byte)' ' or (byte)'\t' or (byte)'\r' or (byte)'\n' or 0
                       or (byte)'{' or (byte)'}' or (byte)',' or (byte)';' or (byte)'"' or (byte)'<' or (byte)'[' or (byte)']' or (byte)'(' or (byte)')'))
                {
                    i++;
                }
                if (i == start)
                {
                    i++;
                    continue;
                }
                string name = VmdText.ShiftJis.GetString(s[start..i]);
                tokens.Add(name == "template" ? new XToken(XTokenKind.Template) : new XToken(XTokenKind.Name, name));
            }
        }
        return tokens;
    }

    // ===== バイナリ（DirectX の .x のバイナリ形式。字の種類を WORD で持つ） =====
    private const int TokenName = 1;
    private const int TokenString = 2;
    private const int TokenInteger = 3;
    private const int TokenGuid = 5;
    private const int TokenIntegerList = 6;
    private const int TokenFloatList = 7;
    private const int TokenOpenBrace = 10;
    private const int TokenCloseBrace = 11;
    private const int TokenComma = 19;
    private const int TokenSemicolon = 20;
    private const int TokenTemplate = 31;

    private static List<XToken> Binary(ReadOnlySpan<byte> s, int floatBits)
    {
        var tokens = new List<XToken>();
        int i = 0;
        while (i + 2 <= s.Length)
        {
            int token = BinaryPrimitives.ReadUInt16LittleEndian(s[i..]);
            i += 2;
            switch (token)
            {
                case TokenName:
                {
                    int count = ReadCount(s, ref i);
                    tokens.Add(new XToken(XTokenKind.Name, VmdText.ShiftJis.GetString(Take(s, ref i, count))));
                    break;
                }
                case TokenString:
                {
                    int count = ReadCount(s, ref i);
                    string text = VmdText.ShiftJis.GetString(Take(s, ref i, count)).TrimEnd('\0');
                    Take(s, ref i, 2); // 終わりの印（; か ,）
                    tokens.Add(new XToken(XTokenKind.String, text));
                    break;
                }
                case TokenInteger:
                    tokens.Add(new XToken(XTokenKind.Integer, Number: BinaryPrimitives.ReadUInt32LittleEndian(Take(s, ref i, 4))));
                    break;
                case TokenGuid:
                    Take(s, ref i, 16);
                    tokens.Add(new XToken(XTokenKind.Guid));
                    break;
                case TokenIntegerList:
                {
                    int count = ReadCount(s, ref i);
                    ReadOnlySpan<byte> list = Take(s, ref i, checked(count * 4));
                    for (int k = 0; k < count; k++)
                    {
                        tokens.Add(new XToken(XTokenKind.Integer, Number: BinaryPrimitives.ReadUInt32LittleEndian(list[(k * 4)..])));
                    }
                    break;
                }
                case TokenFloatList:
                {
                    int count = ReadCount(s, ref i);
                    int size = floatBits / 8;
                    ReadOnlySpan<byte> list = Take(s, ref i, checked(count * size));
                    for (int k = 0; k < count; k++)
                    {
                        double value = size == 8 ? BinaryPrimitives.ReadDoubleLittleEndian(list[(k * 8)..]) : BinaryPrimitives.ReadSingleLittleEndian(list[(k * 4)..]);
                        tokens.Add(new XToken(XTokenKind.Float, Number: value));
                    }
                    break;
                }
                case TokenOpenBrace:
                    tokens.Add(new XToken(XTokenKind.OpenBrace));
                    break;
                case TokenCloseBrace:
                    tokens.Add(new XToken(XTokenKind.CloseBrace));
                    break;
                case TokenComma or TokenSemicolon:
                    tokens.Add(new XToken(XTokenKind.Separator));
                    break;
                case TokenTemplate:
                    tokens.Add(new XToken(XTokenKind.Template));
                    break;
                case >= 12 and <= 18 or >= 40 and <= 53:
                    tokens.Add(new XToken(XTokenKind.Other)); // 括弧・型の名前（テンプレートの中だけに出る）
                    break;
                default:
                    throw new ModelFormatException($"バイナリの .x に知らない字の種類（{token}）があります（{i - 2} バイト目）");
            }
        }
        return tokens;
    }

    private static int ReadCount(ReadOnlySpan<byte> s, ref int i)
    {
        uint count = BinaryPrimitives.ReadUInt32LittleEndian(Take(s, ref i, 4));
        if (count > int.MaxValue / 8)
        {
            throw new ModelFormatException("バイナリの .x の数が大きすぎます（壊れている可能性があります）");
        }
        return (int)count;
    }

    private static ReadOnlySpan<byte> Take(ReadOnlySpan<byte> s, ref int i, int count)
    {
        if (count < 0 || i + count > s.Length)
        {
            throw new ModelFormatException("バイナリの .x が途中で終わっています");
        }
        ReadOnlySpan<byte> part = s.Slice(i, count);
        i += count;
        return part;
    }
}

/// <summary>
/// 圧縮した .x（tzip・bzip）をほどく（MSZIP）。頭の後ろに、ほどいた後の大きさ（DWORD）と、ブロックが続く。
/// ブロックは「ほどいた大きさ（WORD）・縮めた大きさ（WORD。"CK" を含む）・"CK"・Deflate のデータ」。
/// Deflate のデータは、前のブロックの中身（最後の 32 KB まで）を参照してよい。.NET の Deflate には前の中身を渡す口が無いので、
/// 前の中身を「縮めないブロック」にして前に付けたデータとしてほどき、その分を捨てる。
/// </summary>
internal static class MsZip
{
    private const int History = 32 * 1024;

    public static byte[] Decompress(ReadOnlySpan<byte> data, LoadReport report)
    {
        if (data.Length < 4)
        {
            throw new ModelFormatException("圧縮した .x が短すぎます");
        }
        uint expected = BinaryPrimitives.ReadUInt32LittleEndian(data);
        var output = new MemoryStream();
        int i = 4;
        while (i + 4 <= data.Length)
        {
            int plain = BinaryPrimitives.ReadUInt16LittleEndian(data[i..]);
            int packed = BinaryPrimitives.ReadUInt16LittleEndian(data[(i + 2)..]);
            i += 4;
            if (packed < 2 || i + packed > data.Length || data[i] != 'C' || data[i + 1] != 'K')
            {
                throw new ModelFormatException($"圧縮した .x のブロックが壊れています（{i} バイト目）");
            }
            ReadOnlySpan<byte> deflate = data.Slice(i + 2, packed - 2);
            i += packed;

            int historyLength = (int)Math.Min(History, output.Length);
            var input = new MemoryStream(historyLength + 5 + deflate.Length);
            if (historyLength > 0)
            {
                // 縮めないブロック（最後ではない）: 頭 0、長さ、長さの補数、中身
                input.WriteByte(0);
                input.WriteByte((byte)historyLength);
                input.WriteByte((byte)(historyLength >> 8));
                input.WriteByte((byte)~historyLength);
                input.WriteByte((byte)(~historyLength >> 8));
                input.Write(output.GetBuffer(), (int)output.Length - historyLength, historyLength);
            }
            input.Write(deflate);
            input.Position = 0;
            byte[] block = new byte[historyLength + plain];
            using (var inflate = new DeflateStream(input, CompressionMode.Decompress))
            {
                int read = 0;
                while (read < block.Length)
                {
                    int n = inflate.Read(block, read, block.Length - read);
                    if (n == 0)
                    {
                        throw new ModelFormatException("圧縮した .x のブロックが途中で終わっています");
                    }
                    read += n;
                }
            }
            output.Write(block, historyLength, plain);
        }
        // ほどいた後の大きさは、頭（16 バイト）を含む数のことがある
        if (expected != output.Length && expected != output.Length + XHeader.Size)
        {
            report.Info(LoadIssueCategory.Note, $"圧縮した .x の、ほどいた後の大きさの書き込み（{expected:N0}）と、実際の大きさ（{output.Length:N0}）が合いません");
        }
        return output.ToArray();
    }
}
