using System.Buffers.Binary;
using System.Text.Json;

namespace Theater3D.Formats.Gltf;

/// <summary>
/// glTF 2.0 の中身（JSON と、データの塊）。GLB（1 つのファイルに JSON とデータを入れた形。VRM もこれ）と、
/// .gltf（JSON のファイル。データは別のファイルか、中に書いた base64）を読む。データの並び（アクセサー）を数の配列にして返す。
/// 変わる所だけを書いた並び（sparse）と、データの無い並び（すべて 0）にも対応する。
/// </summary>
internal sealed class GltfFile : IDisposable
{
    private const uint GlbMagic = 0x46546C67;   // "glTF"
    private const uint JsonChunk = 0x4E4F534A;  // "JSON"
    private const uint BinaryChunk = 0x004E4942; // "BIN\0"

    private readonly JsonDocument _json;
    private readonly byte[][] _buffers;

    private GltfFile(JsonDocument json, byte[][] buffers)
    {
        _json = json;
        _buffers = buffers;
    }

    public JsonElement Root => _json.RootElement;

    /// <summary>GLB なら true（.gltf の JSON なら false）。</summary>
    public bool IsBinary { get; private init; }

    /// <summary>ファイルの頭が GLB か（拡張子によらず中身で決めるため）。</summary>
    public static bool LooksLikeGlb(ReadOnlySpan<byte> head) =>
        head.Length >= 4 && BinaryPrimitives.ReadUInt32LittleEndian(head) == GlbMagic;

    /// <summary>GLB か .gltf の JSON を読む。<paramref name="baseDirectory"/> は、別のファイルのデータ（.gltf）を探す場所。</summary>
    public static GltfFile Parse(byte[] data, string? baseDirectory)
    {
        if (LooksLikeGlb(data))
        {
            return ParseGlb(data, baseDirectory);
        }
        ReadOnlySpan<byte> text = data;
        if (text.StartsWith((ReadOnlySpan<byte>)[0xEF, 0xBB, 0xBF]))
        {
            text = text[3..];
        }
        int first = text.IndexOfAnyExcept(" \t\r\n"u8);
        if (first < 0 || text[first] != (byte)'{')
        {
            throw new ModelFormatException("glTF ではありません（GLB の頭「glTF」も、JSON の「{」もありません）");
        }
        JsonDocument json = ParseJson(text);
        return new GltfFile(json, LoadBuffers(json.RootElement, null, baseDirectory)) { IsBinary = false };
    }

    private static GltfFile ParseGlb(byte[] data, string? baseDirectory)
    {
        if (data.Length < 20)
        {
            throw new ModelFormatException("GLB の頭が短すぎます");
        }
        uint version = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(4));
        if (version != 2)
        {
            throw new ModelFormatException($"GLB の版 {version} には対応していません（glTF 2.0 だけ）");
        }
        int length = (int)Math.Min(BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(8)), (uint)data.Length);
        JsonDocument? json = null;
        byte[]? binary = null;
        int offset = 12;
        while (offset + 8 <= length)
        {
            uint chunkLength = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(offset));
            uint chunkType = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(offset + 4));
            offset += 8;
            if (chunkLength > (uint)(length - offset))
            {
                throw new ModelFormatException("GLB のデータの塊が、ファイルの終わりを越えています（壊れているかもしれません）");
            }
            ReadOnlySpan<byte> chunk = data.AsSpan(offset, (int)chunkLength);
            if (chunkType == JsonChunk && json is null)
            {
                json = ParseJson(chunk);
            }
            else if (chunkType == BinaryChunk && binary is null)
            {
                binary = chunk.ToArray();
            }
            offset += (int)chunkLength;
        }
        if (json is null)
        {
            throw new ModelFormatException("GLB に JSON の塊がありません");
        }
        return new GltfFile(json, LoadBuffers(json.RootElement, binary, baseDirectory)) { IsBinary = true };
    }

    private static JsonDocument ParseJson(ReadOnlySpan<byte> text)
    {
        try
        {
            return JsonDocument.Parse(text.ToArray(), new JsonDocumentOptions { AllowTrailingCommas = true, MaxDepth = 256 });
        }
        catch (JsonException ex)
        {
            throw new ModelFormatException($"glTF の JSON を読めません（{ex.Message}）");
        }
    }

    // buffers: GLB の 1 つ目（uri なし）は BIN の塊。uri があれば、base64 の data: か、別のファイル
    private static byte[][] LoadBuffers(JsonElement root, byte[]? binary, string? baseDirectory)
    {
        if (!root.TryGetProperty("buffers", out JsonElement buffers) || buffers.ValueKind != JsonValueKind.Array)
        {
            return [];
        }
        var result = new byte[buffers.GetArrayLength()][];
        int i = 0;
        foreach (JsonElement buffer in buffers.EnumerateArray())
        {
            string? uri = buffer.TryGetProperty("uri", out JsonElement u) ? u.GetString() : null;
            if (uri is null)
            {
                result[i] = i == 0 && binary is not null
                    ? binary
                    : throw new ModelFormatException($"データの塊 {i} の中身がありません（GLB の BIN の塊が無い）");
            }
            else
            {
                result[i] = ReadUri(uri, baseDirectory, $"データの塊 {i}");
            }
            i++;
        }
        return result;
    }

    /// <summary>uri の中身（base64 の data: か、<paramref name="baseDirectory"/> からの別のファイル）。</summary>
    public static byte[] ReadUri(string uri, string? baseDirectory, string label)
    {
        if (uri.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
        {
            int comma = uri.IndexOf(',', StringComparison.Ordinal);
            if (comma < 0 || !uri.AsSpan(0, comma).EndsWith(";base64", StringComparison.OrdinalIgnoreCase))
            {
                throw new ModelFormatException($"{label}の data: の形が読めません（base64 だけに対応しています）");
            }
            try
            {
                return Convert.FromBase64String(uri[(comma + 1)..]);
            }
            catch (FormatException)
            {
                throw new ModelFormatException($"{label}の base64 が壊れています");
            }
        }
        if (baseDirectory is null)
        {
            throw new ModelFormatException($"{label}は別のファイル「{uri}」にありますが、探す場所が分かりません");
        }
        string path = Path.GetFullPath(Path.Combine(baseDirectory, Uri.UnescapeDataString(uri).Replace('/', Path.DirectorySeparatorChar)));
        try
        {
            return File.ReadAllBytes(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException)
        {
            throw new ModelFormatException($"{label}のファイル「{uri}」を読めません（{ex.Message}）");
        }
    }

    public void Dispose() => _json.Dispose();

    // ---- 配列の要素 ----

    public JsonElement Array(string name) =>
        Root.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.Array ? value : default;

    public int Count(string name) => Array(name).ValueKind == JsonValueKind.Array ? Array(name).GetArrayLength() : 0;

    public JsonElement Item(string name, int index)
    {
        JsonElement array = Array(name);
        if (array.ValueKind != JsonValueKind.Array || (uint)index >= (uint)array.GetArrayLength())
        {
            throw new ModelFormatException($"{name} の {index} 番がありません");
        }
        return array[index];
    }

    // ---- アクセサー（データの並び） ----

    /// <summary>並びを小数にして読む（count × 要素の数）。正規化した整数は 0〜1（符号付きは -1〜1）にする。</summary>
    public float[] ReadFloats(int accessorIndex, out int components)
    {
        Accessor a = ReadAccessor(accessorIndex);
        components = a.Components;
        var result = new float[a.Count * a.Components];
        if (a.BufferView is int view)
        {
            ReadOnlySpan<byte> bytes = ViewSpan(view, a.ByteOffset, a.Count, a.ElementSize, $"並び {accessorIndex}", out int stride);
            for (int i = 0; i < a.Count; i++)
            {
                ReadOnlySpan<byte> element = bytes.Slice(i * stride, a.ElementSize);
                for (int c = 0; c < a.Components; c++)
                {
                    result[i * a.Components + c] = ReadFloat(element[(c * a.ComponentSize)..], a.ComponentType, a.Normalized);
                }
            }
        }
        if (a.Sparse is { } sparse)
        {
            ApplySparse(sparse, a, accessorIndex, (i, values) =>
            {
                for (int c = 0; c < a.Components; c++)
                {
                    result[i * a.Components + c] = ReadFloat(values[(c * a.ComponentSize)..], a.ComponentType, a.Normalized);
                }
            });
        }
        return result;
    }

    /// <summary>並びを整数にして読む（面の頂点の番号・ボーンの番号）。</summary>
    public uint[] ReadIntegers(int accessorIndex, out int components)
    {
        Accessor a = ReadAccessor(accessorIndex);
        if (a.ComponentType == ComponentFloat)
        {
            throw new ModelFormatException($"並び {accessorIndex} は整数のはずですが、小数です");
        }
        components = a.Components;
        var result = new uint[a.Count * a.Components];
        if (a.BufferView is int view)
        {
            ReadOnlySpan<byte> bytes = ViewSpan(view, a.ByteOffset, a.Count, a.ElementSize, $"並び {accessorIndex}", out int stride);
            for (int i = 0; i < a.Count; i++)
            {
                ReadOnlySpan<byte> element = bytes.Slice(i * stride, a.ElementSize);
                for (int c = 0; c < a.Components; c++)
                {
                    result[i * a.Components + c] = ReadUInt(element[(c * a.ComponentSize)..], a.ComponentType);
                }
            }
        }
        if (a.Sparse is { } sparse)
        {
            ApplySparse(sparse, a, accessorIndex, (i, values) =>
            {
                for (int c = 0; c < a.Components; c++)
                {
                    result[i * a.Components + c] = ReadUInt(values[(c * a.ComponentSize)..], a.ComponentType);
                }
            });
        }
        return result;
    }

    /// <summary>並びの要素の数。</summary>
    public int AccessorCount(int accessorIndex) => ReadAccessor(accessorIndex).Count;

    /// <summary>画像のデータ（bufferView か uri）と、種類（image/png など。分からなければ null）。</summary>
    public (byte[] Data, string? MimeType) ReadImage(int imageIndex, string? baseDirectory)
    {
        JsonElement image = Item("images", imageIndex);
        string? mime = image.TryGetProperty("mimeType", out JsonElement m) ? m.GetString() : null;
        if (image.TryGetProperty("bufferView", out JsonElement v) && v.TryGetInt32(out int view))
        {
            JsonElement bufferView = Item("bufferViews", view);
            int length = Int(bufferView, "byteLength", 0);
            ReadOnlySpan<byte> bytes = ViewSpan(view, 0, 1, length, $"画像 {imageIndex}", out _);
            return (bytes[..length].ToArray(), mime);
        }
        if (image.TryGetProperty("uri", out JsonElement u) && u.GetString() is { } uri)
        {
            return (ReadUri(uri, baseDirectory, $"画像 {imageIndex}"), mime);
        }
        throw new ModelFormatException($"画像 {imageIndex} の中身がありません");
    }

    private const int ComponentByte = 5120;
    private const int ComponentUnsignedByte = 5121;
    private const int ComponentShort = 5122;
    private const int ComponentUnsignedShort = 5123;
    private const int ComponentUnsignedInt = 5125;
    private const int ComponentFloat = 5126;

    private sealed record Accessor(int? BufferView, int ByteOffset, int ComponentType, bool Normalized, int Count, int Components, JsonElement? Sparse)
    {
        public int ComponentSize => SizeOf(ComponentType);

        public int ElementSize => ComponentSize * Components;
    }

    private Accessor ReadAccessor(int index)
    {
        JsonElement a = Item("accessors", index);
        int componentType = Int(a, "componentType", 0);
        if (componentType is not (ComponentByte or ComponentUnsignedByte or ComponentShort or ComponentUnsignedShort or ComponentUnsignedInt or ComponentFloat))
        {
            throw new ModelFormatException($"並び {index} の数の種類（{componentType}）が分かりません");
        }
        string type = a.TryGetProperty("type", out JsonElement t) ? t.GetString() ?? string.Empty : string.Empty;
        int components = type switch
        {
            "SCALAR" => 1,
            "VEC2" => 2,
            "VEC3" => 3,
            "VEC4" => 4,
            "MAT2" => 4,
            "MAT3" => 9,
            "MAT4" => 16,
            _ => throw new ModelFormatException($"並び {index} の形（{type}）が分かりません"),
        };
        int count = Int(a, "count", -1);
        if (count < 0)
        {
            throw new ModelFormatException($"並び {index} の数がありません");
        }
        int? bufferView = a.TryGetProperty("bufferView", out JsonElement v) && v.TryGetInt32(out int view) ? view : null;
        bool normalized = a.TryGetProperty("normalized", out JsonElement n) && n.ValueKind == JsonValueKind.True;
        JsonElement? sparse = a.TryGetProperty("sparse", out JsonElement s) && s.ValueKind == JsonValueKind.Object ? s : null;
        return new Accessor(bufferView, Int(a, "byteOffset", 0), componentType, normalized, count, components, sparse);
    }

    // bufferView の中の、offset から count 個（1 個 elementSize バイト）を読める範囲と、1 個の間隔（stride）
    private ReadOnlySpan<byte> ViewSpan(int viewIndex, int offset, int count, int elementSize, string label, out int stride)
    {
        JsonElement view = Item("bufferViews", viewIndex);
        int buffer = Int(view, "buffer", 0);
        if ((uint)buffer >= (uint)_buffers.Length)
        {
            throw new ModelFormatException($"{label}のデータの塊 {buffer} がありません");
        }
        int viewOffset = Int(view, "byteOffset", 0);
        int viewLength = Int(view, "byteLength", 0);
        stride = Int(view, "byteStride", 0);
        if (stride <= 0)
        {
            stride = elementSize;
        }
        byte[] data = _buffers[buffer];
        long needed = count == 0 ? 0 : (long)offset + (long)(count - 1) * stride + elementSize;
        if (viewOffset < 0 || viewLength < 0 || (long)viewOffset + viewLength > data.Length || needed > viewLength || offset < 0)
        {
            throw new ModelFormatException($"{label}が、データの塊の範囲の外を指しています（壊れているかもしれません）");
        }
        return data.AsSpan(viewOffset + offset, (int)Math.Max(0, viewLength - offset));
    }

    private delegate void SparseWriter(int index, ReadOnlySpan<byte> values);

    private void ApplySparse(JsonElement sparse, Accessor a, int accessorIndex, SparseWriter write)
    {
        int count = Int(sparse, "count", 0);
        if (count <= 0)
        {
            return;
        }
        JsonElement indices = sparse.GetProperty("indices");
        JsonElement values = sparse.GetProperty("values");
        int indexType = Int(indices, "componentType", 0);
        if (indexType is not (ComponentUnsignedByte or ComponentUnsignedShort or ComponentUnsignedInt))
        {
            throw new ModelFormatException($"並び {accessorIndex} の sparse の番号の種類（{indexType}）が分かりません");
        }
        int indexSize = SizeOf(indexType);
        ReadOnlySpan<byte> indexBytes = ViewSpan(Int(indices, "bufferView", -1), Int(indices, "byteOffset", 0), count, indexSize, $"並び {accessorIndex} の sparse の番号", out _);
        ReadOnlySpan<byte> valueBytes = ViewSpan(Int(values, "bufferView", -1), Int(values, "byteOffset", 0), count, a.ElementSize, $"並び {accessorIndex} の sparse の値", out _);
        for (int k = 0; k < count; k++)
        {
            uint target = ReadUInt(indexBytes[(k * indexSize)..], indexType);
            if (target >= (uint)a.Count)
            {
                throw new ModelFormatException($"並び {accessorIndex} の sparse の番号（{target}）が範囲の外です");
            }
            write((int)target, valueBytes.Slice(k * a.ElementSize, a.ElementSize));
        }
    }

    private static int SizeOf(int componentType) => componentType switch
    {
        ComponentByte or ComponentUnsignedByte => 1,
        ComponentShort or ComponentUnsignedShort => 2,
        _ => 4,
    };

    private static float ReadFloat(ReadOnlySpan<byte> bytes, int componentType, bool normalized) => componentType switch
    {
        ComponentFloat => BinaryPrimitives.ReadSingleLittleEndian(bytes),
        ComponentUnsignedByte => normalized ? bytes[0] / 255f : bytes[0],
        ComponentByte => normalized ? Math.Max((sbyte)bytes[0] / 127f, -1f) : (sbyte)bytes[0],
        ComponentUnsignedShort => normalized ? BinaryPrimitives.ReadUInt16LittleEndian(bytes) / 65535f : BinaryPrimitives.ReadUInt16LittleEndian(bytes),
        ComponentShort => normalized ? Math.Max(BinaryPrimitives.ReadInt16LittleEndian(bytes) / 32767f, -1f) : BinaryPrimitives.ReadInt16LittleEndian(bytes),
        _ => BinaryPrimitives.ReadUInt32LittleEndian(bytes),
    };

    private static uint ReadUInt(ReadOnlySpan<byte> bytes, int componentType) => componentType switch
    {
        ComponentUnsignedByte or ComponentByte => bytes[0],
        ComponentUnsignedShort or ComponentShort => BinaryPrimitives.ReadUInt16LittleEndian(bytes),
        _ => BinaryPrimitives.ReadUInt32LittleEndian(bytes),
    };

    // ---- JSON の小さな読み方 ----

    /// <summary>オブジェクトの中の項目（<paramref name="element"/> がオブジェクトでなければ false。壊れたファイルで例外にしないため）。</summary>
    public static bool TryGet(JsonElement element, string name, out JsonElement value)
    {
        if (element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out value))
        {
            return true;
        }
        value = default;
        return false;
    }

    public static int Int(JsonElement element, string name, int fallback) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out JsonElement value) && value.TryGetInt32(out int result) ? result : fallback;

    public static float Float(JsonElement element, string name, float fallback) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out JsonElement value) && value.TryGetDouble(out double result)
            ? (float)result
            : fallback;

    public static string? String(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    /// <summary>数の配列（[1, 2, 3] など）。無いか数が足りなければ null。</summary>
    public static float[]? Floats(JsonElement element, string name, int count)
    {
        if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty(name, out JsonElement value)
            || value.ValueKind != JsonValueKind.Array || value.GetArrayLength() < count)
        {
            return null;
        }
        var result = new float[count];
        for (int i = 0; i < count; i++)
        {
            result[i] = value[i].TryGetDouble(out double d) ? (float)d : 0f;
        }
        return result;
    }

    public static IEnumerable<int> Ints(JsonElement element, string name)
    {
        if (element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement item in value.EnumerateArray())
            {
                if (item.TryGetInt32(out int i))
                {
                    yield return i;
                }
            }
        }
    }

    public static IEnumerable<string> Strings(JsonElement element, string name)
    {
        if (element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement item in value.EnumerateArray())
            {
                yield return item.ValueKind == JsonValueKind.String ? item.GetString() ?? string.Empty : string.Empty;
            }
        }
    }

}
