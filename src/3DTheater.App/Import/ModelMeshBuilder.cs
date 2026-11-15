using System.IO;
using System.Numerics;
using Theater3D.Editor;
using Theater3D.Engine.Interop;
using Theater3D.Formats;
using Theater3D.Formats.Images;
using Theater3D.Formats.Pmx;

namespace Theater3D.App.Import;

/// <summary>材質が使うテクスチャ（<see cref="PreparedModel.Textures"/> の鍵。-1 は無し）。</summary>
internal readonly record struct MaterialTextures(int Main, int Toon, int Sphere);

/// <summary>GPU に載せる前の、組み立て済みのモデルの形（別のスレッドで作る）。</summary>
internal sealed record PreparedModel(
    MeshVertex[] Vertices,
    uint[] Indices,
    MeshMaterial[] Materials,
    MaterialTextures[] MaterialTextures,
    IReadOnlyDictionary<int, DecodedImage> Textures,
    float[] EdgeScales,
    Vector3 BoundsMin,
    Vector3 BoundsMax,
    bool[] CoreVertices,
    Vector3 DisplayBoundsMin,
    Vector3 DisplayBoundsMax);

/// <summary>
/// PMX の中身から、描画エンジンに渡す頂点・面・材質を組み立てる。
/// 壊れた番号は描画で問題が起きないよう無害にする（存在しない頂点を指す面はつぶして描かない、材質の範囲は面の数で切る）。
/// 元の <see cref="PmxDocument"/> は書き換えない。
/// ここで作るのは初期の姿勢の形で、ボーンとモーフで変形した形は描画の前に置き換える（<see cref="Rendering.PoseUpdater"/>）。
/// テクスチャは、基本・トゥーン・スフィア（掛ける・足す）を使う。共有トゥーン（toon01〜10）は、モデルの上のフォルダに
/// MMD があればその Data フォルダの画像を、無ければ自前の画像（<see cref="DefaultToons"/>）を使う。
/// </summary>
internal static class ModelMeshBuilder
{
    /// <summary>共有トゥーン n 番の、<see cref="PreparedModel.Textures"/> の鍵（モデルのテクスチャの番号と重ならないよう負にする）。</summary>
    public static int SharedToonKey(int index) => -2 - index;

    public static PreparedModel Prepare(PmxDocument d, string? baseDirectory, LoadReport report)
    {
        var vertices = new MeshVertex[d.Vertices.Length];
        var edgeScales = new float[d.Vertices.Length];
        Vector3 min = new(float.MaxValue), max = new(float.MinValue);
        for (int i = 0; i < vertices.Length; i++)
        {
            ref readonly PmxVertex v = ref d.Vertices[i];
            Vector3 normal = v.Normal.LengthSquared() > 1e-12f ? Vector3.Normalize(v.Normal) : Vector3.UnitY;
            vertices[i] = new MeshVertex(v.Position, normal, v.Uv);
            edgeScales[i] = float.IsFinite(v.EdgeScale) ? Math.Max(0f, v.EdgeScale) : 1f;
            min = Vector3.Min(min, v.Position);
            max = Vector3.Max(max, v.Position);
        }
        if (vertices.Length == 0)
        {
            min = max = Vector3.Zero;
        }

        // 存在しない頂点を指す三角形は 3 点とも 0 番にして、大きさ 0（描かれない）にする
        int usableIndices = d.Indices.Length - (d.Indices.Length % 3);
        var indices = new uint[vertices.Length == 0 ? 0 : usableIndices];
        for (int i = 0; i < indices.Length; i += 3)
        {
            int a = d.Indices[i], b = d.Indices[i + 1], c = d.Indices[i + 2];
            bool valid = (uint)a < (uint)vertices.Length && (uint)b < (uint)vertices.Length && (uint)c < (uint)vertices.Length;
            indices[i] = valid ? (uint)a : 0;
            indices[i + 1] = valid ? (uint)b : 0;
            indices[i + 2] = valid ? (uint)c : 0;
        }

        // テクスチャのファイル（番号ごとに 1 回だけ探す）
        var paths = new Dictionary<int, string?>();
        string? PathOf(int index)
        {
            if (!paths.TryGetValue(index, out string? path))
            {
                path = baseDirectory is null ? null : PmxPaths.Resolve(baseDirectory, d.Textures[index]);
                paths[index] = path is not null && File.Exists(path) ? path : null;
            }
            return paths[index];
        }
        // ファイルの中に入っている画像（VRM・GLB）か、見つかったファイルがあるか
        bool Available(int index) => d.EmbeddedTextures.ContainsKey(index) || PathOf(index) is not null;
        bool Valid(int index) => (uint)index < (uint)d.Textures.Length;

        var materials = new MeshMaterial[d.Materials.Length];
        var materialTextures = new MaterialTextures[d.Materials.Length];
        int subTextureSpheres = 0;
        uint start = 0;
        for (int i = 0; i < d.Materials.Length; i++)
        {
            PmxMaterial m = d.Materials[i];
            uint requested = (uint)Math.Max(0, m.IndexCount - (m.IndexCount % 3));
            uint count = start >= indices.Length ? 0 : Math.Min(requested, (uint)indices.Length - start);

            ToonReference toon = PmxToon.Resolve(m, d.Textures, Available);
            SphereMode sphereMode = m.SphereMode switch
            {
                PmxSphereMode.Multiply => SphereMode.Multiply,
                PmxSphereMode.Add => SphereMode.Add,
                _ => SphereMode.None,
            };
            if (m.SphereMode == PmxSphereMode.SubTexture && Valid(m.SphereTextureIndex))
            {
                subTextureSpheres++;
            }
            int sphere = sphereMode != SphereMode.None && Valid(m.SphereTextureIndex) ? m.SphereTextureIndex : -1;
            materialTextures[i] = new MaterialTextures(
                Valid(m.TextureIndex) ? m.TextureIndex : -1,
                toon.SharedIndex >= 0 ? SharedToonKey(toon.SharedIndex) : toon.TextureIndex,
                sphere);

            MaterialFlags flags = MaterialFlags.None;
            if ((m.Flags & PmxMaterialFlags.DoubleSided) != 0)
            {
                flags |= MaterialFlags.DoubleSided;
            }
            if ((m.Flags & PmxMaterialFlags.Edge) != 0)
            {
                flags |= MaterialFlags.Edge;
            }
            if ((m.Flags & PmxMaterialFlags.GroundShadow) != 0)
            {
                flags |= MaterialFlags.GroundShadow;
            }
            if ((m.Flags & PmxMaterialFlags.CastSelfShadow) != 0)
            {
                flags |= MaterialFlags.CastShadow;
            }
            if ((m.Flags & PmxMaterialFlags.ReceiveSelfShadow) != 0)
            {
                flags |= MaterialFlags.ReceiveShadow;
            }
            materials[i] = new MeshMaterial(
                start,
                count,
                m.Diffuse,
                m.Specular,
                m.Specularity,
                m.Ambient,
                flags,
                null,
                SphereMode: sphere >= 0 ? sphereMode : SphereMode.None,
                EdgeSize: float.IsFinite(m.EdgeSize) ? Math.Max(0f, m.EdgeSize) : 0f,
                EdgeColor: m.EdgeColor);
            start = Math.Min(start + requested, (uint)indices.Length);
        }
        if (subTextureSpheres > 0)
        {
            report.Info(LoadIssueCategory.Unsupported,
                $"追加 UV を使うスフィア（サブテクスチャ）の材質が {subTextureSpheres:N0} 個あります。まだ対応していないため、スフィアなしで描きます");
        }

        var textures = new Dictionary<int, DecodedImage>();
        foreach (int index in materialTextures.SelectMany(t => new[] { t.Main, t.Toon, t.Sphere }).Where(k => k >= 0).Distinct())
        {
            byte[]? embedded = d.EmbeddedTextures.GetValueOrDefault(index);
            string? path = embedded is null ? PathOf(index) : null;
            if (embedded is null && path is null)
            {
                continue; // 見つからないことは、読み込みの点検ですでにレポートに載っている
            }
            try
            {
                textures[index] = embedded is not null ? TextureLoader.Decode(embedded, d.Textures[index]) : TextureLoader.Load(path!);
            }
            catch (Exception ex) when (ex is ModelFormatException or IOException or UnauthorizedAccessException or OutOfMemoryException)
            {
                report.Warning(LoadIssueCategory.Format, $"テクスチャ「{d.Textures[index]}」を読めませんでした（{ex.Message}）。そのテクスチャなしで表示します");
            }
        }
        foreach (int key in materialTextures.Select(t => t.Toon).Where(k => k <= SharedToonKey(0)).Distinct())
        {
            textures[key] = SharedToons.Load(-2 - key, baseDirectory);
        }

        // 輪郭線の太さを場所ごとに変える画像（VRM の MToon）: その材質の頂点の太さに、UV の所の画像の値を掛ける（設計判断記録 82 章の 5 段目）
        foreach (var (material, width) in d.EdgeWidthTextures)
        {
            if ((uint)material >= (uint)materials.Length || !Valid(width.Texture))
            {
                continue;
            }
            DecodedImage? image = textures.GetValueOrDefault(width.Texture);
            try
            {
                image ??= d.EmbeddedTextures.TryGetValue(width.Texture, out byte[]? data) ? TextureLoader.Decode(data, d.Textures[width.Texture])
                    : PathOf(width.Texture) is { } path ? TextureLoader.Load(path) : null;
            }
            catch (Exception ex) when (ex is ModelFormatException or IOException or UnauthorizedAccessException or OutOfMemoryException)
            {
                report.Warning(LoadIssueCategory.Format, $"輪郭線の太さの画像「{d.Textures[width.Texture]}」を読めませんでした（{ex.Message}）。どこも同じ太さで描きます");
            }
            if (image is not null)
            {
                MeshMaterial range = materials[material];
                ApplyEdgeWidth(image, Math.Clamp(width.Channel, 0, 2), vertices, indices.AsSpan((int)range.IndexStart, (int)range.IndexCount), edgeScales);
            }
        }

        // 画面に収めるときの箱は、遠くに隠した部品（使わない眼鏡など）を除いて求める
        Vector3[] positions = [.. d.Vertices.Select(v => v.Position)];
        bool[] core = DisplayBounds.CoreVertices(positions, indices);
        var (displayMin, displayMax) = DisplayBounds.Compute(positions, core);
        int hidden = core.Count(c => !c) - CountUnused(indices, positions.Length);
        if (hidden > 0)
        {
            report.Info(LoadIssueCategory.Note,
                $"ほかから大きく離れた頂点が {hidden:N0} 個あります（使わない部品を遠くに隠しておく作りと思われます）。画面に収めるときと選択の枠では除いて扱います");
        }

        return new PreparedModel(vertices, indices, materials, materialTextures, textures, edgeScales, min, max, core, displayMin, displayMax);
    }

    // 材質の面が使う頂点の輪郭線の太さに、UV の所の画像の値（0〜1。channel は 0 = R・1 = G・2 = B）を掛ける。UV は繰り返し、いちばん近い画素を読む
    private static void ApplyEdgeWidth(DecodedImage image, int channel, MeshVertex[] vertices, ReadOnlySpan<uint> faces, float[] edgeScales)
    {
        if (image.Width <= 0 || image.Height <= 0)
        {
            return;
        }
        var done = new HashSet<uint>();
        foreach (uint v in faces)
        {
            if (v >= (uint)vertices.Length || !done.Add(v))
            {
                continue;
            }
            Vector2 uv = vertices[v].Uv;
            float u = uv.X - MathF.Floor(uv.X);
            float t = uv.Y - MathF.Floor(uv.Y);
            int x = Math.Clamp((int)(u * image.Width), 0, image.Width - 1);
            int y = Math.Clamp((int)(t * image.Height), 0, image.Height - 1);
            byte value = image.Bgra[y * image.Stride + x * 4 + (2 - channel)]; // BGRA の並び
            edgeScales[v] *= value / 255f;
        }
    }

    private static int CountUnused(uint[] indices, int vertexCount)
    {
        var used = new bool[vertexCount];
        foreach (uint i in indices)
        {
            if (i < (uint)vertexCount)
            {
                used[i] = true;
            }
        }
        return indices.Length == 0 ? 0 : used.Count(u => !u);
    }

    /// <summary>組み立て済みの形を GPU に載せる（エンジンを作ったスレッドで呼ぶ）。</summary>
    public static NativeMesh Upload(NativeEngine engine, PreparedModel model, LoadReport report)
    {
        var uploaded = new Dictionary<int, NativeTexture>();
        try
        {
            foreach (var (key, image) in model.Textures)
            {
                try
                {
                    uploaded[key] = engine.CreateTexture(image.Width, image.Height, image.Bgra, image.Stride);
                }
                catch (NativeEngineException ex) when (ex.Result != NativeResult.DeviceLost)
                {
                    // GPU そのものが使えないときは、呼び出し側が作り直してからやり直す
                    string what = key >= 0 ? $"テクスチャ {key} 番" : $"共有トゥーン {PmxToon.SharedFileName(-2 - key)}";
                    report.Warning(LoadIssueCategory.Format, $"{what}を GPU に載せられませんでした（{ex.Message}）");
                }
            }
            NativeTexture? Find(int key) => key == -1 ? null : uploaded.GetValueOrDefault(key);
            var materials = new MeshMaterial[model.Materials.Length];
            for (int i = 0; i < materials.Length; i++)
            {
                MaterialTextures t = model.MaterialTextures[i];
                NativeTexture? sphere = Find(t.Sphere);
                materials[i] = model.Materials[i] with
                {
                    Texture = Find(t.Main),
                    ToonTexture = Find(t.Toon),
                    SphereTexture = sphere,
                    SphereMode = sphere is null ? SphereMode.None : model.Materials[i].SphereMode,
                };
            }
            return engine.CreateMesh(model.Vertices, model.Indices, materials, model.EdgeScales);
        }
        finally
        {
            // メッシュがテクスチャの中身を参照し続けるので、入れ物はここで手放してよい
            foreach (NativeTexture texture in uploaded.Values)
            {
                texture.Dispose();
            }
        }
    }
}

/// <summary>
/// 共有トゥーン（toon01.bmp〜toon10.bmp）の画像。モデルのフォルダから上へたどって MMD のフォルダ（Data\toon01.bmp がある所）が
/// 見つかればその画像を使い（利用者の手元の MMD のもの。アプリには入れない）、無ければ自前の画像を使う。
/// </summary>
internal static class SharedToons
{
    public static DecodedImage Load(int index, string? modelDirectory)
    {
        if (FindMmdDataFolder(modelDirectory) is { } data)
        {
            try
            {
                return TextureLoader.Load(Path.Combine(data, PmxToon.SharedFileName(index)));
            }
            catch (Exception ex) when (ex is ModelFormatException or IOException or UnauthorizedAccessException)
            {
                Logging.AppLog.Warning($"MMD の共有トゥーン {PmxToon.SharedFileName(index)} を読めなかったため、自前の画像を使います", ex);
            }
        }
        return DefaultToons.Create(index);
    }

    /// <summary>モデルのフォルダとその上のフォルダから、MMD の Data フォルダ（toon01.bmp〜toon10.bmp がある所）を探す。</summary>
    public static string? FindMmdDataFolder(string? modelDirectory)
    {
        for (DirectoryInfo? dir = string.IsNullOrEmpty(modelDirectory) ? null : new DirectoryInfo(modelDirectory); dir is not null; dir = dir.Parent)
        {
            string data = Path.Combine(dir.FullName, "Data");
            if (Enumerable.Range(0, PmxToon.SharedCount).All(i => File.Exists(Path.Combine(data, PmxToon.SharedFileName(i)))))
            {
                return data;
            }
        }
        return null;
    }
}
