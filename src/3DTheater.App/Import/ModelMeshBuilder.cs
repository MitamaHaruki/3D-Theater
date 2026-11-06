using System.IO;
using System.Numerics;
using Theater3D.Engine.Interop;
using Theater3D.Formats;
using Theater3D.Formats.Images;
using Theater3D.Formats.Pmx;

namespace Theater3D.App.Import;

/// <summary>GPU に載せる前の、組み立て済みのモデルの形（別のスレッドで作る）。</summary>
internal sealed record PreparedModel(
    MeshVertex[] Vertices,
    uint[] Indices,
    MeshMaterial[] Materials,
    int[] MaterialTextureIndices,
    IReadOnlyDictionary<int, DecodedImage> Textures,
    Vector3[] Positions,
    Vector3 BoundsMin,
    Vector3 BoundsMax);

/// <summary>
/// PMX の中身から、描画エンジンに渡す頂点・面・材質を組み立てる。
/// 壊れた番号は描画で問題が起きないよう無害にする（存在しない頂点を指す面はつぶして描かない、材質の範囲は面の数で切る）。
/// 元の <see cref="PmxDocument"/> は書き換えない。
/// Phase 2 の今は初期の姿勢のまま描く（ボーンでの変形は次の段階）。テクスチャは材質の基本のものだけを使う。
/// </summary>
internal static class ModelMeshBuilder
{
    public static PreparedModel Prepare(PmxDocument d, string? baseDirectory, LoadReport report)
    {
        var vertices = new MeshVertex[d.Vertices.Length];
        var positions = new Vector3[d.Vertices.Length];
        Vector3 min = new(float.MaxValue), max = new(float.MinValue);
        for (int i = 0; i < vertices.Length; i++)
        {
            ref readonly PmxVertex v = ref d.Vertices[i];
            Vector3 normal = v.Normal.LengthSquared() > 1e-12f ? Vector3.Normalize(v.Normal) : Vector3.UnitY;
            vertices[i] = new MeshVertex(v.Position, normal, v.Uv);
            positions[i] = v.Position;
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

        var materials = new MeshMaterial[d.Materials.Length];
        var textureIndices = new int[d.Materials.Length];
        uint start = 0;
        for (int i = 0; i < d.Materials.Length; i++)
        {
            PmxMaterial m = d.Materials[i];
            uint requested = (uint)Math.Max(0, m.IndexCount - (m.IndexCount % 3));
            uint count = start >= indices.Length ? 0 : Math.Min(requested, (uint)indices.Length - start);
            materials[i] = new MeshMaterial(
                start,
                count,
                m.Diffuse,
                m.Specular,
                m.Specularity,
                m.Ambient,
                (m.Flags & PmxMaterialFlags.DoubleSided) != 0 ? MaterialFlags.DoubleSided : MaterialFlags.None,
                null);
            textureIndices[i] = (uint)m.TextureIndex < (uint)d.Textures.Length ? m.TextureIndex : -1;
            start = Math.Min(start + requested, (uint)indices.Length);
        }

        var textures = new Dictionary<int, DecodedImage>();
        if (baseDirectory is not null)
        {
            foreach (int index in textureIndices.Where(t => t >= 0).Distinct())
            {
                string relative = d.Textures[index];
                string? path = PmxPaths.Resolve(baseDirectory, relative);
                if (path is null || !File.Exists(path))
                {
                    continue; // 見つからないことは、読み込みの点検ですでにレポートに載っている
                }
                try
                {
                    textures[index] = TextureLoader.Load(path);
                }
                catch (Exception ex) when (ex is ModelFormatException or IOException or UnauthorizedAccessException or OutOfMemoryException)
                {
                    report.Warning(LoadIssueCategory.Format, $"テクスチャ「{relative}」を読めませんでした（{ex.Message}）。そのテクスチャなしで表示します");
                }
            }
        }

        return new PreparedModel(vertices, indices, materials, textureIndices, textures, positions, min, max);
    }

    /// <summary>組み立て済みの形を GPU に載せる（エンジンを作ったスレッドで呼ぶ）。</summary>
    public static NativeMesh Upload(NativeEngine engine, PreparedModel model, LoadReport report)
    {
        var uploaded = new Dictionary<int, NativeTexture>();
        try
        {
            foreach (var (index, image) in model.Textures)
            {
                try
                {
                    uploaded[index] = engine.CreateTexture(image.Width, image.Height, image.Bgra, image.Stride);
                }
                catch (NativeEngineException ex)
                {
                    report.Warning(LoadIssueCategory.Format, $"テクスチャ {index} 番を GPU に載せられませんでした（{ex.Message}）");
                }
            }
            var materials = new MeshMaterial[model.Materials.Length];
            for (int i = 0; i < materials.Length; i++)
            {
                int textureIndex = model.MaterialTextureIndices[i];
                materials[i] = model.Materials[i] with { Texture = textureIndex >= 0 ? uploaded.GetValueOrDefault(textureIndex) : null };
            }
            return engine.CreateMesh(model.Vertices, model.Indices, materials);
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
