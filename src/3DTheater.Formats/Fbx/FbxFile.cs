using System.Buffers.Binary;
using System.Globalization;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Text;
using Theater3D.Formats.Vmd;

namespace Theater3D.Formats.Fbx;

/// <summary>
/// FBX の木の 1 つの節（名前・値の並び・子）。値は long・int・short・bool・float・double・string・byte[]（中身そのまま）か、
/// 数の配列（double[]・float[]・long[]・int[]・bool[]）。
/// </summary>
internal sealed class FbxNode(string name, object[] properties, List<FbxNode> children)
{
    public string Name { get; } = name;

    public object[] Properties { get; } = properties;

    public List<FbxNode> Children { get; } = children;

    public FbxNode? Child(string name) => Children.Find(c => c.Name == name);

    public IEnumerable<FbxNode> ChildrenNamed(string name) => Children.Where(c => c.Name == name);

    public long Long(int index, long fallback = 0) => index < Properties.Length && TryNumber(Properties[index], out double v) ? (long)v : fallback;

    public double Double(int index, double fallback = 0) => index < Properties.Length && TryNumber(Properties[index], out double v) ? v : fallback;

    public string String(int index) => index < Properties.Length && Properties[index] is string s ? s : string.Empty;

    /// <summary>子の 1 つ目の値（文字）。無ければ null。</summary>
    public string? ChildString(string name) => Child(name) is { Properties: [string s, ..] } ? s : null;

    /// <summary>子の 1 つ目の値（数）。無ければ <paramref name="fallback"/>。</summary>
    public double ChildDouble(string name, double fallback = 0) => Child(name) is { } c ? c.Double(0, fallback) : fallback;

    /// <summary>子の値の数の並び（配列か、数の値の並び）。無ければ null。</summary>
    public double[]? ChildDoubles(string name) => Child(name) is { } c ? c.Doubles() : null;

    public int[]? ChildInts(string name) => Child(name) is { } c ? c.Ints() : null;

    /// <summary>この節の値の数の並び（1 つ目が配列ならそれ、そうでなければ数の値を並べたもの）。</summary>
    public double[] Doubles() => Properties switch
    {
        [double[] d, ..] => d,
        [float[] f, ..] => [.. f.Select(v => (double)v)],
        [long[] l, ..] => [.. l.Select(v => (double)v)],
        [int[] i, ..] => [.. i.Select(v => (double)v)],
        _ => [.. Properties.Select(p => TryNumber(p, out double v) ? v : 0)],
    };

    public int[] Ints() => Properties switch
    {
        [int[] i, ..] => i,
        [long[] l, ..] => [.. l.Select(v => (int)v)],
        [double[] d, ..] => [.. d.Select(v => (int)Math.Round(v))],
        [float[] f, ..] => [.. f.Select(v => (int)Math.Round(v))],
        _ => [.. Properties.Select(p => TryNumber(p, out double v) ? (int)Math.Round(v) : 0)],
    };

    public static bool TryNumber(object value, out double result)
    {
        switch (value)
        {
            case double d: result = d; return true;
            case float f: result = f; return true;
            case long l: result = l; return true;
            case int i: result = i; return true;
            case short s: result = s; return true;
            case bool b: result = b ? 1 : 0; return true;
            default: result = 0; return false;
        }
    }
}

/// <summary>読んだ FBX（版と、いちばん上の節の並び）。</summary>
internal sealed record FbxDocument(int Version, bool IsBinary, List<FbxNode> Nodes)
{
    public FbxNode? Node(string name) => Nodes.Find(n => n.Name == name);
}

/// <summary>
/// FBX のファイルの木を読む（設計判断記録 83 章）。バイナリ（「Kaydara FBX Binary」で始まる。7.5 からは位置が 8 バイト）と、
/// 文字の FBX（ASCII）の両方。配列は zlib で縮めてあることがある。文字は UTF-8（読めなければ Shift_JIS）。
/// </summary>
internal static class FbxFile
{
    private static ReadOnlySpan<byte> BinaryMagic => "Kaydara FBX Binary  \0"u8;

    private const int MaxDepth = 64;
    private const long MaxArrayBytes = 1L << 30;

    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    /// <summary>バイナリの FBX か（頭の「Kaydara FBX Binary」）。</summary>
    public static bool IsBinary(ReadOnlySpan<byte> data) => data.Length >= 27 && data.StartsWith(BinaryMagic);

    /// <summary>文字の FBX らしいか（頭の「; FBX」か、FBXHeaderExtension で始まる）。</summary>
    public static bool LooksLikeAscii(ReadOnlySpan<byte> data)
    {
        ReadOnlySpan<byte> head = data[..Math.Min(data.Length, 4096)];
        return head.IndexOf("FBXHeaderExtension"u8) >= 0 || head.IndexOf("; FBX"u8) >= 0;
    }

    public static FbxDocument Parse(byte[] data)
    {
        if (IsBinary(data))
        {
            return ParseBinary(data);
        }
        if (LooksLikeAscii(data))
        {
            return ParseAscii(data);
        }
        throw new ModelFormatException("FBX ではありません（頭に「Kaydara FBX Binary」も「; FBX」もありません）");
    }

    // ---- バイナリ ----

    private static FbxDocument ParseBinary(byte[] data)
    {
        int version = (int)BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(23));
        bool wide = version >= 7500;
        long pos = 27;
        var nodes = new List<FbxNode>();
        while (pos < data.Length)
        {
            FbxNode? node = ReadBinaryNode(data, ref pos, wide, 0);
            if (node is null)
            {
                break;
            }
            nodes.Add(node);
        }
        return new FbxDocument(version, true, nodes);
    }

    // 節: 終わりの位置・値の数・値の長さ・名前の長さ（1 バイト）・名前・値・子（終わりに中身 0 の節）
    private static FbxNode? ReadBinaryNode(byte[] data, ref long pos, bool wide, int depth)
    {
        int headerSize = wide ? 25 : 13;
        if (pos + headerSize > data.Length)
        {
            throw new ModelFormatException("FBX の節が、ファイルの終わりで切れています（壊れているかもしれません）");
        }
        ReadOnlySpan<byte> span = data.AsSpan((int)pos);
        ulong end = wide ? BinaryPrimitives.ReadUInt64LittleEndian(span) : BinaryPrimitives.ReadUInt32LittleEndian(span);
        ulong count = wide ? BinaryPrimitives.ReadUInt64LittleEndian(span[8..]) : BinaryPrimitives.ReadUInt32LittleEndian(span[4..]);
        int nameLength = span[wide ? 24 : 12];
        if (end == 0)
        {
            pos += headerSize + nameLength;
            return null; // 中身 0 の節（並びの終わり）
        }
        if (depth > MaxDepth)
        {
            throw new ModelFormatException("FBX の入れ子が深すぎます（壊れているかもしれません）");
        }
        long start = pos;
        pos += headerSize;
        if (end > (ulong)data.Length || end <= (ulong)start || pos + nameLength > (long)end)
        {
            throw new ModelFormatException("FBX の節の終わりの位置がおかしいです（壊れているかもしれません）");
        }
        string name = Encoding.ASCII.GetString(data, (int)pos, nameLength);
        pos += nameLength;
        if (count > (ulong)((long)end - pos))
        {
            throw new ModelFormatException($"FBX の節「{name}」の値の数がおかしいです（壊れているかもしれません）");
        }
        var properties = new object[count];
        for (ulong i = 0; i < count; i++)
        {
            properties[i] = ReadProperty(data, ref pos, (long)end, name);
        }
        var children = new List<FbxNode>();
        while (pos < (long)end)
        {
            FbxNode? child = ReadBinaryNode(data, ref pos, wide, depth + 1);
            if (child is null)
            {
                break;
            }
            children.Add(child);
        }
        pos = (long)end;
        return new FbxNode(name, properties, children);
    }

    private static object ReadProperty(byte[] data, ref long pos, long end, string node)
    {
        Need(pos, 1, end, node);
        char type = (char)data[pos++];
        switch (type)
        {
            case 'Y':
                Need(pos, 2, end, node);
                return (object)BinaryPrimitives.ReadInt16LittleEndian(data.AsSpan((int)Advance(ref pos, 2)));
            case 'C':
                Need(pos, 1, end, node);
                return data[pos++] != 0;
            case 'I':
                Need(pos, 4, end, node);
                return BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan((int)Advance(ref pos, 4)));
            case 'F':
                Need(pos, 4, end, node);
                return BinaryPrimitives.ReadSingleLittleEndian(data.AsSpan((int)Advance(ref pos, 4)));
            case 'D':
                Need(pos, 8, end, node);
                return BinaryPrimitives.ReadDoubleLittleEndian(data.AsSpan((int)Advance(ref pos, 8)));
            case 'L':
                Need(pos, 8, end, node);
                return BinaryPrimitives.ReadInt64LittleEndian(data.AsSpan((int)Advance(ref pos, 8)));
            case 'S':
            case 'R':
            {
                Need(pos, 4, end, node);
                uint length = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan((int)pos));
                pos += 4;
                Need(pos, length, end, node);
                long at = Advance(ref pos, length);
                return type == 'S' ? Text(data.AsSpan((int)at, (int)length)) : data.AsSpan((int)at, (int)length).ToArray();
            }
            case 'f':
            case 'd':
            case 'l':
            case 'i':
            case 'b':
                return ReadArray(data, ref pos, end, type, node);
            default:
                throw new ModelFormatException($"FBX の節「{node}」に、知らない種類の値「{type}」があります（壊れているかもしれません）");
        }
    }

    private static object ReadArray(byte[] data, ref long pos, long end, char type, string node)
    {
        Need(pos, 12, end, node);
        uint count = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan((int)pos));
        uint encoding = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan((int)pos + 4));
        uint stored = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan((int)pos + 8));
        pos += 12;
        int size = type is 'd' or 'l' ? 8 : type == 'b' ? 1 : 4;
        long bytes = (long)count * size;
        if (bytes > MaxArrayBytes)
        {
            throw new ModelFormatException($"FBX の節「{node}」の配列が大きすぎます（壊れているかもしれません）");
        }
        Need(pos, stored, end, node);
        ReadOnlySpan<byte> source = data.AsSpan((int)Advance(ref pos, stored), (int)stored);
        byte[] raw;
        if (encoding == 0)
        {
            if (stored != bytes)
            {
                throw new ModelFormatException($"FBX の節「{node}」の配列の長さが合いません（壊れているかもしれません）");
            }
            raw = source.ToArray();
        }
        else if (encoding == 1)
        {
            raw = new byte[bytes];
            try
            {
                using var input = new MemoryStream(source.ToArray(), false);
                using var zlib = new ZLibStream(input, CompressionMode.Decompress);
                zlib.ReadExactly(raw);
            }
            catch (Exception ex) when (ex is InvalidDataException or EndOfStreamException)
            {
                throw new ModelFormatException($"FBX の節「{node}」の縮めた配列を戻せません（壊れているかもしれません）");
            }
        }
        else
        {
            throw new ModelFormatException($"FBX の節「{node}」の配列の縮め方 {encoding} を知りません");
        }
        return type switch
        {
            'f' => MemoryMarshal.Cast<byte, float>(raw).ToArray(),
            'd' => MemoryMarshal.Cast<byte, double>(raw).ToArray(),
            'l' => MemoryMarshal.Cast<byte, long>(raw).ToArray(),
            'i' => MemoryMarshal.Cast<byte, int>(raw).ToArray(),
            _ => raw.Select(b => b != 0).ToArray(),
        };
    }

    private static void Need(long pos, long length, long end, string node)
    {
        if (pos + length > end)
        {
            throw new ModelFormatException($"FBX の節「{node}」の値が、節の終わりを越えています（壊れているかもしれません）");
        }
    }

    private static long Advance(ref long pos, long length)
    {
        long at = pos;
        pos += length;
        return at;
    }

    /// <summary>FBX の文字（UTF-8。読めなければ Shift_JIS。日本語の Windows で作られた古いファイルのため）。</summary>
    public static string Text(ReadOnlySpan<byte> bytes)
    {
        try
        {
            return StrictUtf8.GetString(bytes);
        }
        catch (DecoderFallbackException)
        {
            return VmdText.ShiftJis.GetString(bytes);
        }
    }

    // ---- 文字の FBX（ASCII） ----

    // 書き方: 「名前: 値, 値, ... { 子 }」。「;」から行の終わりまでは注釈。配列は「*個数 { a: 値,... }」。
    private static FbxDocument ParseAscii(byte[] data)
    {
        ReadOnlySpan<byte> bytes = data;
        if (bytes.StartsWith((ReadOnlySpan<byte>)[0xEF, 0xBB, 0xBF]))
        {
            bytes = bytes[3..];
        }
        var parser = new AsciiParser(Text(bytes));
        List<FbxNode> nodes = parser.ParseNodes(0);
        int version = 0;
        if (nodes.Find(n => n.Name == "FBXHeaderExtension")?.Child("FBXVersion") is { } v)
        {
            version = (int)v.Long(0);
        }
        else if (parser.HeaderVersion is int header)
        {
            version = header;
        }
        return new FbxDocument(version, false, nodes);
    }

    private sealed class AsciiParser(string text)
    {
        private int _pos;

        /// <summary>頭の注釈「; FBX 6.1.0 project file」の版（6100 など）。</summary>
        public int? HeaderVersion { get; private set; }

        public List<FbxNode> ParseNodes(int depth)
        {
            if (depth > MaxDepth)
            {
                throw new ModelFormatException("FBX の入れ子が深すぎます（壊れているかもしれません）");
            }
            var nodes = new List<FbxNode>();
            while (true)
            {
                SkipSpace(newlines: true);
                if (_pos >= text.Length)
                {
                    return nodes;
                }
                if (text[_pos] == '}')
                {
                    if (depth == 0)
                    {
                        throw new ModelFormatException("文字の FBX の「}」の数が合いません");
                    }
                    _pos++;
                    return nodes;
                }
                nodes.Add(ParseNode(depth));
            }
        }

        private FbxNode ParseNode(int depth)
        {
            int start = _pos;
            while (_pos < text.Length && text[_pos] != ':' && !char.IsWhiteSpace(text[_pos]) && text[_pos] is not '{' and not '}' and not ',')
            {
                _pos++;
            }
            if (_pos >= text.Length || text[_pos] != ':' || _pos == start)
            {
                throw new ModelFormatException($"文字の FBX を読めません（{Line()} 行目に「名前:」がありません）");
            }
            string name = text[start.._pos];
            _pos++;
            var values = new List<object>();
            List<FbxNode> children = [];
            long? arrayCount = null;
            while (true)
            {
                SkipSpace(newlines: false);
                if (_pos >= text.Length || text[_pos] is '\n' or '\r' or '}')
                {
                    break;
                }
                if (text[_pos] == '{')
                {
                    _pos++;
                    children = ParseNodes(depth + 1);
                    break;
                }
                if (text[_pos] == '*')
                {
                    _pos++;
                    arrayCount = (long)ReadNumber();
                }
                else
                {
                    values.Add(ReadValue());
                }
                SkipSpace(newlines: false);
                if (_pos < text.Length && text[_pos] == ',')
                {
                    _pos++;
                    SkipSpace(newlines: true); // 「,」の後は次の行に続く
                }
            }
            // 配列「*個数 { a: ... }」は、値を 1 つの配列にする
            if (arrayCount is not null)
            {
                FbxNode? a = children.Find(c => c.Name == "a");
                double[] array = a is null ? [] : [.. a.Properties.Select(p => FbxNode.TryNumber(p, out double v) ? v : 0)];
                return new FbxNode(name, [array], []);
            }
            return new FbxNode(name, [.. values], children);
        }

        private object ReadValue()
        {
            char c = text[_pos];
            if (c == '"')
            {
                int end = text.IndexOf('"', _pos + 1);
                if (end < 0)
                {
                    throw new ModelFormatException($"文字の FBX の「\"」が閉じていません（{Line()} 行目）");
                }
                string s = text[(_pos + 1)..end].Replace("&quot;", "\"", StringComparison.Ordinal);
                _pos = end + 1;
                return s;
            }
            if (c is '-' or '+' or '.' || char.IsAsciiDigit(c))
            {
                return ReadNumber();
            }
            // 「T」「Y」などの記号（そのまま文字にする）
            int start = _pos;
            while (_pos < text.Length && !char.IsWhiteSpace(text[_pos]) && text[_pos] is not ',' and not '{' and not '}')
            {
                _pos++;
            }
            return text[start.._pos];
        }

        private object ReadNumber()
        {
            int start = _pos;
            bool real = false;
            while (_pos < text.Length && (char.IsAsciiDigit(text[_pos]) || text[_pos] is '-' or '+' or '.' or 'e' or 'E'))
            {
                real |= text[_pos] is '.' or 'e' or 'E';
                _pos++;
            }
            ReadOnlySpan<char> s = text.AsSpan(start, _pos - start);
            if (!real && long.TryParse(s, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out long l))
            {
                return l;
            }
            if (double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out double d))
            {
                return d;
            }
            throw new ModelFormatException($"文字の FBX の数「{s}」を読めません（{Line()} 行目）");
        }

        private void SkipSpace(bool newlines)
        {
            while (_pos < text.Length)
            {
                char c = text[_pos];
                if (c == ';')
                {
                    int end = text.IndexOf('\n', _pos);
                    string comment = end < 0 ? text[_pos..] : text[_pos..end];
                    if (HeaderVersion is null && comment.StartsWith("; FBX ", StringComparison.Ordinal))
                    {
                        string[] parts = comment[6..].Split(' ')[0].Split('.');
                        if (parts.Length >= 2 && int.TryParse(parts[0], out int major) && int.TryParse(parts[1], out int minor))
                        {
                            HeaderVersion = major * 1000 + minor * 100;
                        }
                    }
                    if (!newlines)
                    {
                        _pos = end < 0 ? text.Length : end;
                        return;
                    }
                    _pos = end < 0 ? text.Length : end + 1;
                }
                else if (c is ' ' or '\t' || (newlines && c is '\r' or '\n'))
                {
                    _pos++;
                }
                else
                {
                    return;
                }
            }
        }

        private int Line() => text.AsSpan(0, Math.Min(_pos, text.Length)).Count('\n') + 1;
    }
}
