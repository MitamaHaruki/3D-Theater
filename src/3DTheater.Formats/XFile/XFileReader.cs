using System.Numerics;
using Theater3D.Formats.Pmx;

namespace Theater3D.Formats.XFile;

/// <summary>
/// DirectX の .x（MMD のアクセサリ）を読み、PMX と同じ中身（<see cref="PmxDocument"/>）にする。テキスト・バイナリ・圧縮（tzip・bzip）を読む。
/// 形（Mesh）と材質（色・つや・反射・自己照明・テクスチャ）を読み、骨組み（Frame）の行列は形に掛けてしまう。ボーンは 1 本（「全ての親」）。
/// 大きさは MMD と同じく 10 倍にする（MMD はアクセサリを 10 倍で表示する。<see cref="MmdScale"/>）。
/// 材質: 色 → 拡散色、自己照明 → 環境色（MMD の PMX と同じ描き方になる）、トゥーン・輪郭線なし。半透明の材質は両面を描く。
/// テクスチャ名の「a.bmp*b.sph」は、テクスチャとスフィア（.sph は乗算、.spa は加算）に分ける。
/// スキン（ボーンの重み）・アニメーションは読まない（形だけ）。
/// </summary>
public static class XFileReader
{
    /// <summary>MMD がアクセサリを表示するときの倍率。</summary>
    public const float MmdScale = 10f;

    public static PmxLoadResult Load(string path)
    {
        var report = new LoadReport();
        byte[] data;
        try
        {
            data = File.ReadAllBytes(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException)
        {
            report.Error(LoadIssueCategory.Format, $"ファイルを開けませんでした: {ex.Message}");
            return new PmxLoadResult(null, report, path);
        }
        return Read(data, Path.GetDirectoryName(Path.GetFullPath(path)), path, Path.GetFileNameWithoutExtension(path), report);
    }

    /// <summary>メモリ上のデータから読む。<paramref name="baseDirectory"/> があればテクスチャの有無も調べる。</summary>
    public static PmxLoadResult Read(byte[] data, string? baseDirectory = null, string? name = null) =>
        Read(data, baseDirectory, null, name, new LoadReport());

    private static PmxLoadResult Read(byte[] data, string? baseDirectory, string? path, string? name, LoadReport report)
    {
        if (XHeader.Parse(data) is not { } header)
        {
            report.Error(LoadIssueCategory.Format,
                "DirectX の .x ではないか、対応していない形式です（頭が「xof 」で始まり、形式が txt・bin・tzip・bzip のどれかのもの）");
            return new PmxLoadResult(null, report, path);
        }
        try
        {
            ReadOnlySpan<byte> body = data.AsSpan(XHeader.Size);
            byte[]? inflated = header.IsCompressed ? MsZip.Decompress(body, report) : null;
            List<XToken> tokens = XFileTokenizer.Tokenize(inflated ?? body, header);
            List<XObject> objects = XFileParser.Parse(tokens);
            PmxDocument document = XFileConverter.ToPmx(objects, name, report);
            report.Info(LoadIssueCategory.Note,
                $"DirectX の .x（{header.DisplayName}・{header.FloatBits} ビットの小数）を、MMD と同じく {MmdScale:0} 倍の大きさで読み込みました");
            PmxValidator.Validate(document, report, baseDirectory);
            return new PmxLoadResult(document, report, path);
        }
        catch (Exception ex) when (ex is ModelFormatException or InvalidDataException or OverflowException or IndexOutOfRangeException or ArgumentOutOfRangeException)
        {
            report.Error(LoadIssueCategory.Format, $".x を読めませんでした（{ex.Message}）");
            return new PmxLoadResult(null, report, path);
        }
    }

    /// <summary>ファイルの頭から、.x の形式の名前（「テキスト」など）。.x でなければ null。</summary>
    public static string? FormatName(ReadOnlySpan<byte> head) => XHeader.Parse(head)?.DisplayName;
}

/// <summary>.x のデータの入れ子を、PMX の中身にする。</summary>
internal static class XFileConverter
{
    private sealed record XMaterial(string? Name, Vector4 Diffuse, float Power, Vector3 Specular, Vector3 Emissive, string? Texture);

    public static PmxDocument ToPmx(List<XObject> objects, string? name, LoadReport report)
    {
        var named = new Dictionary<string, XObject>(StringComparer.Ordinal);
        CollectNamedMaterials(objects, named);
        var meshes = new List<(XObject Mesh, Matrix4x4 Transform)>();
        CollectMeshes(objects, Matrix4x4.Identity, meshes);
        if (meshes.Count == 0)
        {
            throw new ModelFormatException("形（Mesh）がありません");
        }
        if (objects.Any(o => o.Type == "AnimationSet") || meshes.Any(m => m.Mesh.Child("SkinWeights") is not null))
        {
            report.Info(LoadIssueCategory.Unsupported, ".x のアニメーション・スキン（ボーンの重み）は読みません（形と材質だけを読みます）");
        }

        var builder = new Builder();
        foreach (var (mesh, transform) in meshes)
        {
            builder.Add(mesh, transform, named, report);
        }
        return builder.Build(string.IsNullOrWhiteSpace(name) ? "アクセサリ" : name);
    }

    private static void CollectNamedMaterials(IEnumerable<XObject> objects, Dictionary<string, XObject> named)
    {
        foreach (XObject o in objects)
        {
            if (o.Type == "Material" && o.Name is { } materialName)
            {
                named.TryAdd(materialName, o);
            }
            CollectNamedMaterials(o.Children, named);
        }
    }

    private static void CollectMeshes(IEnumerable<XObject> objects, Matrix4x4 parent, List<(XObject, Matrix4x4)> meshes)
    {
        foreach (XObject o in objects)
        {
            if (o.Type == "Mesh")
            {
                meshes.Add((o, parent));
            }
            else if (o.Type == "Frame")
            {
                Matrix4x4 local = Matrix4x4.Identity;
                if (o.Child("FrameTransformMatrix") is { Numbers.Count: >= 16 } m)
                {
                    List<double> v = m.Numbers;
                    local = new Matrix4x4(
                        (float)v[0], (float)v[1], (float)v[2], (float)v[3],
                        (float)v[4], (float)v[5], (float)v[6], (float)v[7],
                        (float)v[8], (float)v[9], (float)v[10], (float)v[11],
                        (float)v[12], (float)v[13], (float)v[14], (float)v[15]);
                }
                CollectMeshes(o.Children, local * parent, meshes);
            }
        }
    }

    private sealed class Builder
    {
        private readonly List<PmxVertex> _vertices = [];
        private readonly List<XMaterial> _materials = [];
        private readonly List<List<int>> _indices = [];

        public void Add(XObject mesh, Matrix4x4 transform, Dictionary<string, XObject> named, LoadReport report)
        {
            var reader = new NumberReader(mesh.Numbers, "Mesh");
            int positionCount = reader.Count();
            var positions = new Vector3[positionCount];
            for (int i = 0; i < positionCount; i++)
            {
                positions[i] = Vector3.Transform(reader.Vector3(), transform) * XFileReader.MmdScale;
            }
            List<int[]> faces = ReadFaces(ref reader, positionCount, "面");

            // 法線（無い頂点は、まわりの面の向きから求める）
            Vector3[]? normals = null;
            List<int[]>? normalFaces = null;
            if (mesh.Child("MeshNormals") is { } meshNormals)
            {
                var n = new NumberReader(meshNormals.Numbers, "MeshNormals");
                int normalCount = n.Count();
                normals = new Vector3[normalCount];
                for (int i = 0; i < normalCount; i++)
                {
                    Vector3 normal = Vector3.TransformNormal(n.Vector3(), transform);
                    normals[i] = normal.LengthSquared() > 1e-12f ? Vector3.Normalize(normal) : Vector3.UnitY;
                }
                normalFaces = ReadFaces(ref n, normalCount, "法線の面");
                if (normalFaces.Count != faces.Count)
                {
                    report.Warning(LoadIssueCategory.Format, "法線の面の数が、形の面の数と合わないため、法線を求め直しました");
                    normalFaces = null;
                }
            }
            Vector3[] smooth = SmoothNormals(positions, faces);

            Vector2[]? uvs = null;
            if (mesh.Child("MeshTextureCoords") is { } coords)
            {
                var t = new NumberReader(coords.Numbers, "MeshTextureCoords");
                int count = t.Count();
                uvs = new Vector2[count];
                for (int i = 0; i < count; i++)
                {
                    uvs[i] = new Vector2(t.Float(), t.Float());
                }
            }

            // 材質（面ごとの材質の番号）
            int materialBase = _materials.Count;
            int[] faceMaterial = new int[faces.Count];
            if (mesh.Child("MeshMaterialList") is { } list)
            {
                var m = new NumberReader(list.Numbers, "MeshMaterialList");
                int materialCount = m.Count();
                int indexCount = m.Count();
                for (int f = 0; f < faces.Count; f++)
                {
                    // 番号の数が面より少なければ、最後の番号を残りの面にも使う（1 つだけなら全部の面）
                    faceMaterial[f] = indexCount == 0 ? 0 : f < indexCount ? m.IntAt(f) : m.IntAt(indexCount - 1);
                }
                int added = 0;
                foreach (object member in list.Members)
                {
                    XObject? material = member switch
                    {
                        XObject { Type: "Material" } inline => inline,
                        XReference reference => named.GetValueOrDefault(reference.Name),
                        _ => null,
                    };
                    if (material is null && member is XReference missing)
                    {
                        report.Warning(LoadIssueCategory.InvalidReference, $"材質「{missing.Name}」が見つからないため、白い材質にしました");
                    }
                    if (material is not null || member is XReference)
                    {
                        _materials.Add(material is null ? DefaultMaterial() : ReadMaterial(material));
                        added++;
                    }
                }
                for (; added < Math.Max(materialCount, 1); added++)
                {
                    _materials.Add(DefaultMaterial());
                }
                int total = Math.Max(materialCount, 1);
                for (int f = 0; f < faceMaterial.Length; f++)
                {
                    if ((uint)faceMaterial[f] >= (uint)total)
                    {
                        faceMaterial[f] = 0;
                    }
                }
            }
            else
            {
                _materials.Add(DefaultMaterial());
            }
            while (_indices.Count < _materials.Count)
            {
                _indices.Add([]);
            }

            // 頂点: 位置と法線の組ごとに 1 つ（UV は位置ごと）
            var map = new Dictionary<(int Position, int Normal), int>();
            for (int f = 0; f < faces.Count; f++)
            {
                int[] face = faces[f];
                int[]? normalFace = normalFaces?[f];
                if (normalFace is not null && normalFace.Length != face.Length)
                {
                    normalFace = null;
                }
                var corners = new int[face.Length];
                for (int k = 0; k < face.Length; k++)
                {
                    int p = face[k];
                    int n = normalFace?[k] ?? -1;
                    if (!map.TryGetValue((p, n), out int index))
                    {
                        index = _vertices.Count;
                        map[(p, n)] = index;
                        _vertices.Add(new PmxVertex
                        {
                            Position = positions[p],
                            Normal = n >= 0 ? normals![n] : smooth[p],
                            Uv = uvs is not null && p < uvs.Length ? uvs[p] : Vector2.Zero,
                            WeightType = PmxWeightType.Bdef1,
                            Bone1 = 0,
                            Bone2 = -1,
                            Bone3 = -1,
                            Bone4 = -1,
                            Weights = new Vector4(1, 0, 0, 0),
                            EdgeScale = 1,
                        });
                    }
                    corners[k] = index;
                }
                List<int> target = _indices[materialBase + faceMaterial[f]];
                for (int k = 1; k + 1 < corners.Length; k++)
                {
                    target.Add(corners[0]);
                    target.Add(corners[k]);
                    target.Add(corners[k + 1]);
                }
            }
        }

        public PmxDocument Build(string name)
        {
            var textures = new List<string>();
            int TextureIndex(string? file)
            {
                if (string.IsNullOrWhiteSpace(file))
                {
                    return -1;
                }
                int index = textures.FindIndex(t => t.Equals(file, StringComparison.OrdinalIgnoreCase));
                if (index < 0)
                {
                    index = textures.Count;
                    textures.Add(file);
                }
                return index;
            }
            var materials = new List<PmxMaterial>();
            for (int i = 0; i < _materials.Count; i++)
            {
                XMaterial m = _materials[i];
                var (texture, sphere, sphereMode) = SplitTexture(m.Texture);
                PmxMaterialFlags flags = PmxMaterialFlags.GroundShadow | PmxMaterialFlags.CastSelfShadow | PmxMaterialFlags.ReceiveSelfShadow;
                if (m.Diffuse.W < 0.999f)
                {
                    flags |= PmxMaterialFlags.DoubleSided;
                }
                materials.Add(new PmxMaterial
                {
                    Name = m.Name ?? $"材質{i + 1}",
                    Diffuse = m.Diffuse,
                    Specular = m.Specular,
                    Specularity = m.Power,
                    Ambient = m.Emissive,
                    Flags = flags,
                    EdgeColor = new Vector4(0, 0, 0, 1),
                    EdgeSize = 0,
                    TextureIndex = TextureIndex(texture),
                    SphereTextureIndex = TextureIndex(sphere),
                    SphereMode = sphere is null ? PmxSphereMode.None : sphereMode,
                    ToonIndex = -1,
                    IndexCount = _indices[i].Count,
                });
            }
            return new PmxDocument
            {
                Version = 2.0f,
                Encoding = PmxTextEncoding.Utf16,
                IndexSizes = new PmxIndexSizes(4, 1, 1, 1, 1, 1),
                Name = name,
                NameUniversal = name,
                Comment = "DirectX の .x（MMD のアクセサリ）から読み込みました",
                Vertices = [.. _vertices],
                Indices = [.. _indices.SelectMany(i => i)],
                Textures = [.. textures],
                Materials = [.. materials],
                Bones =
                [
                    new PmxBone
                    {
                        Name = "全ての親",
                        NameUniversal = "Root",
                        Flags = PmxBoneFlags.Rotatable | PmxBoneFlags.Translatable | PmxBoneFlags.Visible | PmxBoneFlags.Operable,
                    },
                ],
                DisplayFrames =
                [
                    new PmxDisplayFrame { Name = "Root", NameUniversal = "Root", IsSpecial = true, Elements = [new PmxDisplayElement(PmxDisplayTarget.Bone, 0)] },
                    new PmxDisplayFrame { Name = "表情", NameUniversal = "Exp", IsSpecial = true },
                ],
            };
        }

        private static List<int[]> ReadFaces(ref NumberReader reader, int vertexCount, string label)
        {
            int faceCount = reader.Count();
            var faces = new List<int[]>(faceCount);
            for (int f = 0; f < faceCount; f++)
            {
                int corners = reader.Count();
                var face = new int[corners];
                for (int k = 0; k < corners; k++)
                {
                    int index = reader.Int();
                    if ((uint)index >= (uint)vertexCount)
                    {
                        throw new ModelFormatException($"{label}の頂点の番号（{index}）が範囲の外です（頂点 {vertexCount} 個）");
                    }
                    face[k] = index;
                }
                if (corners >= 3)
                {
                    faces.Add(face);
                }
            }
            return faces;
        }

        private static Vector3[] SmoothNormals(Vector3[] positions, List<int[]> faces)
        {
            var sum = new Vector3[positions.Length];
            foreach (int[] face in faces)
            {
                for (int k = 1; k + 1 < face.Length; k++)
                {
                    // 左手系・時計回りが表（PMX と同じ）
                    Vector3 n = Vector3.Cross(positions[face[k]] - positions[face[0]], positions[face[k + 1]] - positions[face[0]]);
                    sum[face[0]] += n;
                    sum[face[k]] += n;
                    sum[face[k + 1]] += n;
                }
            }
            for (int i = 0; i < sum.Length; i++)
            {
                sum[i] = sum[i].LengthSquared() > 1e-20f ? Vector3.Normalize(sum[i]) : Vector3.UnitY;
            }
            return sum;
        }

        private static XMaterial ReadMaterial(XObject material)
        {
            List<double> v = material.Numbers;
            float At(int i, float fallback) => i < v.Count ? (float)v[i] : fallback;
            string? texture = material.Child("TextureFilename")?.Strings.FirstOrDefault()
                ?? material.Child("TextureFileName")?.Strings.FirstOrDefault();
            return new XMaterial(material.Name,
                new Vector4(At(0, 1), At(1, 1), At(2, 1), At(3, 1)),
                At(4, 5),
                new Vector3(At(5, 0), At(6, 0), At(7, 0)),
                new Vector3(At(8, 0), At(9, 0), At(10, 0)),
                texture);
        }

        private static XMaterial DefaultMaterial() => new(null, Vector4.One, 5, Vector3.Zero, new Vector3(0.5f), null);
    }

    /// <summary>「a.bmp*b.sph」を、テクスチャとスフィアに分ける（スフィアだけのこともある）。</summary>
    internal static (string? Texture, string? Sphere, PmxSphereMode Mode) SplitTexture(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return (null, null, PmxSphereMode.None);
        }
        string? texture = null;
        string? sphere = null;
        PmxSphereMode mode = PmxSphereMode.None;
        foreach (string part in text.Split('*', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            string extension = Path.GetExtension(part);
            if (extension.Equals(".sph", StringComparison.OrdinalIgnoreCase) || extension.Equals(".spa", StringComparison.OrdinalIgnoreCase))
            {
                sphere = part;
                mode = extension.Equals(".spa", StringComparison.OrdinalIgnoreCase) ? PmxSphereMode.Add : PmxSphereMode.Multiply;
            }
            else
            {
                texture ??= part;
            }
        }
        return (texture, sphere, mode);
    }

    /// <summary>データの中の数を、順に読む。</summary>
    private struct NumberReader(List<double> numbers, string label)
    {
        private int _next;

        public int Count()
        {
            int value = Int();
            if (value < 0)
            {
                throw new ModelFormatException($"「{label}」の数が負です");
            }
            return value;
        }

        public int Int()
        {
            double value = Float();
            return value is >= int.MinValue and <= int.MaxValue ? (int)value : throw new ModelFormatException($"「{label}」の数が大きすぎます");
        }

        public int IntAt(int offsetFromHere) => (int)Peek(offsetFromHere);

        public float Float()
        {
            if (_next >= numbers.Count)
            {
                throw new ModelFormatException($"「{label}」の数が足りません");
            }
            return (float)numbers[_next++];
        }

        public Vector3 Vector3() => new(Float(), Float(), Float());

        private readonly double Peek(int offset) =>
            _next + offset < numbers.Count ? numbers[_next + offset] : throw new ModelFormatException($"「{label}」の数が足りません");
    }
}
