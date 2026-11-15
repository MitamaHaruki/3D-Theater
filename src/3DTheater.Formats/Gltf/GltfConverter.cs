using System.Numerics;
using System.Text;
using System.Text.Json;
using Theater3D.Formats.Pmx;

namespace Theater3D.Formats.Gltf;

/// <summary>
/// glTF の中身の元のファイル（FBX など、glTF にしてから読むもの。設計判断記録 83 章）。<paramref name="Kind"/> はレポートとコメントに出す形式の名前。
/// </summary>
internal sealed record GltfSource(string Kind, string Comment);

/// <summary>VRM の版（無ければ、ふつうの glTF）。</summary>
public enum VrmVersion
{
    None,
    Vrm0,
    Vrm1,
}

/// <summary>
/// glTF 2.0（VRM を含む）を、PMX と同じ中身（<see cref="PmxDocument"/>）にする（設計判断記録 82 章）。
/// <list type="bullet">
/// <item>向き: glTF は右手系、3D Theater（MMD）は左手系。1 つの軸を裏返し、MMD と同じく正面が -Z になるようにする
/// （VRM 0.x は正面が glTF の -Z なので X を、VRM 1.0 とふつうの glTF は正面が +Z なので Z を裏返す）。裏返すので、三角形の頂点の順も逆にする。</item>
/// <item>大きさ: glTF の 1 m を MMD の 12.5（MMD の 1 は 8 cm）にする。</item>
/// <item>形: 初めの姿勢（ノードの今の位置・向き・大きさ）で頂点を変形した形を、PMX の形にする（PMX のボーンは向きを持たないため）。
/// ボーンは、変形に使うノードとその親（ノードの名前のまま。いちばん上に「全ての親」を足す）。重みは 4 つまで。</item>
/// <item>材質: 色（glTF は光の強さの値なので、画面の色の値に直す）と色のテクスチャ・両面・半透明。光の当たり方によらず材質の色のまま見えるよう、
/// 環境色を拡散色の 0.4 倍にする（MMD の照明の初めの明るさ 0.6 と足して 1）。</item>
/// <item>表情の元（モーフターゲット）: 頂点の位置の変化を、名前のまま頂点モーフにする（同じメッシュの同じ番号は 1 つにまとめる）。</item>
/// </list>
/// ボーンの名前の付け替え・表情の対応・揺れ物・MToon の影の色と輪郭線は、まだしない（レポートに書く）。
/// </summary>
internal static class GltfConverter
{
    /// <summary>glTF の 1 m を、MMD の長さにする倍率。</summary>
    public const float MetersToMmd = 12.5f;

    /// <summary>環境色を拡散色の何倍にするか（MMD の照明の初めの明るさ 154/255 と足して、ほぼ 1 にする）。</summary>
    public const float AmbientShare = 0.4f;

    public const string RootBoneName = "全ての親";

    /// <summary>腕を A の字にするとき、水平から下げる角度（度。手本の PMX は 40 度ほど）。</summary>
    public const float ArmAngleDegrees = 37f;

    /// <summary>センターを、腰（下半身）の高さの何倍の所に置くか（手本の PMX は 0.69）。</summary>
    public const float CenterHeight = 0.69f;

    /// <summary>両目を、目の真ん中からどれだけ上に置くか（MMD の長さ。手本の PMX と同じく頭の上の操作用の位置）。</summary>
    public const float EyesHandleHeight = 2f;

    private sealed class Node
    {
        public string Name = string.Empty;
        public int[] Children = [];
        public int Mesh = -1;
        public int Skin = -1;
        public int Parent = -1;
        public Matrix4x4 Local = Matrix4x4.Identity;
        public Matrix4x4 World = Matrix4x4.Identity;
    }

    public static PmxDocument ToPmx(GltfFile file, string name, string? baseDirectory, LoadReport report, GltfSource? source = null)
    {
        var (version, comment, title) = ReadVrmMeta(file.Root);
        var builder = new Builder(file, baseDirectory, report, version);
        builder.Build();
        string kind = version switch
        {
            VrmVersion.Vrm0 => "VRM 0.x",
            VrmVersion.Vrm1 => "VRM 1.0",
            _ => source?.Kind ?? "glTF",
        };
        report.Info(LoadIssueCategory.Note,
            $"{kind} を、MMD と同じ大きさ（1 m を {MetersToMmd:0.#}）と向き（正面が -Z）にして読み込みました（元のファイルは書き換えていません）");
        builder.ReportUnsupported(version, kind);
        return builder.ToDocument(name, string.IsNullOrWhiteSpace(title) ? name : title!,
            $"{kind} から読み込みました（元のファイルは書き換えていません）。" + (source?.Comment ?? string.Empty) + comment);
    }

    // ---- VRM の利用条件など（PMX のコメントに書く） ----

    private static (VrmVersion Version, string Comment, string? Title) ReadVrmMeta(JsonElement root)
    {
        if (!GltfFile.TryGet(root, "extensions", out JsonElement extensions) || extensions.ValueKind != JsonValueKind.Object)
        {
            return (VrmVersion.None, string.Empty, null);
        }
        var text = new StringBuilder();
        void Line(string label, string? value)
        {
            if (!string.IsNullOrWhiteSpace(value))
            {
                text.Append('\n').Append(label).Append(": ").Append(value.Trim());
            }
        }
        if (GltfFile.TryGet(extensions, "VRMC_vrm", out JsonElement vrm1) && GltfFile.TryGet(vrm1, "meta", out JsonElement meta1))
        {
            string? title = GltfFile.String(meta1, "name");
            Line("名前", title);
            Line("版", GltfFile.String(meta1, "version"));
            Line("作者", string.Join("、", GltfFile.Strings(meta1, "authors").Where(a => a.Length > 0)));
            Line("著作権", GltfFile.String(meta1, "copyrightInformation"));
            Line("ライセンス", GltfFile.String(meta1, "licenseUrl"));
            Line("アバターとして使える人", GltfFile.String(meta1, "avatarPermission"));
            Line("過度な暴力表現", Bool(meta1, "allowExcessivelyViolentUsage"));
            Line("過度な性的表現", Bool(meta1, "allowExcessivelySexualUsage"));
            Line("商用の利用", GltfFile.String(meta1, "commercialUsage"));
            Line("政治・宗教の用途", Bool(meta1, "allowPoliticalOrReligiousUsage"));
            Line("再配布", Bool(meta1, "allowRedistribution"));
            Line("改変", GltfFile.String(meta1, "modification"));
            Line("クレジット表記", GltfFile.String(meta1, "creditNotation"));
            Line("その他のライセンス", GltfFile.String(meta1, "otherLicenseUrl"));
            return (VrmVersion.Vrm1, text.ToString(), title);
        }
        if (GltfFile.TryGet(extensions, "VRM", out JsonElement vrm0))
        {
            JsonElement meta0 = GltfFile.TryGet(vrm0, "meta", out JsonElement m) ? m : default;
            string? title = GltfFile.String(meta0, "title");
            Line("名前", title);
            Line("版", GltfFile.String(meta0, "version"));
            Line("作者", GltfFile.String(meta0, "author"));
            Line("連絡先", GltfFile.String(meta0, "contactInformation"));
            Line("アバターとして使える人", GltfFile.String(meta0, "allowedUserName"));
            Line("暴力表現", GltfFile.String(meta0, "violentUssageName"));
            Line("性的表現", GltfFile.String(meta0, "sexualUssageName"));
            Line("商用の利用", GltfFile.String(meta0, "commercialUssageName"));
            Line("そのほかの利用条件", GltfFile.String(meta0, "otherPermissionUrl"));
            Line("ライセンス", GltfFile.String(meta0, "licenseName"));
            Line("その他のライセンス", GltfFile.String(meta0, "otherLicenseUrl"));
            return (VrmVersion.Vrm0, text.ToString(), title);
        }
        return (VrmVersion.None, string.Empty, null);

        static string? Bool(JsonElement element, string name) =>
            element.ValueKind == JsonValueKind.Object && GltfFile.TryGet(element, name, out JsonElement value)
                ? value.ValueKind switch { JsonValueKind.True => "可", JsonValueKind.False => "不可", _ => null }
                : null;
    }

    /// <summary>光の強さの値（glTF の色の数）を、画面の色の値（sRGB）にする。</summary>
    internal static float LinearToSrgb(float value)
    {
        float v = Math.Clamp(value, 0f, 1f);
        if (v >= 1f)
        {
            return 1f; // 白は白のまま（式のままだと 0.99999994 になる）
        }
        return v <= 0.0031308f ? v * 12.92f : 1.055f * MathF.Pow(v, 1f / 2.4f) - 0.055f;
    }

    private sealed class Builder(GltfFile file, string? baseDirectory, LoadReport report, VrmVersion version)
    {
        private readonly bool _mirrorX = version == VrmVersion.Vrm0;
        private Node[] _nodes = [];
        private Dictionary<string, int> _human = [];   // VRM の人型のボーンの名前 → ノード
        private Matrix4x4[] _pose = [];               // ノードごとの、初めの姿勢へ足す動き（腕を A の字にする回転。ほかは何もしない）
        private int _posedArms;
        private bool _humanoidApplied;
        private List<VrmExpression> _expressions = [];
        private bool _lookAtByExpression;                                                  // 目線を表情で動かす VRM（両目のボーンを作らない）
        private readonly Dictionary<(int Mesh, int Target), List<PmxVertexOffset>> _targetOffsets = []; // メッシュと番号ごとの表情の元
        private readonly Dictionary<int, List<(int Vertex, Vector2 Uv)>> _materialVertices = [];      // glTF の材質 → それを使う頂点と UV
        private readonly Dictionary<int, List<int>> _pmxMaterialsOf = [];                            // glTF の材質 → PMX の材質（形ごと）
        private readonly List<PmxMorph> _expressionMorphs = [];
        private readonly List<PmxMorph> _partMorphs = [];
        private readonly HashSet<string> _expressionNames = new(StringComparer.Ordinal);
        private int _binaryExpressions;
        private int _overrideExpressions;
        private int _unsupportedColorBinds;
        private readonly List<SpringSpec> _springSpecs = [];
        private readonly List<ColliderSpec?> _colliderSpecs = [];
        private SpringBoneSet? _springBoneSet;
        private int _springChains;
        private Dictionary<int, JsonElement> _vrm0Materials = [];                 // glTF の材質 → VRM 0.x の materialProperties
        private readonly Dictionary<string, int> _toonTextures = [];              // 影の色の画像（同じものは 1 つ）
        private readonly Dictionary<int, EdgeWidthTexture> _edgeWidthTextures = []; // PMX の材質 → 輪郭線の太さの画像
        private readonly SortedSet<string> _mtoonUnsupported = new(StringComparer.Ordinal);
        private int _mtoonMaterials;
        private int _outlineMaterials;
        private readonly List<int> _order = []; // 階層の順（親が先）
        private readonly Dictionary<int, int> _boneOf = []; // ノード → ボーンの番号
        private readonly List<PmxBone> _bones = [];
        private readonly List<PmxVertex> _vertices = [];
        private readonly List<int> _indices = [];
        private readonly List<PmxMaterial> _materials = [];
        private readonly List<string> _textures = [];
        private readonly Dictionary<int, byte[]> _embedded = [];
        private readonly Dictionary<int, int> _textureOfImage = [];
        private readonly Dictionary<string, List<PmxVertexOffset>> _morphs = [];
        private readonly List<string> _morphOrder = [];
        private int _skippedPrimitives;
        private int _computedNormals;
        private int _textureTransforms;
        private int _maskMaterials;
        private bool _springBones;
        private bool _mtoon;

        private Vector3 Point(Vector3 p) => (_mirrorX ? new Vector3(-p.X, p.Y, p.Z) : new Vector3(p.X, p.Y, -p.Z)) * MetersToMmd;

        private Vector3 Direction(Vector3 d) => _mirrorX ? new Vector3(-d.X, d.Y, d.Z) : new Vector3(d.X, d.Y, -d.Z);

        public void Build()
        {
            ReadNodes();
            ComputeWorld();
            _human = VrmHumanoid.Read(file.Root, version, _nodes.Length);
            _vrm0Materials = ReadVrm0Materials();
            (_expressions, _lookAtByExpression) = VrmExpressions.Read(file.Root, version, n => (uint)n < (uint)_nodes.Length ? _nodes[n].Mesh : -1, MaterialByName);
            ComputePose();
            ReadSprings();
            BuildBones();
            _springBoneSet = BuildSpringBones();
            foreach (int node in _order)
            {
                if (_nodes[node].Mesh >= 0)
                {
                    AddMesh(node);
                }
            }
            BuildExpressions();
            if (GltfFile.TryGet(file.Root, "extensions", out JsonElement extensions) && extensions.ValueKind == JsonValueKind.Object)
            {
                _springBones = GltfFile.TryGet(extensions, "VRMC_springBone", out _)
                    || (GltfFile.TryGet(extensions, "VRM", out JsonElement vrm0) && GltfFile.TryGet(vrm0, "secondaryAnimation", out JsonElement secondary)
                        && GltfFile.TryGet(secondary, "boneGroups", out JsonElement groups) && groups.ValueKind == JsonValueKind.Array && groups.GetArrayLength() > 0);
                _mtoon |= GltfFile.TryGet(extensions, "VRM", out JsonElement v0) && GltfFile.TryGet(v0, "materialProperties", out JsonElement props)
                    && props.ValueKind == JsonValueKind.Array && props.EnumerateArray().Any(p => GltfFile.String(p, "shader") == "VRM/MToon");
            }
        }

        // ---- ノード ----

        private void ReadNodes()
        {
            _nodes = new Node[file.Count("nodes")];
            for (int i = 0; i < _nodes.Length; i++)
            {
                JsonElement n = file.Item("nodes", i);
                var node = new Node
                {
                    Name = GltfFile.String(n, "name") is { Length: > 0 } nodeName ? nodeName : $"node{i}",
                    Children = [.. GltfFile.Ints(n, "children")],
                    Mesh = GltfFile.Int(n, "mesh", -1),
                    Skin = GltfFile.Int(n, "skin", -1),
                };
                if (GltfFile.Floats(n, "matrix", 16) is { } m)
                {
                    // glTF の行列は列ごとに並ぶ（列ベクトル）。System.Numerics は行ベクトルなので、並びをそのまま読めば転置になる
                    node.Local = new Matrix4x4(m[0], m[1], m[2], m[3], m[4], m[5], m[6], m[7], m[8], m[9], m[10], m[11], m[12], m[13], m[14], m[15]);
                }
                else
                {
                    float[] t = GltfFile.Floats(n, "translation", 3) ?? [0, 0, 0];
                    float[] r = GltfFile.Floats(n, "rotation", 4) ?? [0, 0, 0, 1];
                    float[] s = GltfFile.Floats(n, "scale", 3) ?? [1, 1, 1];
                    var rotation = new Quaternion(r[0], r[1], r[2], r[3]);
                    rotation = rotation.LengthSquared() > 1e-12f ? Quaternion.Normalize(rotation) : Quaternion.Identity;
                    node.Local = Matrix4x4.CreateScale(s[0], s[1], s[2]) * Matrix4x4.CreateFromQuaternion(rotation)
                        * Matrix4x4.CreateTranslation(t[0], t[1], t[2]);
                }
                _nodes[i] = node;
            }
            for (int i = 0; i < _nodes.Length; i++)
            {
                foreach (int child in _nodes[i].Children)
                {
                    if ((uint)child < (uint)_nodes.Length && child != i && _nodes[child].Parent < 0)
                    {
                        _nodes[child].Parent = i;
                    }
                }
            }
        }

        // 場面のいちばん上のノードから、親が先の順に、初めの姿勢の位置（ワールドの行列）を求める
        private void ComputeWorld()
        {
            var roots = new List<int>();
            if (file.Count("scenes") > 0)
            {
                int scene = Math.Clamp(GltfFile.Int(file.Root, "scene", 0), 0, file.Count("scenes") - 1);
                roots.AddRange(GltfFile.Ints(file.Item("scenes", scene), "nodes").Where(n => (uint)n < (uint)_nodes.Length));
            }
            // 場面に入っていない木も、形があれば読む（いちばん上のノードを足す）
            roots.AddRange(Enumerable.Range(0, _nodes.Length).Where(n => _nodes[n].Parent < 0 && !roots.Contains(n)));
            var visited = new bool[_nodes.Length];
            var stack = new Stack<int>();
            foreach (int root in roots)
            {
                stack.Push(root);
                while (stack.Count > 0)
                {
                    int i = stack.Pop();
                    if (visited[i])
                    {
                        continue;
                    }
                    visited[i] = true;
                    Node node = _nodes[i];
                    node.World = node.Parent >= 0 && visited[node.Parent] ? node.Local * _nodes[node.Parent].World : node.Local;
                    _order.Add(i);
                    for (int c = node.Children.Length - 1; c >= 0; c--)
                    {
                        int child = node.Children[c];
                        if ((uint)child < (uint)_nodes.Length && _nodes[child].Parent == i && !visited[child])
                        {
                            stack.Push(child);
                        }
                    }
                }
            }
        }

        // ---- ボーン ----

        // 作っている途中のボーン（並びの番号は、最後に親が先の順で決める）
        private sealed class BoneSpec
        {
            public string Name = string.Empty;
            public string Universal = string.Empty;
            public Vector3 Position;
            public PmxBoneFlags Flags = PmxBoneFlags.Rotatable | PmxBoneFlags.Visible | PmxBoneFlags.Operable;
            public int Node = -1;
            public BoneSpec? Parent;
            public readonly List<BoneSpec> Children = [];
            public BoneSpec? Inherit;
            public float InheritWeight;
            public BoneSpec? IkTarget;
            public int IkLoop;
            public float IkLimit;
            public readonly List<(BoneSpec Bone, bool Limited, Vector3 Min, Vector3 Max)> IkLinks = [];
            public int Index = -1;

            public void Add(BoneSpec child)
            {
                child.Parent = this;
                Children.Add(child);
            }
        }

        private const PmxBoneFlags Movable = PmxBoneFlags.Rotatable | PmxBoneFlags.Translatable | PmxBoneFlags.Visible | PmxBoneFlags.Operable;

        private BoneSpec? HumanSpec(Dictionary<int, BoneSpec> specOf, string vrmBone) =>
            _human.TryGetValue(vrmBone, out int node) && specOf.TryGetValue(node, out BoneSpec? spec) ? spec : null;

        // 初めの姿勢（腕を A の字にした後）での、ノードの位置（MMD の座標）
        private Vector3 PosedPoint(int node) => Point(Vector3.Transform(_nodes[node].World.Translation, _pose[node]));

        private void BuildBones()
        {
            var used = new HashSet<int>();
            for (int s = 0; s < file.Count("skins"); s++)
            {
                foreach (int joint in GltfFile.Ints(file.Item("skins", s), "joints"))
                {
                    if ((uint)joint < (uint)_nodes.Length)
                    {
                        used.Add(joint);
                    }
                }
            }
            foreach (int i in _order.Where(i => _nodes[i].Mesh >= 0 && !IsSkinned(i)))
            {
                used.Add(i); // 骨で変形しない形は、置いてあるノードに付ける
            }
            used.UnionWith(_human.Values); // 人型のボーンは、形が付いていなくても作る（目など）
            used.UnionWith(_springSpecs.Select(s => s.Node)); // 揺れるボーンと、当たる物が付くノードも
            used.UnionWith(_colliderSpecs.OfType<ColliderSpec>().Select(c => c.Node));
            foreach (int i in used.ToArray())
            {
                for (int p = _nodes[i].Parent; p >= 0 && used.Add(p); p = _nodes[p].Parent)
                {
                }
            }

            // 名前: 人型のボーンは MMD の名前（先に取っておく）。ほかはノードの名前のまま（重なれば「_2」を足す）
            bool human = _human.TryGetValue("hips", out int hips) && used.Contains(hips);
            int spine = _human.GetValueOrDefault("spine", -1);
            var reserved = new HashSet<string>(StringComparer.Ordinal) { RootBoneName };
            var mmdName = new Dictionary<int, string>();
            if (human)
            {
                reserved.UnionWith(["センター", "グルーブ", "両目", "左つま先", "右つま先", "左足ＩＫ", "右足ＩＫ", "左つま先ＩＫ", "右つま先ＩＫ"]);
                foreach (var (vrmBone, node) in _human)
                {
                    if (used.Contains(node) && VrmHumanoid.MmdName(vrmBone, version) is { } name && reserved.Add(name))
                    {
                        mmdName[node] = name;
                    }
                }
            }
            string Unique(string name)
            {
                string result = name;
                for (int k = 2; !reserved.Add(result); k++)
                {
                    result = $"{name}_{k}";
                }
                return result;
            }

            var root = new BoneSpec { Name = RootBoneName, Universal = "Root", Flags = Movable };
            var specOf = new Dictionary<int, BoneSpec>();
            foreach (int i in _order.Where(used.Contains))
            {
                specOf[i] = new BoneSpec
                {
                    Node = i,
                    Name = mmdName.TryGetValue(i, out string? n) ? n : Unique(_nodes[i].Name),
                    Universal = _nodes[i].Name,
                    Position = PosedPoint(i),
                };
            }

            // MMD の体の中心: 全ての親 → センター → グルーブ → 下半身（腰）と上半身（背骨）。上半身は下半身の子にしない
            // （MMD のモーションは、上半身と下半身を別々に回すため）。センターは腰の高さの 0.69 倍（手本の PMX と同じくらい）
            BoneSpec? groove = null;
            if (human)
            {
                Vector3 hipsPosition = specOf[hips].Position;
                var center = new BoneSpec
                {
                    Name = "センター",
                    Universal = "center",
                    Position = new Vector3(hipsPosition.X, hipsPosition.Y * CenterHeight, hipsPosition.Z),
                    Flags = Movable,
                };
                groove = new BoneSpec { Name = "グルーブ", Universal = "groove", Position = center.Position, Flags = Movable };
                root.Add(center);
                center.Add(groove);
            }
            foreach (int i in _order.Where(used.Contains))
            {
                BoneSpec spec = specOf[i];
                BoneSpec parent = groove is not null && (i == hips || i == spine) ? groove
                    : _nodes[i].Parent >= 0 && specOf.TryGetValue(_nodes[i].Parent, out BoneSpec? p) ? p
                    : root;
                parent.Add(spec);
                if (parent == root)
                {
                    spec.Flags |= PmxBoneFlags.Translatable; // いちばん上のボーンは動かせる
                }
            }

            if (human)
            {
                if (!_lookAtByExpression)
                {
                    AddEyes(specOf); // 目線を表情で動かす VRM は、両目を作らない（トラッキングは「視線上」などの表情で目線を動かす）
                }
                AddLegIk(specOf, root);
                _humanoidApplied = true;
            }

            // 親が先の順に番号を付けて、PMX のボーンにする
            var ordered = new List<BoneSpec>();
            var stack = new Stack<BoneSpec>();
            stack.Push(root);
            while (stack.Count > 0)
            {
                BoneSpec spec = stack.Pop();
                spec.Index = ordered.Count;
                ordered.Add(spec);
                for (int c = spec.Children.Count - 1; c >= 0; c--)
                {
                    stack.Push(spec.Children[c]);
                }
            }
            foreach (BoneSpec s in ordered)
            {
                if (s.Node >= 0)
                {
                    _boneOf[s.Node] = s.Index;
                }
                _bones.Add(new PmxBone
                {
                    Name = s.Name,
                    NameUniversal = s.Universal,
                    Position = s.Position,
                    ParentIndex = s.Parent?.Index ?? -1,
                    Flags = s.Flags | (s.Children.Count > 0 ? PmxBoneFlags.TailIsBone : PmxBoneFlags.None),
                    TailBone = s.Children.Count > 0 ? s.Children[0].Index : -1,
                    InheritParent = s.Inherit?.Index ?? -1,
                    InheritWeight = s.InheritWeight,
                    IkTarget = s.IkTarget?.Index ?? -1,
                    IkLoopCount = s.IkLoop,
                    IkLimitAngle = s.IkLimit,
                    IkLinks = [.. s.IkLinks.Select(l => new PmxIkLink { Bone = l.Bone.Index, HasLimits = l.Limited, MinAngle = l.Min, MaxAngle = l.Max })],
                });
            }
        }

        // 両目: 左目・右目の真ん中の上に置き、左目・右目はその回転を受け取る（顔のトラッキングの目線は両目を回す）。
        // 左目・右目より先に計算するよう、頭の初めの子にする
        private void AddEyes(Dictionary<int, BoneSpec> specOf)
        {
            BoneSpec[] eyes = [.. new[] { "leftEye", "rightEye" }.Select(e => HumanSpec(specOf, e)).OfType<BoneSpec>()];
            if (eyes.Length == 0 || HumanSpec(specOf, "head") is not { } head)
            {
                return;
            }
            Vector3 middle = eyes.Aggregate(Vector3.Zero, (sum, e) => sum + e.Position) / eyes.Length;
            var both = new BoneSpec { Name = "両目", Universal = "eyes", Position = middle + new Vector3(0, EyesHandleHeight, 0), Parent = head };
            head.Children.Insert(0, both);
            foreach (BoneSpec eye in eyes)
            {
                eye.Inherit = both;
                eye.InheritWeight = 1f;
                eye.Flags |= PmxBoneFlags.InheritRotation;
            }
        }

        // 足の IK（MMD の標準と同じ）: 足ＩＫ（足首の位置。目標は足首、つなぎはひざ（後ろにだけ曲がる）と足。40 回・1 回 2 ラジアンまで）と、
        // つま先ＩＫ（つま先の位置。目標はつま先の先端、つなぎは足首。3 回・4 ラジアンまで）。先端の「つま先」は、つま先（toes）か足首の前に置く
        private void AddLegIk(Dictionary<int, BoneSpec> specOf, BoneSpec root)
        {
            foreach (var (side, name) in new[] { ("left", "左"), ("right", "右") })
            {
                if (HumanSpec(specOf, side + "UpperLeg") is not { } upper || HumanSpec(specOf, side + "LowerLeg") is not { } lower
                    || HumanSpec(specOf, side + "Foot") is not { } foot)
                {
                    continue;
                }
                Vector3 tipPosition = HumanSpec(specOf, side + "Toes")?.Position
                    ?? foot.Position + new Vector3(0, -0.94f * foot.Position.Y, -2f * Math.Max(foot.Position.Y, 0.1f));
                var tip = new BoneSpec
                {
                    Name = name + "つま先",
                    Universal = $"toe tip_{side}",
                    Position = tipPosition,
                    Flags = PmxBoneFlags.Rotatable | PmxBoneFlags.Operable, // 目印なので見せない（手本の PMX と同じ）
                };
                foot.Add(tip);
                var legIk = new BoneSpec
                {
                    Name = name + "足ＩＫ",
                    Universal = $"leg IK_{side}",
                    Position = foot.Position,
                    Flags = Movable | PmxBoneFlags.IK,
                    IkTarget = foot,
                    IkLoop = 40,
                    IkLimit = 2f,
                };
                legIk.IkLinks.Add((lower, true, new Vector3(-MathF.PI, 0, 0), new Vector3(-0.5f * MathF.PI / 180f, 0, 0)));
                legIk.IkLinks.Add((upper, false, Vector3.Zero, Vector3.Zero));
                var toeIk = new BoneSpec
                {
                    Name = name + "つま先ＩＫ",
                    Universal = $"toe IK_{side}",
                    Position = tipPosition,
                    Flags = Movable | PmxBoneFlags.IK,
                    IkTarget = tip,
                    IkLoop = 3,
                    IkLimit = 4f,
                };
                toeIk.IkLinks.Add((foot, false, Vector3.Zero, Vector3.Zero));
                legIk.Add(toeIk);
                root.Add(legIk); // IK は、ほかのボーンの後に計算する（全ての親の最後の子）
            }
        }

        // 腕を A の字にする: VRM は腕を横に伸ばした T の字で作られているが、MMD のモーションは腕を下げた A の字を基本にしているので、
        // 腕（上腕）から先を、肩の付け根を中心に、正面から見て水平から ArmAngleDegrees 度下がるまで回した形を、初めの姿勢にする
        private void ComputePose()
        {
            _pose = new Matrix4x4[_nodes.Length];
            Array.Fill(_pose, Matrix4x4.Identity);
            foreach (string side in new[] { "left", "right" })
            {
                if (!_human.TryGetValue(side + "UpperArm", out int upper) || !_human.TryGetValue(side + "LowerArm", out int lower))
                {
                    continue;
                }
                Vector3 shoulder = _nodes[upper].World.Translation;
                Vector3 elbow = _nodes[lower].World.Translation;
                // 正面から見た面（glTF の XY。VRM はどちらの版も正面が ±Z）での、腕の向き
                var direction = new Vector2(elbow.X - shoulder.X, elbow.Y - shoulder.Y);
                if (direction.LengthSquared() < 1e-12f)
                {
                    continue;
                }
                float current = MathF.Atan2(direction.Y, MathF.Abs(direction.X)); // 水平から上が正
                float delta = -ArmAngleDegrees * MathF.PI / 180f - current;
                if (MathF.Abs(delta) < 1e-4f)
                {
                    continue;
                }
                float angle = direction.X >= 0 ? delta : -delta; // +X へ伸びる腕は負の向き、-X へ伸びる腕は正の向きに回すと下がる
                Matrix4x4 rotation = Matrix4x4.CreateTranslation(-shoulder) * Matrix4x4.CreateRotationZ(angle) * Matrix4x4.CreateTranslation(shoulder);
                var stack = new Stack<int>();
                stack.Push(upper);
                while (stack.Count > 0)
                {
                    int n = stack.Pop();
                    _pose[n] = rotation;
                    foreach (int child in _nodes[n].Children)
                    {
                        if ((uint)child < (uint)_nodes.Length && _nodes[child].Parent == n)
                        {
                            stack.Push(child);
                        }
                    }
                }
                _posedArms++;
            }
        }

        private Matrix4x4 PoseOf(int node) => (uint)node < (uint)_pose.Length ? _pose[node] : Matrix4x4.Identity;

        private bool IsSkinned(int node) => _nodes[node].Skin >= 0 && _nodes[node].Skin < file.Count("skins");

        // ---- 形 ----

        private void AddMesh(int nodeIndex)
        {
            Node node = _nodes[nodeIndex];
            if (node.Mesh >= file.Count("meshes"))
            {
                report.Warning(LoadIssueCategory.InvalidReference, $"ノード「{node.Name}」のメッシュ {node.Mesh} がありません（そのメッシュを除いて読みます）");
                return;
            }
            JsonElement mesh = file.Item("meshes", node.Mesh);
            string meshName = GltfFile.String(mesh, "name") is { Length: > 0 } mn ? mn : node.Name;
            string[] meshTargetNames = TargetNames(mesh);

            // 骨で変形するなら、骨ごとの「初めの姿勢へ動かす行列」（逆バインド行列 × 骨のワールド）
            int[] jointNodes = [];
            Matrix4x4[] skinMatrices = [];
            if (IsSkinned(nodeIndex))
            {
                JsonElement skin = file.Item("skins", node.Skin);
                jointNodes = [.. GltfFile.Ints(skin, "joints")];
                float[]? inverseBind = GltfFile.Int(skin, "inverseBindMatrices", -1) is int ibm and >= 0 ? file.ReadFloats(ibm, out _) : null;
                skinMatrices = new Matrix4x4[jointNodes.Length];
                for (int j = 0; j < jointNodes.Length; j++)
                {
                    Matrix4x4 bind = Matrix4x4.Identity;
                    if (inverseBind is not null && inverseBind.Length >= (j + 1) * 16)
                    {
                        ReadOnlySpan<float> m = inverseBind.AsSpan(j * 16, 16);
                        bind = new Matrix4x4(m[0], m[1], m[2], m[3], m[4], m[5], m[6], m[7], m[8], m[9], m[10], m[11], m[12], m[13], m[14], m[15]);
                    }
                    Matrix4x4 jointWorld = (uint)jointNodes[j] < (uint)_nodes.Length ? _nodes[jointNodes[j]].World : Matrix4x4.Identity;
                    skinMatrices[j] = bind * jointWorld * PoseOf(jointNodes[j]);
                }
            }

            JsonElement primitives = GltfFile.TryGet(mesh, "primitives", out JsonElement p) && p.ValueKind == JsonValueKind.Array ? p : default;
            if (primitives.ValueKind != JsonValueKind.Array)
            {
                return;
            }
            foreach (JsonElement primitive in primitives.EnumerateArray())
            {
                AddPrimitive(node, nodeIndex, node.Mesh, primitive, meshName, meshTargetNames, jointNodes, skinMatrices);
            }
        }

        private static string[] TargetNames(JsonElement owner) =>
            GltfFile.TryGet(owner, "extras", out JsonElement extras) && extras.ValueKind == JsonValueKind.Object
                ? [.. GltfFile.Strings(extras, "targetNames")]
                : [];

        private void AddPrimitive(Node node, int nodeIndex, int meshIndex, JsonElement primitive, string meshName, string[] meshTargetNames, int[] jointNodes,
            Matrix4x4[] skinMatrices)
        {
            if (GltfFile.Int(primitive, "mode", 4) != 4)
            {
                _skippedPrimitives++; // 点・線などは描かない
                return;
            }
            JsonElement attributes = GltfFile.TryGet(primitive, "attributes", out JsonElement a) ? a : default;
            int positionAccessor = GltfFile.Int(attributes, "POSITION", -1);
            if (positionAccessor < 0)
            {
                _skippedPrimitives++;
                return;
            }
            float[] positions = file.ReadFloats(positionAccessor, out int pc);
            if (pc != 3)
            {
                throw new ModelFormatException($"「{meshName}」の頂点の位置が 3 つの数ではありません");
            }
            int count = positions.Length / 3;
            float[]? normals = GltfFile.Int(attributes, "NORMAL", -1) is int na and >= 0 ? file.ReadFloats(na, out _) : null;
            float[]? uvs = GltfFile.Int(attributes, "TEXCOORD_0", -1) is int ua and >= 0 ? file.ReadFloats(ua, out _) : null;
            uint[]? joints = null;
            float[]? weights = null;
            if (skinMatrices.Length > 0 && GltfFile.Int(attributes, "JOINTS_0", -1) is int ja and >= 0 && GltfFile.Int(attributes, "WEIGHTS_0", -1) is int wa and >= 0)
            {
                joints = file.ReadIntegers(ja, out _);
                weights = file.ReadFloats(wa, out _);
            }
            uint[] faces = GltfFile.Int(primitive, "indices", -1) is int ia and >= 0
                ? file.ReadIntegers(ia, out _)
                : [.. Enumerable.Range(0, count).Select(i => (uint)i)];
            if (normals is null || normals.Length < count * 3)
            {
                normals = ComputeNormals(positions, faces, count);
                _computedNormals++;
            }

            int baseIndex = _vertices.Count;
            int rigidBone = _boneOf.TryGetValue(nodeIndex, out int rb) ? rb : 0;
            var vertexMatrices = new Matrix4x4[count];
            Span<(int Bone, float Weight)> influences = stackalloc (int, float)[4];
            for (int i = 0; i < count; i++)
            {
                Matrix4x4 matrix;
                var vertex = new PmxVertex { EdgeScale = 1f };
                if (joints is not null && weights is not null && joints.Length >= (i + 1) * 4 && weights.Length >= (i + 1) * 4)
                {
                    int used = 0;
                    matrix = default;
                    float total = 0f;
                    for (int k = 0; k < 4; k++)
                    {
                        float w = weights[i * 4 + k];
                        uint j = joints[i * 4 + k];
                        if (w <= 0f || j >= (uint)jointNodes.Length)
                        {
                            continue;
                        }
                        total += w;
                        matrix += skinMatrices[j] * w;
                        int bone = _boneOf.TryGetValue(jointNodes[j], out int b) ? b : 0;
                        int existing = -1;
                        for (int e = 0; e < used; e++)
                        {
                            if (influences[e].Bone == bone)
                            {
                                existing = e;
                            }
                        }
                        if (existing >= 0)
                        {
                            influences[existing].Weight += w;
                        }
                        else
                        {
                            influences[used++] = (bone, w);
                        }
                    }
                    if (total <= 1e-6f)
                    {
                        matrix = node.World * PoseOf(nodeIndex);
                        used = 0;
                    }
                    else
                    {
                        matrix *= 1f / total;
                    }
                    SetWeights(ref vertex, influences[..used], total, rigidBone);
                }
                else
                {
                    matrix = node.World * PoseOf(nodeIndex);
                    SetWeights(ref vertex, [], 0f, rigidBone);
                }
                vertexMatrices[i] = matrix;
                var position = new Vector3(positions[i * 3], positions[i * 3 + 1], positions[i * 3 + 2]);
                var normal = new Vector3(normals[i * 3], normals[i * 3 + 1], normals[i * 3 + 2]);
                vertex.Position = Point(Vector3.Transform(position, matrix));
                Vector3 n = Direction(Vector3.TransformNormal(normal, matrix));
                vertex.Normal = n.LengthSquared() > 1e-12f ? Vector3.Normalize(n) : Vector3.UnitY;
                if (uvs is not null && uvs.Length >= (i + 1) * 2)
                {
                    vertex.Uv = new Vector2(uvs[i * 2], uvs[i * 2 + 1]);
                }
                _vertices.Add(vertex);
            }

            // 面: 軸を 1 つ裏返したので、頂点の順を逆にする（表が同じ側に見えるように）
            int faceCount = faces.Length / 3;
            for (int f = 0; f < faceCount; f++)
            {
                uint i0 = faces[f * 3];
                uint i1 = faces[f * 3 + 1];
                uint i2 = faces[f * 3 + 2];
                if (i0 >= count || i1 >= count || i2 >= count)
                {
                    throw new ModelFormatException($"「{meshName}」の面の頂点の番号が範囲の外です");
                }
                _indices.Add(baseIndex + (int)i0);
                _indices.Add(baseIndex + (int)i2);
                _indices.Add(baseIndex + (int)i1);
            }
            int gltfMaterial = GltfFile.Int(primitive, "material", -1);
            _materials.Add(Material(gltfMaterial, faceCount * 3));
            // 表情が材質を指すときのために、材質ごとの PMX の材質と、頂点と UV を覚える
            if (!_pmxMaterialsOf.TryGetValue(gltfMaterial, out List<int>? pmxMaterials))
            {
                pmxMaterials = [];
                _pmxMaterialsOf[gltfMaterial] = pmxMaterials;
            }
            pmxMaterials.Add(_materials.Count - 1);
            if (!_materialVertices.TryGetValue(gltfMaterial, out List<(int Vertex, Vector2 Uv)>? materialVertices))
            {
                materialVertices = [];
                _materialVertices[gltfMaterial] = materialVertices;
            }
            for (int i = 0; i < count; i++)
            {
                materialVertices.Add((baseIndex + i, _vertices[baseIndex + i].Uv));
            }

            // 表情の元（モーフターゲット）: 位置の変化を、初めの姿勢の向き・大きさに合わせて頂点モーフにする
            if (GltfFile.TryGet(primitive, "targets", out JsonElement targets) && targets.ValueKind == JsonValueKind.Array)
            {
                string[] primitiveNames = TargetNames(primitive);
                int k = 0;
                foreach (JsonElement target in targets.EnumerateArray())
                {
                    string morphName = k < meshTargetNames.Length && meshTargetNames[k].Length > 0 ? meshTargetNames[k]
                        : k < primitiveNames.Length && primitiveNames[k].Length > 0 ? primitiveNames[k]
                        : $"{meshName}_{k}";
                    int deltaAccessor = GltfFile.Int(target, "POSITION", -1);
                    k++;
                    if (deltaAccessor < 0)
                    {
                        continue;
                    }
                    float[] deltas = file.ReadFloats(deltaAccessor, out _);
                    if (!_morphs.TryGetValue(morphName, out List<PmxVertexOffset>? offsets))
                    {
                        offsets = [];
                        _morphs[morphName] = offsets;
                        _morphOrder.Add(morphName);
                    }
                    if (!_targetOffsets.TryGetValue((meshIndex, k - 1), out List<PmxVertexOffset>? targetOffsets))
                    {
                        targetOffsets = [];
                        _targetOffsets[(meshIndex, k - 1)] = targetOffsets;
                    }
                    for (int i = 0; i < count && (i + 1) * 3 <= deltas.Length; i++)
                    {
                        var delta = new Vector3(deltas[i * 3], deltas[i * 3 + 1], deltas[i * 3 + 2]);
                        if (delta.LengthSquared() < 1e-14f)
                        {
                            continue;
                        }
                        var offset = new PmxVertexOffset(baseIndex + i, Direction(Vector3.TransformNormal(delta, vertexMatrices[i])) * MetersToMmd);
                        offsets.Add(offset);
                        targetOffsets.Add(offset);
                    }
                }
            }
        }

        // 重みを PMX の形にする（1 つなら BDEF1、2 つなら BDEF2、3〜4 つなら BDEF4。足して 1 にする）
        private static void SetWeights(ref PmxVertex vertex, Span<(int Bone, float Weight)> influences, float total, int fallbackBone)
        {
            if (influences.Length == 0 || total <= 1e-6f)
            {
                vertex.WeightType = PmxWeightType.Bdef1;
                vertex.Bone1 = fallbackBone;
                vertex.Bone2 = vertex.Bone3 = vertex.Bone4 = -1;
                vertex.Weights = new Vector4(1, 0, 0, 0);
                return;
            }
            influences.Sort((x, y) => y.Weight.CompareTo(x.Weight));
            float sum = 0f;
            foreach (var (_, w) in influences)
            {
                sum += w;
            }
            vertex.Bone1 = influences[0].Bone;
            vertex.Bone2 = influences.Length > 1 ? influences[1].Bone : -1;
            vertex.Bone3 = influences.Length > 2 ? influences[2].Bone : -1;
            vertex.Bone4 = influences.Length > 3 ? influences[3].Bone : -1;
            switch (influences.Length)
            {
                case 1:
                    vertex.WeightType = PmxWeightType.Bdef1;
                    vertex.Weights = new Vector4(1, 0, 0, 0);
                    break;
                case 2:
                    vertex.WeightType = PmxWeightType.Bdef2;
                    float w1 = influences[0].Weight / sum;
                    vertex.Weights = new Vector4(w1, 1 - w1, 0, 0);
                    break;
                default:
                    vertex.WeightType = PmxWeightType.Bdef4;
                    vertex.Weights = new Vector4(
                        influences[0].Weight / sum,
                        influences[1].Weight / sum,
                        influences[2].Weight / sum,
                        influences.Length > 3 ? influences[3].Weight / sum : 0f);
                    break;
            }
        }

        private static float[] ComputeNormals(float[] positions, uint[] faces, int count)
        {
            var sums = new Vector3[count];
            for (int f = 0; f + 2 < faces.Length; f += 3)
            {
                uint a = faces[f];
                uint b = faces[f + 1];
                uint c = faces[f + 2];
                if (a >= count || b >= count || c >= count)
                {
                    continue;
                }
                Vector3 pa = new(positions[a * 3], positions[a * 3 + 1], positions[a * 3 + 2]);
                Vector3 pb = new(positions[b * 3], positions[b * 3 + 1], positions[b * 3 + 2]);
                Vector3 pc = new(positions[c * 3], positions[c * 3 + 1], positions[c * 3 + 2]);
                Vector3 n = Vector3.Cross(pb - pa, pc - pa);
                sums[a] += n;
                sums[b] += n;
                sums[c] += n;
            }
            var result = new float[count * 3];
            for (int i = 0; i < count; i++)
            {
                Vector3 n = sums[i].LengthSquared() > 1e-20f ? Vector3.Normalize(sums[i]) : Vector3.UnitY;
                result[i * 3] = n.X;
                result[i * 3 + 1] = n.Y;
                result[i * 3 + 2] = n.Z;
            }
            return result;
        }

        // ---- 材質 ----

        private PmxMaterial Material(int index, int indexCount)
        {
            const PmxMaterialFlags shadows = PmxMaterialFlags.GroundShadow | PmxMaterialFlags.CastSelfShadow | PmxMaterialFlags.ReceiveSelfShadow;
            if (index < 0 || index >= file.Count("materials"))
            {
                return new PmxMaterial
                {
                    Name = $"材質{_materials.Count + 1}",
                    Diffuse = Vector4.One,
                    Ambient = new Vector3(AmbientShare),
                    Flags = shadows,
                    EdgeColor = new Vector4(0, 0, 0, 1),
                    IndexCount = indexCount,
                };
            }
            JsonElement m = file.Item("materials", index);
            JsonElement pbr = GltfFile.TryGet(m, "pbrMetallicRoughness", out JsonElement p) ? p : default;
            float[] factor = GltfFile.Floats(pbr, "baseColorFactor", 4) ?? [1, 1, 1, 1];
            string alphaMode = GltfFile.String(m, "alphaMode") ?? "OPAQUE";
            bool opaque = alphaMode == "OPAQUE";
            if (alphaMode == "MASK")
            {
                _maskMaterials++;
            }
            var color = new Vector3(LinearToSrgb(factor[0]), LinearToSrgb(factor[1]), LinearToSrgb(factor[2]));
            int texture = -1;
            if (pbr.ValueKind == JsonValueKind.Object && GltfFile.TryGet(pbr, "baseColorTexture", out JsonElement baseTexture))
            {
                texture = Texture(GltfFile.Int(baseTexture, "index", -1));
                if (GltfFile.TryGet(baseTexture, "extensions", out JsonElement te) && GltfFile.TryGet(te, "KHR_texture_transform", out _))
                {
                    _textureTransforms++;
                }
            }
            PmxMaterialFlags flags = shadows;
            if (GltfFile.TryGet(m, "doubleSided", out JsonElement ds) && ds.ValueKind == JsonValueKind.True)
            {
                flags |= PmxMaterialFlags.DoubleSided;
            }

            // アニメ調の材質（MToon。5 段目）: 影の色はトゥーンの画像に、輪郭線は MMD の輪郭線に、matcap はスフィア（足す）にする
            int toon = -1;
            int sphere = -1;
            var edgeColor = new Vector4(0, 0, 0, 1);
            float edgeSize = 0f;
            MToonSettings? mtoon = version == VrmVersion.Vrm0
                ? (_vrm0Materials.TryGetValue(index, out JsonElement properties) ? MToon.ReadVrm0(properties) : null)
                : MToon.ReadVrm1(m);
            if (mtoon is not null)
            {
                _mtoon = true;
                _mtoonMaterials++;
                _mtoonUnsupported.UnionWith(mtoon.Unsupported);
                if (mtoon.Shade is { } shade)
                {
                    // トゥーンは材質の色に掛けるので、影の色 ÷ 材質の色（1 より明るくはしない）
                    Vector3 ratio = Vector3.Clamp(shade / Vector3.Max(color, new Vector3(1e-3f)), Vector3.Zero, Vector3.One);
                    toon = ShadeToon(ratio, mtoon.Shift, mtoon.Toony);
                }
                edgeSize = MToon.EdgeSize(mtoon);
                if (edgeSize > 0f)
                {
                    flags |= PmxMaterialFlags.Edge;
                    edgeColor = new Vector4(mtoon.OutlineColor, 1f);
                    _outlineMaterials++;
                    if (mtoon.OutlineTexture >= 0 && Texture(mtoon.OutlineTexture) is int widthTexture and >= 0)
                    {
                        _edgeWidthTextures[_materials.Count] = new EdgeWidthTexture(widthTexture, mtoon.OutlineChannel);
                    }
                }
                if (mtoon.Matcap >= 0)
                {
                    sphere = Texture(mtoon.Matcap);
                }
            }
            return new PmxMaterial
            {
                Name = GltfFile.String(m, "name") is { Length: > 0 } name ? name : $"材質{index + 1}",
                NameUniversal = GltfFile.String(m, "name") ?? string.Empty,
                Diffuse = new Vector4(color, opaque ? 1f : Math.Clamp(factor[3], 0f, 1f)),
                Ambient = color * AmbientShare,
                Flags = flags,
                EdgeColor = edgeColor,
                EdgeSize = edgeSize,
                TextureIndex = texture,
                ToonIndex = toon,
                SharedToon = false,
                SphereTextureIndex = sphere,
                SphereMode = sphere >= 0 ? PmxSphereMode.Add : PmxSphereMode.None,
                IndexCount = indexCount,
            };
        }

        // 影の色の画像（同じ色・ずれ・くっきりさは 1 つ）。TGA にして、ファイルの中の画像と同じく持たせる
        private int ShadeToon(Vector3 ratio, float shift, float toony)
        {
            string key = FormattableString.Invariant($"{ratio.X:F3},{ratio.Y:F3},{ratio.Z:F3},{shift:F2},{toony:F2}");
            if (_toonTextures.TryGetValue(key, out int known))
            {
                return known;
            }
            int index = _textures.Count;
            _textures.Add($"mtoon_shade_{_toonTextures.Count + 1}.tga");
            _embedded[index] = MToon.ToonTga(MToon.ToonColumn(ratio, shift, toony));
            _toonTextures[key] = index;
            return index;
        }

        // VRM 0.x の材質の設定（materialProperties。名前で glTF の材質と結ぶ。名前が無ければ同じ番号）
        private Dictionary<int, JsonElement> ReadVrm0Materials()
        {
            var result = new Dictionary<int, JsonElement>();
            if (version != VrmVersion.Vrm0 || !GltfFile.TryGet(file.Root, "extensions", out JsonElement extensions)
                || !GltfFile.TryGet(extensions, "VRM", out JsonElement vrm0) || !GltfFile.TryGet(vrm0, "materialProperties", out JsonElement properties)
                || properties.ValueKind != JsonValueKind.Array)
            {
                return result;
            }
            int i = 0;
            foreach (JsonElement p in properties.EnumerateArray())
            {
                int material = GltfFile.String(p, "name") is { Length: > 0 } name ? MaterialByName(name) : -1;
                result.TryAdd(material >= 0 ? material : i, p);
                i++;
            }
            return result;
        }

        // glTF のテクスチャ → PMX のテクスチャの番号（画像ごとに 1 つ。中に入っている画像は、中身を持たせる）
        private int Texture(int textureIndex)
        {
            if (textureIndex < 0 || textureIndex >= file.Count("textures"))
            {
                return -1;
            }
            int image = GltfFile.Int(file.Item("textures", textureIndex), "source", -1);
            if (image < 0 || image >= file.Count("images"))
            {
                return -1;
            }
            if (_textureOfImage.TryGetValue(image, out int known))
            {
                return known;
            }
            JsonElement element = file.Item("images", image);
            string? uri = GltfFile.String(element, "uri");
            int pmxIndex = _textures.Count;
            if (uri is not null && !uri.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
            {
                _textures.Add(Uri.UnescapeDataString(uri)); // .gltf の隣の画像のファイル（ふつうのテクスチャと同じく探す）
            }
            else
            {
                try
                {
                    var (data, mime) = file.ReadImage(image, baseDirectory);
                    string extension = mime switch
                    {
                        "image/jpeg" => ".jpg",
                        "image/png" => ".png",
                        _ => string.Empty,
                    };
                    string imageName = GltfFile.String(element, "name") is { Length: > 0 } n ? n : $"image{image}";
                    _textures.Add(Path.HasExtension(imageName) ? imageName : imageName + extension);
                    _embedded[pmxIndex] = data;
                }
                catch (ModelFormatException ex)
                {
                    report.Warning(LoadIssueCategory.Format, $"画像 {image} を読めませんでした（{ex.Message}）。そのテクスチャなしで表示します");
                    _textureOfImage[image] = -1;
                    return -1;
                }
            }
            _textureOfImage[image] = pmxIndex;
            return pmxIndex;
        }

        // ---- 揺れ物（4 段目） ----

        // 揺れるボーン（ノードの番号のまま）。先端は次のノードか、末端なら骨の向きに 7 cm 先（glTF の座標・腕を A の字にした後）
        private sealed record SpringSpec(int Node, int TailNode, Vector3? VirtualTail, float Stiffness, float GravityPower, Vector3 GravityDirection,
            float DragForce, float HitRadius, int[] Colliders, string Chain);

        // 当たる物（ノードの番号・ノードの中での位置と半径。Tail があればカプセル）
        private sealed record ColliderSpec(int Node, Vector3 Offset, float Radius, Vector3? Tail);

        private const float VirtualTailLength = 0.07f; // 末端のボーンの先（VRM 0.x と同じ 7 cm）

        // ノードの初めの姿勢（腕を A の字にした後）での位置（glTF の座標）
        private Vector3 PosedGltf(int node) => Vector3.Transform(_nodes[node].World.Translation, _pose[node]);

        private float ScaleOf(int node) => Vector3.TransformNormal(Vector3.UnitX, _nodes[node].World).Length();

        // VRM の揺れ物の設定を読む（1.0 の VRMC_springBone・0.x の secondaryAnimation）
        private void ReadSprings()
        {
            if (!GltfFile.TryGet(file.Root, "extensions", out JsonElement extensions))
            {
                return;
            }
            if (version == VrmVersion.Vrm1 && GltfFile.TryGet(extensions, "VRMC_springBone", out JsonElement spring))
            {
                foreach (JsonElement c in Items(spring, "colliders"))
                {
                    int node = GltfFile.Int(c, "node", -1);
                    if ((uint)node >= (uint)_nodes.Length || !GltfFile.TryGet(c, "shape", out JsonElement shape))
                    {
                        _colliderSpecs.Add(null);
                        continue;
                    }
                    if (GltfFile.TryGet(shape, "capsule", out JsonElement capsule))
                    {
                        _colliderSpecs.Add(new ColliderSpec(node, Vector(capsule, "offset"), GltfFile.Float(capsule, "radius", 0f), Vector(capsule, "tail")));
                    }
                    else if (GltfFile.TryGet(shape, "sphere", out JsonElement sphere))
                    {
                        _colliderSpecs.Add(new ColliderSpec(node, Vector(sphere, "offset"), GltfFile.Float(sphere, "radius", 0f), null));
                    }
                    else
                    {
                        _colliderSpecs.Add(null);
                    }
                }
                List<int[]> groups = [.. Items(spring, "colliderGroups").Select(g => GltfFile.Ints(g, "colliders").ToArray())];
                int index = 0;
                foreach (JsonElement s in Items(spring, "springs"))
                {
                    string chain = GltfFile.String(s, "name") is { Length: > 0 } n ? n : $"揺れ物{index + 1}";
                    index++;
                    int[] colliders = ColliderUnion(groups, GltfFile.Ints(s, "colliderGroups"));
                    JsonElement[] joints = [.. Items(s, "joints")];
                    // 1.0: 揺れるのは最後の 1 つの手前まで（最後のジョイントは先端だけ）
                    for (int i = 0; i + 1 < joints.Length; i++)
                    {
                        int node = GltfFile.Int(joints[i], "node", -1);
                        int tail = GltfFile.Int(joints[i + 1], "node", -1);
                        if ((uint)node >= (uint)_nodes.Length || (uint)tail >= (uint)_nodes.Length)
                        {
                            continue;
                        }
                        JsonElement j = joints[i];
                        float[] direction = GltfFile.Floats(j, "gravityDir", 3) ?? [0, -1, 0];
                        _springSpecs.Add(new SpringSpec(node, tail, null, GltfFile.Float(j, "stiffness", 1f), GltfFile.Float(j, "gravityPower", 0f),
                            new Vector3(direction[0], direction[1], direction[2]), GltfFile.Float(j, "dragForce", 0.5f), GltfFile.Float(j, "hitRadius", 0f),
                            colliders, chain));
                    }
                }
            }
            else if (version == VrmVersion.Vrm0 && GltfFile.TryGet(extensions, "VRM", out JsonElement vrm0)
                && GltfFile.TryGet(vrm0, "secondaryAnimation", out JsonElement secondary))
            {
                var groups = new List<int[]>();
                foreach (JsonElement g in Items(secondary, "colliderGroups"))
                {
                    int node = GltfFile.Int(g, "node", -1);
                    var members = new List<int>();
                    foreach (JsonElement c in Items(g, "colliders"))
                    {
                        if ((uint)node < (uint)_nodes.Length)
                        {
                            members.Add(_colliderSpecs.Count);
                            _colliderSpecs.Add(new ColliderSpec(node, Vector0(c, "offset"), GltfFile.Float(c, "radius", 0f), null));
                        }
                    }
                    groups.Add([.. members]);
                }
                var seen = new HashSet<int>();
                int index = 0;
                foreach (JsonElement g in Items(secondary, "boneGroups"))
                {
                    string chain = GltfFile.String(g, "comment") is { Length: > 0 } n ? n : $"揺れ物{index + 1}";
                    index++;
                    int[] colliders = ColliderUnion(groups, GltfFile.Ints(g, "colliderGroups"));
                    Vector3 direction = GltfFile.TryGet(g, "gravityDir", out _) ? Vector0(g, "gravityDir") : new Vector3(0, -1, 0);
                    float stiffness = GltfFile.Float(g, "stiffiness", GltfFile.Float(g, "stiffness", 1f)); // 0.x の名前は綴りが違う（stiffiness）
                    float gravity = GltfFile.Float(g, "gravityPower", 0f);
                    float drag = GltfFile.Float(g, "dragForce", 0.4f);
                    float hit = GltfFile.Float(g, "hitRadius", 0.02f);
                    // 0.x: 書かれたボーンから先の、すべてのボーンが揺れる。先端は初めの子、子が無ければ骨の向きに 7 cm 先
                    var stack = new Stack<int>(GltfFile.Ints(g, "bones").Where(n => (uint)n < (uint)_nodes.Length).Reverse());
                    while (stack.Count > 0)
                    {
                        int node = stack.Pop();
                        if (!seen.Add(node))
                        {
                            continue;
                        }
                        int[] children = [.. _nodes[node].Children.Where(c => (uint)c < (uint)_nodes.Length && _nodes[c].Parent == node)];
                        Vector3? virtualTail = null;
                        int tail = -1;
                        if (children.Length > 0)
                        {
                            tail = children[0];
                        }
                        else
                        {
                            Vector3 head = PosedGltf(node);
                            Vector3 from = _nodes[node].Parent >= 0 ? PosedGltf(_nodes[node].Parent) : head - Vector3.UnitY;
                            Vector3 along = head - from;
                            virtualTail = head + (along.LengthSquared() > 1e-12f ? Vector3.Normalize(along) : -Vector3.UnitY) * VirtualTailLength * ScaleOf(node);
                        }
                        _springSpecs.Add(new SpringSpec(node, tail, virtualTail, stiffness, gravity, direction, drag, hit, colliders, chain));
                        for (int c = children.Length - 1; c >= 0; c--)
                        {
                            stack.Push(children[c]);
                        }
                    }
                }
            }
        }

        private static IEnumerable<JsonElement> Items(JsonElement owner, string name) =>
            GltfFile.TryGet(owner, name, out JsonElement array) && array.ValueKind == JsonValueKind.Array ? array.EnumerateArray() : [];

        private static int[] ColliderUnion(List<int[]> groups, IEnumerable<int> chosen) =>
            [.. chosen.Where(g => (uint)g < (uint)groups.Count).SelectMany(g => groups[g]).Distinct()];

        private static Vector3 Vector(JsonElement owner, string name) =>
            GltfFile.Floats(owner, name, 3) is { } v ? new Vector3(v[0], v[1], v[2]) : Vector3.Zero;

        // VRM 0.x の {x, y, z} の形
        private static Vector3 Vector0(JsonElement owner, string name) =>
            GltfFile.TryGet(owner, name, out JsonElement v)
                ? new Vector3(GltfFile.Float(v, "x", 0f), GltfFile.Float(v, "y", 0f), GltfFile.Float(v, "z", 0f))
                : Vector3.Zero;

        // ボーンの番号と、MMD の座標にする（揺れるボーン・当たる物が付くノードは、ボーンになっている）
        private SpringBoneSet? BuildSpringBones()
        {
            if (_springSpecs.Count == 0)
            {
                return null;
            }
            var colliders = new List<SpringBoneCollider>();
            var colliderIndex = new Dictionary<int, int>();
            for (int i = 0; i < _colliderSpecs.Count; i++)
            {
                if (_colliderSpecs[i] is not { } c || !_boneOf.TryGetValue(c.Node, out int bone))
                {
                    continue;
                }
                Matrix4x4 placement = _nodes[c.Node].World * _pose[c.Node];
                colliderIndex[i] = colliders.Count;
                colliders.Add(new SpringBoneCollider(bone, Point(Vector3.Transform(c.Offset, placement)), c.Radius * ScaleOf(c.Node) * MetersToMmd,
                    c.Tail is { } tail ? Point(Vector3.Transform(tail, placement)) : null));
            }
            var joints = new List<SpringBoneJoint>();
            foreach (SpringSpec s in _springSpecs)
            {
                if (!_boneOf.TryGetValue(s.Node, out int bone))
                {
                    continue;
                }
                Vector3 tail = s.VirtualTail is { } v ? Point(v) : PosedPoint(s.TailNode);
                Vector3 gravity = Direction(s.GravityDirection);
                joints.Add(new SpringBoneJoint(bone, tail, s.Stiffness, s.GravityPower,
                    gravity.LengthSquared() > 1e-12f ? Vector3.Normalize(gravity) : -Vector3.UnitY, Math.Clamp(s.DragForce, 0f, 1f),
                    s.HitRadius * ScaleOf(s.Node) * MetersToMmd, [.. s.Colliders.Where(colliderIndex.ContainsKey).Select(c => colliderIndex[c])], s.Chain));
            }
            _springChains = joints.Select(j => j.Chain).Distinct().Count();
            return joints.Count == 0 ? null : new SpringBoneSet(joints, colliders, MetersToMmd);
        }

        // ---- 表情（3 段目） ----

        // VRM の表情を、PMX の表情にする。形の変化は、指している表情の元（メッシュと番号ごと）を強さで混ぜた頂点モーフ、
        // テクスチャの位置をずらすのは、その材質を使う頂点の UV モーフ（uv' = uv × 大きさ + ずらす量 の差）、
        // 材質の色は、その材質の PMX の材質（形ごとに分かれている）への足す材質モーフ。2 種類以上あれば、それぞれを部品の表情にして、
        // 決まった名前の表情は、それらを 1 倍ずつ混ぜるグループモーフにする。中身の無い表情（neutral など）は作らない
        private void BuildExpressions()
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            var specs = new List<(string Name, PmxMorphPanel Panel, List<PmxVertexOffset> Vertex, List<PmxUvOffset> Uv, List<PmxMaterialOffset> Material)>();
            foreach (VrmExpression e in _expressions.OrderBy(x => VrmExpressions.Order(x.Key)))
            {
                var vertex = new Dictionary<int, Vector3>();
                foreach (var (mesh, target, weight) in e.MorphBinds)
                {
                    if (_targetOffsets.TryGetValue((mesh, target), out List<PmxVertexOffset>? offsets))
                    {
                        foreach (PmxVertexOffset o in offsets)
                        {
                            vertex[o.Vertex] = vertex.GetValueOrDefault(o.Vertex) + o.Translation * weight;
                        }
                    }
                }
                var uv = new Dictionary<int, Vector2>();
                foreach (var (material, scale, offset) in e.UvBinds)
                {
                    if (_materialVertices.TryGetValue(material, out List<(int Vertex, Vector2 Uv)>? list))
                    {
                        foreach (var (v, coordinate) in list)
                        {
                            uv[v] = uv.GetValueOrDefault(v) + coordinate * (scale - Vector2.One) + offset;
                        }
                    }
                }
                var colors = new List<PmxMaterialOffset>();
                foreach (var (material, type, color) in e.ColorBinds)
                {
                    if (type != "color")
                    {
                        _unsupportedColorBinds++;
                        continue;
                    }
                    if (!_pmxMaterialsOf.TryGetValue(material, out List<int>? pmxMaterials))
                    {
                        continue;
                    }
                    Vector4 target = e.ColorIsLinear ? new Vector4(LinearToSrgb(color.X), LinearToSrgb(color.Y), LinearToSrgb(color.Z), color.W) : color;
                    foreach (int p in pmxMaterials)
                    {
                        Vector4 delta = target - _materials[p].Diffuse;
                        colors.Add(new PmxMaterialOffset(p, PmxMaterialOperation.Add, delta, Vector3.Zero, 0f, new Vector3(delta.X, delta.Y, delta.Z) * AmbientShare,
                            Vector4.Zero, 0f, Vector4.Zero, Vector4.Zero, Vector4.Zero));
                    }
                }
                List<PmxVertexOffset> vertexOffsets = [.. vertex.Where(kv => kv.Value.LengthSquared() > 1e-12f).Select(kv => new PmxVertexOffset(kv.Key, kv.Value))];
                List<PmxUvOffset> uvOffsets = [.. uv.Where(kv => kv.Value.LengthSquared() > 1e-12f).Select(kv => new PmxUvOffset(kv.Key, new Vector4(kv.Value, 0, 0)))];
                if (vertexOffsets.Count == 0 && uvOffsets.Count == 0 && colors.Count == 0)
                {
                    continue;
                }
                var (name, panel) = VrmExpressions.MmdName(e.Key) ?? (e.Name, PmxMorphPanel.Other);
                specs.Add((Unique(name, names), panel, vertexOffsets, uvOffsets, colors));
                _binaryExpressions += e.IsBinary ? 1 : 0;
                _overrideExpressions += e.Overrides ? 1 : 0;
            }

            int top = specs.Count;
            foreach (var s in specs)
            {
                var parts = new List<PmxMorph>();
                if (s.Vertex.Count > 0)
                {
                    parts.Add(new PmxMorph { Name = s.Name + "（形）", NameUniversal = s.Name, Panel = s.Panel, Type = PmxMorphType.Vertex, VertexOffsets = s.Vertex });
                }
                if (s.Uv.Count > 0)
                {
                    parts.Add(new PmxMorph { Name = s.Name + "（UV）", NameUniversal = s.Name, Panel = s.Panel, Type = PmxMorphType.Uv, UvOffsets = s.Uv });
                }
                if (s.Material.Count > 0)
                {
                    parts.Add(new PmxMorph { Name = s.Name + "（色）", NameUniversal = s.Name, Panel = s.Panel, Type = PmxMorphType.Material, MaterialOffsets = s.Material });
                }
                if (parts.Count == 1)
                {
                    PmxMorph only = parts[0];
                    _expressionMorphs.Add(new PmxMorph
                    {
                        Name = s.Name,
                        NameUniversal = s.Name,
                        Panel = s.Panel,
                        Type = only.Type,
                        VertexOffsets = only.VertexOffsets,
                        UvOffsets = only.UvOffsets,
                        MaterialOffsets = only.MaterialOffsets,
                    });
                    continue;
                }
                var groups = new List<PmxGroupOffset>();
                foreach (PmxMorph part in parts)
                {
                    groups.Add(new PmxGroupOffset(top + _partMorphs.Count, 1f));
                    _partMorphs.Add(new PmxMorph
                    {
                        Name = Unique(part.Name, names),
                        NameUniversal = part.NameUniversal,
                        Panel = PmxMorphPanel.Other,
                        Type = part.Type,
                        VertexOffsets = part.VertexOffsets,
                        UvOffsets = part.UvOffsets,
                        MaterialOffsets = part.MaterialOffsets,
                    });
                }
                _expressionMorphs.Add(new PmxMorph { Name = s.Name, NameUniversal = s.Name, Panel = s.Panel, Type = PmxMorphType.Group, GroupOffsets = groups });
            }
            _expressionNames.UnionWith(names);
        }

        private static string Unique(string name, HashSet<string> names)
        {
            string result = name;
            for (int k = 2; !names.Add(result); k++)
            {
                result = $"{name}_{k}";
            }
            return result;
        }

        private int MaterialByName(string name)
        {
            for (int i = 0; i < file.Count("materials"); i++)
            {
                if (GltfFile.String(file.Item("materials", i), "name") == name)
                {
                    return i;
                }
            }
            return -1;
        }

        // ---- まとめ ----

        public void ReportUnsupported(VrmVersion version, string kind)
        {
            if (_humanoidApplied)
            {
                report.Info(LoadIssueCategory.Note,
                    "VRM の人型のボーンを、MMD の名前（センター・上半身・頭・左腕 など）に付け替え、足の IK と両目を足しました。MMD のモーションと顔のトラッキングで動かせます");
                if (_posedArms > 0)
                {
                    report.Info(LoadIssueCategory.Note, $"腕を、MMD と同じ A の字（水平から {ArmAngleDegrees:0} 度下げた形）にして読み込みました（VRM は T の字で作られているため）");
                }
            }
            else if (version != VrmVersion.None)
            {
                report.Warning(LoadIssueCategory.Unsupported,
                    "VRM の人型のボーンの決まり（腰の hips）が見つからないため、ボーンの名前は元のままです（MMD のモーションと顔のトラッキングは当てはまりません）");
            }
            else
            {
                report.Info(LoadIssueCategory.Note, $"{kind} には人型のボーンの決まりが無いため、ボーンの名前は元のままです（MMD のモーションは当てはまりません）");
            }
            if (_expressionMorphs.Count > 0)
            {
                string sample = string.Join("・", _expressionMorphs.Take(8).Select(m => m.Name));
                report.Info(LoadIssueCategory.Note,
                    $"VRM の表情を、MMD の表情（{sample} など {_expressionMorphs.Count} 個）にしました。元の形の変化も、名前のまま残しています");
            }
            if (_lookAtByExpression && _humanoidApplied)
            {
                report.Info(LoadIssueCategory.Note, "目線を表情で動かす VRM なので、両目のボーンは作らず、顔のトラッキングの目線は「視線上・視線下・視線左・視線右」の表情で動かします");
            }
            if (_binaryExpressions > 0)
            {
                report.Info(LoadIssueCategory.Unsupported, $"「0 か 1 か」で切り替える表情（isBinary）が {_binaryExpressions} 個あります。途中の強さも、そのまま混ぜて使います");
            }
            if (_overrideExpressions > 0)
            {
                report.Info(LoadIssueCategory.Unsupported, $"表情が出ている間、まばたきなどを止める決まり（override）は、まだ使いません（{_overrideExpressions} 個）");
            }
            if (_unsupportedColorBinds > 0)
            {
                report.Info(LoadIssueCategory.Unsupported, $"表情の材質の変化のうち、色以外（影の色・発光など）は、まだ使いません（{_unsupportedColorBinds} か所）");
            }
            if (_springBoneSet is { } springs)
            {
                report.Info(LoadIssueCategory.Note,
                    $"髪などの揺れ（VRM のスプリングボーン {_springChains} 組・揺れるボーン {springs.Joints.Count} 本・当たる物 {springs.Colliders.Count} 個）を、" +
                    "物理演算の中で VRM と同じ決まりで揺らします");
            }
            else if (_springBones)
            {
                report.Info(LoadIssueCategory.Unsupported, "髪などの揺れ（VRM のスプリングボーン）の設定を読めませんでした（揺らしません）");
            }
            if (_mtoonMaterials > 0)
            {
                report.Info(LoadIssueCategory.Note,
                    $"VRM のアニメ調の材質（MToon）{_mtoonMaterials} 個の影の色を MMD のトゥーン（{_toonTextures.Count} 枚）に、" +
                    $"輪郭線（{_outlineMaterials} 個）を MMD の輪郭線にしました。MMD の輪郭線は画面の上で太さが決まるので、上半身を写したときの太さに合わせています。" +
                    "セルフシャドウを受ける面は、MMD と同じく、影の境目のずれとくっきりさを使わずに描きます");
            }
            if (_mtoonUnsupported.Count > 0)
            {
                report.Info(LoadIssueCategory.Unsupported, $"MToon の{string.Join("・", _mtoonUnsupported)}は、まだ使いません");
            }
            if (_textureTransforms > 0)
            {
                report.Info(LoadIssueCategory.Unsupported, $"テクスチャの位置・大きさの変更（KHR_texture_transform）は、まだ使いません（{_textureTransforms} か所）");
            }
            if (_maskMaterials > 0)
            {
                report.Info(LoadIssueCategory.Unsupported, $"透けた所を切り抜く材質（alphaMode が MASK）が {_maskMaterials} 個あります。切り抜かずに半透明として描きます");
            }
            if (_skippedPrimitives > 0)
            {
                report.Info(LoadIssueCategory.Unsupported, $"三角形でない形（点・線）や位置の無い形が {_skippedPrimitives} 個あります。描きません");
            }
            if (_computedNormals > 0)
            {
                report.Info(LoadIssueCategory.Note, $"面の向き（法線）の無い形が {_computedNormals} 個あったため、面の形から求めました");
            }
        }

        public PmxDocument ToDocument(string name, string universalName, string comment)
        {
            // 並び: MMD の名前の表情 → その部品 → 元の形の変化（名前のまま。MMD の名前と重なれば「_2」を足す）
            PmxMorph[] morphs =
            [
                .. _expressionMorphs,
                .. _partMorphs,
                .. _morphOrder.Select(n => new PmxMorph
                {
                    Name = Unique(n, _expressionNames),
                    NameUniversal = n,
                    Panel = PmxMorphPanel.Other,
                    Type = PmxMorphType.Vertex,
                    VertexOffsets = _morphs[n],
                }),
            ];
            return new PmxDocument
            {
                Version = 2.0f,
                Encoding = PmxTextEncoding.Utf16,
                IndexSizes = new PmxIndexSizes(4, 4, 4, 4, 4, 4),
                Name = name,
                NameUniversal = universalName,
                Comment = comment,
                CommentUniversal = comment,
                Vertices = [.. _vertices],
                Indices = [.. _indices],
                Textures = [.. _textures],
                EmbeddedTextures = _embedded,
                Materials = [.. _materials],
                Bones = [.. _bones],
                Morphs = morphs,
                SpringBones = _springBoneSet,
                EdgeWidthTextures = _edgeWidthTextures,
                DisplayFrames =
                [
                    new PmxDisplayFrame { Name = "Root", NameUniversal = "Root", IsSpecial = true, Elements = [new PmxDisplayElement(PmxDisplayTarget.Bone, 0)] },
                    new PmxDisplayFrame
                    {
                        Name = "表情",
                        NameUniversal = "Exp",
                        IsSpecial = true,
                        Elements = [.. Enumerable.Range(0, morphs.Length).Select(i => new PmxDisplayElement(PmxDisplayTarget.Morph, i))],
                    },
                    new PmxDisplayFrame
                    {
                        Name = "ボーン",
                        NameUniversal = "Bones",
                        Elements = [.. Enumerable.Range(1, Math.Max(0, _bones.Count - 1)).Select(i => new PmxDisplayElement(PmxDisplayTarget.Bone, i))],
                    },
                ],
            };
        }
    }
}
