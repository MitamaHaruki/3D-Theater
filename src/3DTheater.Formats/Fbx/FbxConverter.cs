using System.Buffers.Binary;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Theater3D.Formats.Fbx;

/// <summary>
/// FBX の場面を、glTF 2.0（GLB）の中身にする（設計判断記録 83 章）。その先は VRM・glTF と同じ変換（<c>GltfConverter</c>）で PMX と同じ中身にする。
/// <list type="bullet">
/// <item>軸と長さ: ファイルの軸の向き（GlobalSettings の UpAxis など）と単位（UnitScaleFactor。1 = 1 cm が初め）を、glTF と同じ
/// （上が +Y・正面が +Z・右手系・1 m）にする。いちばん上のノードに掛ける。</item>
/// <item>ノード: すべての Model（ボーン・形・空のノード）。位置・向き・大きさは FBX の決まり（回転の順・前後の回転・回転と拡大の中心）で 1 つの行列にする。</item>
/// <item>形: 多角形は扇の形に三角形に分ける。法線・1 つ目の UV（上下を逆にする）・材質の番号は、頂点ごと・面ごとなどの書き方に合わせて読む。</item>
/// <item>骨の変形: 皮膚の変形（Skin）のクラスターの、骨と重みと「形 → 骨」の行列（Transform）。</item>
/// <item>表情の元: ブレンドシェイプのチャンネルの名前と、形の位置の変化。</item>
/// <item>材質: 拡散色と、拡散色につながるテクスチャ（ファイルの中に入っている画像か、別のファイル）。</item>
/// </list>
/// </summary>
internal static class FbxConverter
{
    // 拡散色のテクスチャがつながる値の名前（ソフトごと）
    private static readonly string[] DiffuseSlots =
        ["DiffuseColor", "Diffuse", "Maya|baseColor", "Maya|TEX_color_map", "3dsMax|Parameters|base_color_map", "base_color_map", "BaseColor", "baseColor"];

    /// <summary>
    /// GLB の中身と、ボーンの名前から推し量った人型のボーン（VRM の名前 → ノード。見つからなければ null）と、
    /// 表情の元の名前から推し量った表情（3 段目）。
    /// </summary>
    public static (byte[] Glb, IReadOnlyDictionary<string, int>? Humanoid, IReadOnlyList<Gltf.VrmExpression> Expressions) ToGlb(
        FbxScene scene, string? baseDirectory, string fileName, LoadReport report) =>
        new Builder(scene, baseDirectory, fileName, report).Build();

    private sealed class Builder(FbxScene scene, string? baseDirectory, string fileName, LoadReport report)
    {
        private readonly GlbWriter _out = new();
        private readonly List<FbxObject> _models = [];
        private readonly Dictionary<long, int> _nodeOf = [];
        private readonly JsonArray _nodes = [];
        private readonly JsonArray _meshes = [];
        private readonly JsonArray _skins = [];
        private readonly JsonArray _materials = [];
        private readonly JsonArray _textures = [];
        private readonly JsonArray _images = [];
        private readonly Dictionary<(long Material, bool DoubleSided), int> _materialOf = [];
        private readonly Dictionary<string, int> _textureOf = [];
        private int _ngons;
        private int _degenerate;
        private int _extraUvSets;
        private int _missingTextures;
        private int _foundElsewhere;
        private int _otherInherit;
        private int _skippedGeometries;
        private bool _flippedHandedness;
        private bool _turnedAround;
        private Dictionary<string, int> _humanoid = [];
        private string[] _missingBones = [];
        private readonly List<(int Mesh, IReadOnlyList<string> Targets)> _meshTargets = [];
        private List<Gltf.VrmExpression> _expressions = [];

        public (byte[] Glb, IReadOnlyDictionary<string, int>? Humanoid, IReadOnlyList<Gltf.VrmExpression> Expressions) Build()
        {
            Matrix4x4 axis = FbxTransform.AxisConversion(scene, out string axisText, out _flippedHandedness);
            foreach (FbxObject model in scene.OfKind("Model"))
            {
                _nodeOf[model.Id] = _models.Count;
                _models.Add(model);
            }
            int[] parentOf = FbxTransform.Parents(_models);
            var childrenOf = new List<int>[_models.Count];
            for (int i = 0; i < _models.Count; i++)
            {
                if (parentOf[i] >= 0)
                {
                    (childrenOf[parentOf[i]] ??= []).Add(i);
                }
            }
            var locals = new Matrix4x4[_models.Count];
            for (int i = 0; i < _models.Count; i++)
            {
                locals[i] = FbxTransform.LocalMatrix(_models[i]);
                if (parentOf[i] < 0)
                {
                    locals[i] *= axis;
                }
            }

            // 人型のボーン（2 段目）: ボーンの名前から推し量る。左右が逆なら（後ろを向いていれば）、Y の周りに半回転させて前を向かせる
            string[] names = [.. _models.Select(m => m.Name)];
            _humanoid = FbxHumanoid.Guess(names, parentOf, out _missingBones);
            if (_humanoid.Count > 0 && FbxTransform.FacesBackward(_humanoid, FbxTransform.Worlds(locals, parentOf)))
            {
                Matrix4x4 turn = Matrix4x4.CreateRotationY(MathF.PI);
                for (int i = 0; i < _models.Count; i++)
                {
                    if (parentOf[i] < 0)
                    {
                        locals[i] *= turn;
                    }
                }
                _turnedAround = true;
            }

            var roots = new JsonArray();
            for (int i = 0; i < _models.Count; i++)
            {
                FbxObject model = _models[i];
                Matrix4x4 local = locals[i];
                if (parentOf[i] < 0)
                {
                    roots.Add(JsonValue.Create(i));
                }
                if (model.Number("InheritType", 1) is not (0 or 1))
                {
                    _otherInherit++;
                }
                var node = new JsonObject { ["name"] = model.Name, ["matrix"] = MatrixArray(local) };
                if (childrenOf[i] is { } children)
                {
                    node["children"] = new JsonArray([.. children.Select(c => (JsonNode)c)]);
                }
                _nodes.Add(node);
            }
            for (int i = 0; i < _models.Count; i++)
            {
                FbxObject model = _models[i];
                foreach (FbxObject geometry in model.ChildrenOf("Geometry"))
                {
                    if (geometry.Type == "Mesh")
                    {
                        AddMesh(model, geometry, (JsonObject)_nodes[i]!);
                        break;
                    }
                    _skippedGeometries++;
                }
            }

            var root = new JsonObject
            {
                ["asset"] = new JsonObject { ["version"] = "2.0", ["generator"] = "3D Theater FBX" },
                ["scene"] = 0,
                ["scenes"] = new JsonArray(new JsonObject { ["nodes"] = roots }),
                ["nodes"] = _nodes,
                ["meshes"] = _meshes,
                ["skins"] = _skins,
                ["materials"] = _materials,
                ["textures"] = _textures,
                ["images"] = _images,
            };
            // 表情（3 段目）: 表情の元の名前から、MMD の表情にするものを推し量る
            _expressions = FbxExpressions.Guess(_meshTargets);
            ReportNotes(axisText);
            return (_out.ToGlb(root), _humanoid.Count > 0 ? _humanoid : null, _expressions);
        }

        private static JsonArray MatrixArray(Matrix4x4 m) =>
            new([.. new[] { m.M11, m.M12, m.M13, m.M14, m.M21, m.M22, m.M23, m.M24, m.M31, m.M32, m.M33, m.M34, m.M41, m.M42, m.M43, m.M44 }
                .Select(v => (JsonNode)(float.IsFinite(v) ? v : 0f))]);

        private static Matrix4x4? Matrix(double[]? m) =>
            m is { Length: >= 16 }
                ? new Matrix4x4((float)m[0], (float)m[1], (float)m[2], (float)m[3], (float)m[4], (float)m[5], (float)m[6], (float)m[7],
                    (float)m[8], (float)m[9], (float)m[10], (float)m[11], (float)m[12], (float)m[13], (float)m[14], (float)m[15])
                : null;

        // ---- 形 ----

        private sealed class Primitive
        {
            public readonly Dictionary<(int Cp, Vector3 Normal, Vector2 Uv), int> Map = [];
            public readonly List<int> Cps = [];
            public readonly List<Vector3> Normals = [];
            public readonly List<Vector2> Uvs = [];
            public readonly List<uint> Indices = [];

            public int Vertex(int cp, Vector3 normal, Vector2 uv)
            {
                if (!Map.TryGetValue((cp, normal, uv), out int index))
                {
                    index = Cps.Count;
                    Map[(cp, normal, uv)] = index;
                    Cps.Add(cp);
                    Normals.Add(normal);
                    Uvs.Add(uv);
                }
                return index;
            }
        }

        private void AddMesh(FbxObject model, FbxObject geometry, JsonObject node)
        {
            FbxNode g = geometry.Node;
            double[] points = g.ChildDoubles("Vertices") ?? [];
            int[] polygons = g.ChildInts("PolygonVertexIndex") ?? [];
            int cpCount = points.Length / 3;
            if (cpCount == 0 || polygons.Length < 3)
            {
                return;
            }
            Layer? normals = Layer.Read(g, "LayerElementNormal", "Normals", "NormalsIndex", 3);
            Layer? uvs = Layer.Read(g, "LayerElementUV", "UV", "UVIndex", 2);
            if (g.ChildrenNamed("LayerElementUV").Skip(1).Any())
            {
                _extraUvSets++;
            }
            FbxNode? materialLayer = g.Child("LayerElementMaterial");
            int[] materialSlots = materialLayer?.ChildInts("Materials") ?? [];
            bool materialByPolygon = materialLayer?.ChildString("MappingInformationType") is "ByPolygon";
            FbxObject[] materials = [.. model.ChildrenOf("Material")];
            bool doubleSided = model.Node.ChildString("Culling") is not ("CullingOnCCW" or "CullingOnCW");

            // 皮膚の変形: クラスターごとの骨・重み・「形 → 骨」の行列
            var joints = new List<int>();
            var bindMatrices = new List<Matrix4x4>();
            List<(int Joint, float Weight)>?[]? influences = null;
            if (geometry.ChildrenOf("Deformer", "Skin").FirstOrDefault() is { } skin)
            {
                influences = new List<(int, float)>?[cpCount];
                foreach (FbxObject cluster in skin.ChildrenOf("Deformer", "Cluster"))
                {
                    FbxObject? bone = cluster.Children.Find(c => c.Kind == "Model");
                    if (bone is null || !_nodeOf.TryGetValue(bone.Id, out int boneNode))
                    {
                        continue;
                    }
                    int joint = joints.Count;
                    joints.Add(boneNode);
                    bindMatrices.Add(Matrix(cluster.Node.ChildDoubles("Transform")) ?? Matrix4x4.Identity);
                    int[] indexes = cluster.Node.ChildInts("Indexes") ?? [];
                    double[] weights = cluster.Node.ChildDoubles("Weights") ?? [];
                    for (int k = 0; k < indexes.Length && k < weights.Length; k++)
                    {
                        if ((uint)indexes[k] < (uint)cpCount && weights[k] > 0)
                        {
                            (influences[indexes[k]] ??= []).Add((joint, (float)weights[k]));
                        }
                    }
                }
                if (joints.Count == 0)
                {
                    influences = null;
                }
            }

            // 形だけに掛かる変形は、骨で変形しない形にだけ掛ける（骨で変形する形は、Transform に含まれている）
            Matrix4x4 geometric = FbxTransform.GeometricMatrix(model);
            bool applyGeometric = influences is null && !geometric.IsIdentity;
            Matrix4x4 normalMatrix = Matrix4x4.Identity;
            if (applyGeometric && Matrix4x4.Invert(geometric, out Matrix4x4 inverse))
            {
                normalMatrix = Matrix4x4.Transpose(inverse);
            }

            // 表情の元: ブレンドシェイプのチャンネルごとに、形の点 → 位置の変化
            var channels = new List<(string Name, Dictionary<int, Vector3> Deltas)>();
            foreach (FbxObject blend in geometry.ChildrenOf("Deformer", "BlendShape"))
            {
                foreach (FbxObject channel in blend.ChildrenOf("Deformer", "BlendShapeChannel"))
                {
                    FbxObject[] shapes = [.. channel.ChildrenOf("Geometry", "Shape")];
                    if (shapes.Length == 0)
                    {
                        continue;
                    }
                    // 途中の形（in-between）があれば、いちばん強い（100%）ものを使う
                    double[] full = channel.Node.ChildDoubles("FullWeights") ?? [];
                    int pick = shapes.Length - 1;
                    if (full.Length == shapes.Length)
                    {
                        pick = Array.IndexOf(full, full.Max());
                    }
                    FbxObject shape = shapes[pick];
                    int[] indexes = shape.Node.ChildInts("Indexes") ?? [];
                    double[] deltas = shape.Node.ChildDoubles("Vertices") ?? [];
                    var map = new Dictionary<int, Vector3>();
                    for (int k = 0; k < indexes.Length && (k + 1) * 3 <= deltas.Length; k++)
                    {
                        if ((uint)indexes[k] < (uint)cpCount)
                        {
                            map[indexes[k]] = new Vector3((float)deltas[k * 3], (float)deltas[k * 3 + 1], (float)deltas[k * 3 + 2]);
                        }
                    }
                    channels.Add((channel.Name.Length > 0 ? channel.Name : shape.Name, map));
                }
            }

            // 面: 多角形の終わりの頂点は、番号を反転（~番号）して書いてある
            var primitives = new SortedDictionary<int, Primitive>();
            var corners = new int[16];
            int polygon = 0;
            int start = 0;
            for (int i = 0; i < polygons.Length; i++)
            {
                if (polygons[i] >= 0 && i < polygons.Length - 1)
                {
                    continue;
                }
                int count = i - start + 1;
                if (count < 3)
                {
                    _degenerate++;
                }
                else
                {
                    int slot = materialSlots.Length == 0 ? 0 : materialByPolygon && polygon < materialSlots.Length ? materialSlots[polygon] : materialSlots[0];
                    if (!primitives.TryGetValue(slot, out Primitive? primitive))
                    {
                        primitive = new Primitive();
                        primitives[slot] = primitive;
                    }
                    if (count > 3)
                    {
                        _ngons++;
                    }
                    if (corners.Length < count)
                    {
                        corners = new int[count];
                    }
                    bool valid = true;
                    for (int k = 0; k < count; k++)
                    {
                        int pv = start + k;
                        int cp = polygons[pv] < 0 ? ~polygons[pv] : polygons[pv];
                        if ((uint)cp >= (uint)cpCount)
                        {
                            valid = false;
                            break;
                        }
                        Vector3 n = normals?.Vector3(polygon, pv, cp) ?? Vector3.Zero;
                        Vector2 uv = uvs?.Vector2(polygon, pv, cp) ?? Vector2.Zero;
                        corners[k] = primitive.Vertex(cp, n, new Vector2(uv.X, 1f - uv.Y));
                    }
                    if (valid)
                    {
                        for (int k = 1; k + 1 < count; k++)
                        {
                            primitive.Indices.Add((uint)corners[0]);
                            primitive.Indices.Add((uint)corners[k]);
                            primitive.Indices.Add((uint)corners[k + 1]);
                        }
                    }
                    else
                    {
                        _degenerate++;
                    }
                }
                polygon++;
                start = i + 1;
            }

            var primitiveArray = new JsonArray();
            foreach ((int slot, Primitive p) in primitives)
            {
                if (p.Indices.Count == 0)
                {
                    continue;
                }
                int vertexCount = p.Cps.Count;
                var positions = new float[vertexCount * 3];
                var normalData = new float[vertexCount * 3];
                var uvData = new float[vertexCount * 2];
                for (int v = 0; v < vertexCount; v++)
                {
                    int cp = p.Cps[v];
                    var position = new Vector3((float)points[cp * 3], (float)points[cp * 3 + 1], (float)points[cp * 3 + 2]);
                    Vector3 normal = p.Normals[v];
                    if (applyGeometric)
                    {
                        position = Vector3.Transform(position, geometric);
                        normal = Vector3.TransformNormal(normal, normalMatrix);
                    }
                    if (normal.LengthSquared() > 1e-20f)
                    {
                        normal = Vector3.Normalize(normal);
                    }
                    positions[v * 3] = position.X;
                    positions[v * 3 + 1] = position.Y;
                    positions[v * 3 + 2] = position.Z;
                    normalData[v * 3] = normal.X;
                    normalData[v * 3 + 1] = normal.Y;
                    normalData[v * 3 + 2] = normal.Z;
                    uvData[v * 2] = p.Uvs[v].X;
                    uvData[v * 2 + 1] = p.Uvs[v].Y;
                }
                var attributes = new JsonObject { ["POSITION"] = _out.Floats(positions, "VEC3", 3) };
                if (normals is not null)
                {
                    attributes["NORMAL"] = _out.Floats(normalData, "VEC3", 3);
                }
                if (uvs is not null)
                {
                    attributes["TEXCOORD_0"] = _out.Floats(uvData, "VEC2", 2);
                }
                if (influences is not null)
                {
                    var jointData = new ushort[vertexCount * 4];
                    var weightData = new float[vertexCount * 4];
                    for (int v = 0; v < vertexCount; v++)
                    {
                        if (influences[p.Cps[v]] is not { } list)
                        {
                            continue;
                        }
                        var strongest = list.OrderByDescending(x => x.Weight).Take(4).ToArray();
                        for (int k = 0; k < strongest.Length; k++)
                        {
                            jointData[v * 4 + k] = (ushort)strongest[k].Joint;
                            weightData[v * 4 + k] = strongest[k].Weight;
                        }
                    }
                    attributes["JOINTS_0"] = _out.UShorts(jointData, "VEC4", 4);
                    attributes["WEIGHTS_0"] = _out.Floats(weightData, "VEC4", 4);
                }
                var primitiveJson = new JsonObject
                {
                    ["attributes"] = attributes,
                    ["indices"] = _out.UInts([.. p.Indices]),
                    ["material"] = MaterialIndex(slot < materials.Length && slot >= 0 ? materials[slot] : null, doubleSided),
                };
                if (channels.Count > 0)
                {
                    var targets = new JsonArray();
                    foreach ((_, Dictionary<int, Vector3> deltas) in channels)
                    {
                        var data = new float[vertexCount * 3];
                        bool any = false;
                        for (int v = 0; v < vertexCount; v++)
                        {
                            if (!deltas.TryGetValue(p.Cps[v], out Vector3 d))
                            {
                                continue;
                            }
                            if (applyGeometric)
                            {
                                d = Vector3.TransformNormal(d, geometric);
                            }
                            data[v * 3] = d.X;
                            data[v * 3 + 1] = d.Y;
                            data[v * 3 + 2] = d.Z;
                            any = true;
                        }
                        targets.Add(new JsonObject { ["POSITION"] = any ? _out.Floats(data, "VEC3", 3) : _out.Zeros(vertexCount, "VEC3") });
                    }
                    primitiveJson["targets"] = targets;
                }
                primitiveArray.Add(primitiveJson);
            }
            if (primitiveArray.Count == 0)
            {
                return;
            }
            var mesh = new JsonObject { ["name"] = model.Name, ["primitives"] = primitiveArray };
            if (channels.Count > 0)
            {
                mesh["extras"] = new JsonObject { ["targetNames"] = new JsonArray([.. channels.Select(c => (JsonNode)c.Name)]) };
                _meshTargets.Add((_meshes.Count, [.. channels.Select(c => c.Name)]));
            }
            _meshes.Add(mesh);
            node["mesh"] = _meshes.Count - 1;
            if (influences is not null)
            {
                var ibm = new float[bindMatrices.Count * 16];
                for (int j = 0; j < bindMatrices.Count; j++)
                {
                    Matrix4x4 m = bindMatrices[j];
                    ReadOnlySpan<float> values = [m.M11, m.M12, m.M13, m.M14, m.M21, m.M22, m.M23, m.M24, m.M31, m.M32, m.M33, m.M34, m.M41, m.M42, m.M43, m.M44];
                    values.CopyTo(ibm.AsSpan(j * 16));
                }
                _skins.Add(new JsonObject
                {
                    ["joints"] = new JsonArray([.. joints.Select(j => (JsonNode)j)]),
                    ["inverseBindMatrices"] = _out.Floats(ibm, "MAT4", 16),
                });
                node["skin"] = _skins.Count - 1;
            }
        }

        // ---- 材質とテクスチャ ----

        private int MaterialIndex(FbxObject? material, bool doubleSided)
        {
            var key = (material?.Id ?? -1, doubleSided);
            if (_materialOf.TryGetValue(key, out int known))
            {
                return known;
            }
            Vector3 color = Vector3.One;
            float alpha = 1f;
            int texture = -1;
            if (material is not null)
            {
                color = material.Has("DiffuseColor") || !material.Has("Diffuse")
                    ? material.Vector("DiffuseColor", new Vector3(0.8f))
                    : material.Vector("Diffuse", new Vector3(0.8f));
                color *= (float)material.Number("DiffuseFactor", 1);
                if (material.Has("Opacity"))
                {
                    alpha = (float)material.Number("Opacity", 1);
                }
                else if (material.Has("TransparencyFactor"))
                {
                    Vector3 transparent = material.Has("TransparentColor") ? material.Vector("TransparentColor", Vector3.One) : Vector3.One;
                    alpha = 1f - (float)material.Number("TransparencyFactor", 0) * (transparent.X + transparent.Y + transparent.Z) / 3f;
                }
                alpha = float.IsFinite(alpha) ? Math.Clamp(alpha, 0f, 1f) : 1f;
                texture = DiffuseTexture(material);
                if (texture >= 0)
                {
                    // テクスチャがつながっていれば、色はテクスチャのまま（Blender は、つながった色の値を描くのに使わない）
                    color = Vector3.One;
                }
            }
            var pbr = new JsonObject
            {
                ["baseColorFactor"] = new JsonArray(Clamp(color.X), Clamp(color.Y), Clamp(color.Z), alpha),
            };
            if (texture >= 0)
            {
                pbr["baseColorTexture"] = new JsonObject { ["index"] = texture };
            }
            var json = new JsonObject
            {
                ["name"] = material?.Name ?? "材質",
                ["pbrMetallicRoughness"] = pbr,
                ["alphaMode"] = alpha < 0.999f ? "BLEND" : "OPAQUE",
                ["doubleSided"] = doubleSided,
            };
            _materials.Add(json);
            _materialOf[key] = _materials.Count - 1;
            return _materials.Count - 1;

            static float Clamp(float v) => float.IsFinite(v) ? Math.Clamp(v, 0f, 1f) : 1f;
        }

        // 拡散色につながるテクスチャ（重ねたテクスチャなら、その 1 つ目）
        private int DiffuseTexture(FbxObject material)
        {
            foreach (string slot in DiffuseSlots)
            {
                foreach ((FbxObject child, string property) in material.PropertyChildren)
                {
                    if (!string.Equals(property, slot, StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }
                    FbxObject? texture = child.Kind == "Texture" ? child
                        : child.Kind == "LayeredTexture" ? child.ChildrenOf("Texture").FirstOrDefault()
                        : null;
                    if (texture is not null && Texture(texture) is int index and >= 0)
                    {
                        return index;
                    }
                }
            }
            return -1;
        }

        private int Texture(FbxObject texture)
        {
            FbxObject? video = texture.ChildrenOf("Video").FirstOrDefault();
            string relative = FirstText(texture.Node.ChildString("RelativeFilename"), video?.Node.ChildString("RelativeFilename"));
            string absolute = FirstText(texture.Node.ChildString("FileName"), video?.Node.ChildString("Filename"), video?.Text("Path"));
            byte[]? content = video is null ? null : Content(video.Node.Child("Content"));
            string key = video is not null && content is not null ? $"video:{video.Id}" : $"file:{relative}|{absolute}";
            if (_textureOf.TryGetValue(key, out int known))
            {
                return known;
            }
            string name = FileName(relative.Length > 0 ? relative : absolute);
            JsonObject image;
            if (content is not null)
            {
                // ファイルの中に入っている画像（名前に拡張子が無ければ、中身の頭で決める）
                string? mime = content.AsSpan().StartsWith((ReadOnlySpan<byte>)[0x89, (byte)'P', (byte)'N', (byte)'G']) ? "image/png"
                    : content.AsSpan().StartsWith((ReadOnlySpan<byte>)[0xFF, 0xD8]) ? "image/jpeg"
                    : null;
                if (name.Length == 0)
                {
                    name = texture.Name.Length > 0 ? texture.Name : $"texture{_images.Count + 1}";
                }
                if (!Path.HasExtension(name))
                {
                    name += mime == "image/png" ? ".png" : mime == "image/jpeg" ? ".jpg" : content.AsSpan().StartsWith("BM"u8) ? ".bmp" : ".tga";
                }
                image = new JsonObject { ["bufferView"] = _out.View(content), ["name"] = name };
                if (mime is not null)
                {
                    image["mimeType"] = mime;
                }
            }
            else
            {
                if (name.Length == 0)
                {
                    return -1;
                }
                string? found = FindTexture(relative, absolute, name);
                string path = found ?? (relative.Length > 0 ? relative : name);
                if (found is null)
                {
                    _missingTextures++;
                }
                image = new JsonObject { ["uri"] = string.Join("/", path.Split('\\', '/').Select(Uri.EscapeDataString)), ["name"] = name };
            }
            _images.Add(image);
            _textures.Add(new JsonObject { ["source"] = _images.Count - 1 });
            _textureOf[key] = _textures.Count - 1;
            return _textures.Count - 1;
        }

        // 画像のファイルを探す。見つかれば、FBX の場所からの相対の場所（ドライブが違えば絶対の場所）
        // 順: 書かれた相対の場所 → 書かれた絶対の場所 → FBX の隣 → 「名前.fbm」の中 → FBX のフォルダーの下 → その 1 つ上のフォルダーの下
        private string? FindTexture(string relative, string absolute, string name)
        {
            if (baseDirectory is null)
            {
                return null;
            }
            string Rel(string full) => Path.GetRelativePath(baseDirectory, full);
            if (relative.Length > 0 && !Path.IsPathRooted(relative) && File.Exists(Path.Combine(baseDirectory, Normalize(relative))))
            {
                return Normalize(relative);
            }
            if (absolute.Length > 0 && Path.IsPathRooted(Normalize(absolute)) && File.Exists(Normalize(absolute)))
            {
                return Rel(Normalize(absolute));
            }
            _foundElsewhere++;
            foreach (string candidate in new[] { Path.Combine(baseDirectory, name), Path.Combine(baseDirectory, fileName + ".fbm", name) })
            {
                if (File.Exists(candidate))
                {
                    return Rel(candidate);
                }
            }
            if (name.IndexOfAny(['*', '?']) < 0)
            {
                foreach ((string folder, int depth) in new[] { (baseDirectory, 3), (Path.GetDirectoryName(baseDirectory) ?? baseDirectory, 3) })
                {
                    try
                    {
                        string? hit = Directory.EnumerateFiles(folder, name, new EnumerationOptions
                        {
                            RecurseSubdirectories = true,
                            MaxRecursionDepth = depth,
                            IgnoreInaccessible = true,
                            MatchCasing = MatchCasing.CaseInsensitive,
                        }).FirstOrDefault();
                        if (hit is not null)
                        {
                            return Rel(hit);
                        }
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
                    {
                        // 探せないフォルダーは飛ばす
                    }
                }
            }
            _foundElsewhere--;
            return null;
        }

        private static string Normalize(string path) => path.Replace('/', Path.DirectorySeparatorChar).Replace('\\', Path.DirectorySeparatorChar);

        private static string FileName(string path) => path.Length == 0 ? string.Empty : Path.GetFileName(Normalize(path));

        private static string FirstText(params string?[] values) => values.FirstOrDefault(v => !string.IsNullOrWhiteSpace(v)) ?? string.Empty;

        // 画像の中身（バイナリは中身そのまま、文字の FBX は base64 の文字の並び）
        private static byte[]? Content(FbxNode? node)
        {
            if (node is null)
            {
                return null;
            }
            if (node.Properties is [byte[] raw, ..])
            {
                return raw.Length > 0 ? raw : null;
            }
            string text = string.Concat(node.Properties.OfType<string>());
            if (text.Length == 0)
            {
                return null;
            }
            try
            {
                byte[] data = Convert.FromBase64String(text);
                return data.Length > 0 ? data : null;
            }
            catch (FormatException)
            {
                return null;
            }
        }

        // ---- レポート ----

        private void ReportNotes(string axisText)
        {
            string creator = scene.Creator.Length > 0 ? $"（作ったソフト: {scene.Creator}）" : string.Empty;
            report.Info(LoadIssueCategory.Note, $"FBX {scene.Version / 1000}.{scene.Version / 100 % 10}{creator}の軸と長さ（{axisText}）に合わせて読みました");
            string Names(IEnumerable<string> bones) =>
                string.Join("・", bones.Select(b => Gltf.VrmHumanoid.MmdName(b, Gltf.VrmVersion.None) ?? b switch
                {
                    "hips" => "下半身",
                    "spine" => "上半身",
                    "head" => "頭",
                    "neck" => "首",
                    _ => b,
                }));
            if (_humanoid.Count > 0)
            {
                report.Info(LoadIssueCategory.Note,
                    $"ボーンの名前から、人型のボーン {_humanoid.Count} 本（腰・背骨・頭・腕・足・指など）を見つけました" +
                    (_missingBones.Length > 0 ? $"。見つからなかったもの: {Names(_missingBones)}" : string.Empty));
            }
            else if (_models.Count > 0)
            {
                report.Info(LoadIssueCategory.Note,
                    $"ボーンの名前から、人型のボーン（{Names(_missingBones.Intersect(FbxHumanoid.Required))}）を見つけられなかったので、ボーンの名前は元のままです（MMD のモーションと顔のトラッキングは当てはまりません）");
            }
            int targets = _meshTargets.Sum(m => m.Targets.Count);
            if (targets > 0 && _expressions.Count == 0)
            {
                report.Info(LoadIssueCategory.Note,
                    $"表情の元（{targets} 個）の名前から、MMD の表情（まばたき・あ など）にするものを見つけられませんでした。表情の元は、名前のまま使えます");
            }
            if (_turnedAround)
            {
                report.Info(LoadIssueCategory.Note, "左右のボーンの位置から後ろを向いていると分かったので、半回転させて正面を向かせました");
            }
            if (_flippedHandedness)
            {
                report.Info(LoadIssueCategory.Note, "左手系と書かれた FBX なので、右の向きを逆にして読みました（左右が逆に見えるときは、元のソフトで書き出し直してください）");
            }
            if (_ngons > 0)
            {
                report.Info(LoadIssueCategory.Note, $"四角形以上の面 {_ngons} 個を、三角形に分けました");
            }
            if (_degenerate > 0)
            {
                report.Info(LoadIssueCategory.Unsupported, $"頂点が 3 つ未満か、頂点の番号がおかしい面が {_degenerate} 個あります。描きません");
            }
            if (_extraUvSets > 0)
            {
                report.Info(LoadIssueCategory.Unsupported, $"2 つ目からの UV（{_extraUvSets} 個の形）は使いません");
            }
            if (_skippedGeometries > 0)
            {
                report.Info(LoadIssueCategory.Unsupported, $"多角形でない形（線・曲面など）が {_skippedGeometries} 個あります。描きません");
            }
            if (_otherInherit > 0)
            {
                report.Info(LoadIssueCategory.Unsupported, $"親の大きさを受け継がない決まり（InheritType）のノードが {_otherInherit} 個あります。ふつうに受け継いで読みます");
            }
            if (_foundElsewhere > 0)
            {
                report.Info(LoadIssueCategory.Note, $"テクスチャ {_foundElsewhere} 枚が書かれた場所に無かったので、FBX の近くのフォルダーで同じ名前のものを使いました");
            }
            if (_missingTextures > 0)
            {
                report.Info(LoadIssueCategory.Note, $"テクスチャ {_missingTextures} 枚を、FBX の近くのフォルダーでも見つけられませんでした");
            }
            int stacks = scene.OfKind("AnimationStack").Count();
            if (stacks > 0 && scene.OfKind("AnimationCurve").Any())
            {
                report.Info(LoadIssueCategory.Unsupported, $"FBX の中のアニメーション（{stacks} 個）は読みません");
            }
            int cameras = scene.OfKind("Model", "Camera").Count() + scene.OfKind("Model", "Light").Count();
            if (cameras > 0)
            {
                report.Info(LoadIssueCategory.Unsupported, $"FBX の中のカメラ・照明（{cameras} 個）は読みません");
            }
        }
    }

    // 形の値（法線・UV）の読み方: 何ごとに書いてあるか（MappingInformationType）と、番号で引くか（ReferenceInformationType）
    private sealed class Layer(string mapping, bool indexed, double[] values, int[] index, int components)
    {
        public static Layer? Read(FbxNode geometry, string layerName, string valuesName, string indexName, int components)
        {
            FbxNode? layer = geometry.Child(layerName);
            if (layer?.ChildDoubles(valuesName) is not { Length: > 0 } values)
            {
                return null;
            }
            string mapping = layer.ChildString("MappingInformationType") ?? "ByPolygonVertex";
            string reference = layer.ChildString("ReferenceInformationType") ?? "Direct";
            int[] index = layer.ChildInts(indexName) ?? [];
            bool indexed = reference is "IndexToDirect" or "Index" && index.Length > 0;
            return new Layer(mapping, indexed, values, index, components);
        }

        private int Slot(int polygon, int polygonVertex, int controlPoint)
        {
            int i = mapping switch
            {
                "ByVertice" or "ByVertex" or "ByControlPoint" => controlPoint,
                "ByPolygon" => polygon,
                "AllSame" => 0,
                _ => polygonVertex,
            };
            if (indexed)
            {
                i = (uint)i < (uint)index.Length ? index[i] : -1;
            }
            return (uint)i < (uint)(values.Length / components) ? i : -1;
        }

        public Vector3 Vector3(int polygon, int polygonVertex, int controlPoint)
        {
            int i = Slot(polygon, polygonVertex, controlPoint);
            return i < 0 ? System.Numerics.Vector3.Zero : new((float)values[i * components], (float)values[i * components + 1], (float)values[i * components + 2]);
        }

        public Vector2 Vector2(int polygon, int polygonVertex, int controlPoint)
        {
            int i = Slot(polygon, polygonVertex, controlPoint);
            return i < 0 ? System.Numerics.Vector2.Zero : new((float)values[i * components], (float)values[i * components + 1]);
        }
    }

    // GLB を書く（JSON と、データの塊。並びは 4 バイトずつそろえる）
    private sealed class GlbWriter
    {
        private readonly MemoryStream _bin = new();
        private readonly JsonArray _bufferViews = [];
        private readonly JsonArray _accessors = [];

        public int View(ReadOnlySpan<byte> bytes)
        {
            Align();
            long offset = _bin.Length;
            _bin.Write(bytes);
            _bufferViews.Add(new JsonObject { ["buffer"] = 0, ["byteOffset"] = offset, ["byteLength"] = bytes.Length });
            return _bufferViews.Count - 1;
        }

        public int Floats(ReadOnlySpan<float> values, string type, int components) =>
            Accessor(View(MemoryMarshal.AsBytes(values)), 5126, values.Length / components, type);

        public int UShorts(ReadOnlySpan<ushort> values, string type, int components) =>
            Accessor(View(MemoryMarshal.AsBytes(values)), 5123, values.Length / components, type);

        public int UInts(ReadOnlySpan<uint> values) => Accessor(View(MemoryMarshal.AsBytes(values)), 5125, values.Length, "SCALAR");

        /// <summary>中身の無い並び（すべて 0）。</summary>
        public int Zeros(int count, string type)
        {
            _accessors.Add(new JsonObject { ["componentType"] = 5126, ["count"] = count, ["type"] = type });
            return _accessors.Count - 1;
        }

        private int Accessor(int view, int componentType, int count, string type)
        {
            _accessors.Add(new JsonObject { ["bufferView"] = view, ["componentType"] = componentType, ["count"] = count, ["type"] = type });
            return _accessors.Count - 1;
        }

        private void Align()
        {
            while (_bin.Length % 4 != 0)
            {
                _bin.WriteByte(0);
            }
        }

        public byte[] ToGlb(JsonObject root)
        {
            Align();
            root["buffers"] = new JsonArray(new JsonObject { ["byteLength"] = _bin.Length });
            root["bufferViews"] = _bufferViews;
            root["accessors"] = _accessors;
            byte[] json;
            using (var stream = new MemoryStream())
            {
                using (var writer = new Utf8JsonWriter(stream))
                {
                    root.WriteTo(writer);
                }
                json = stream.ToArray();
            }
            int jsonLength = (json.Length + 3) & ~3;
            int binLength = (int)_bin.Length;
            var glb = new byte[12 + 8 + jsonLength + 8 + binLength];
            Span<byte> span = glb;
            BinaryPrimitives.WriteUInt32LittleEndian(span, 0x46546C67);
            BinaryPrimitives.WriteUInt32LittleEndian(span[4..], 2);
            BinaryPrimitives.WriteUInt32LittleEndian(span[8..], (uint)glb.Length);
            BinaryPrimitives.WriteUInt32LittleEndian(span[12..], (uint)jsonLength);
            BinaryPrimitives.WriteUInt32LittleEndian(span[16..], 0x4E4F534A);
            json.CopyTo(span[20..]);
            span.Slice(20 + json.Length, jsonLength - json.Length).Fill((byte)' ');
            int binStart = 20 + jsonLength;
            BinaryPrimitives.WriteUInt32LittleEndian(span[binStart..], (uint)binLength);
            BinaryPrimitives.WriteUInt32LittleEndian(span[(binStart + 4)..], 0x004E4942);
            _bin.GetBuffer().AsSpan(0, binLength).CopyTo(span[(binStart + 8)..]);
            return glb;
        }
    }
}
