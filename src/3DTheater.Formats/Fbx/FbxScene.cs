using System.Numerics;

namespace Theater3D.Formats.Fbx;

/// <summary>
/// FBX の物（Objects の中の Model・Geometry・Material・Deformer など）。つながり（Connections）で親と子を持つ。
/// 値（Properties70 の P）が無ければ、種類ごとの初めの値（Definitions の PropertyTemplate）を使う。
/// </summary>
internal sealed class FbxObject(long id, string kind, string name, string type, FbxNode node,
    Dictionary<string, FbxNode> properties, Dictionary<string, FbxNode>? template)
{
    public long Id { get; } = id;

    /// <summary>物の種類（Model・Geometry・Material・Texture・Video・Deformer・NodeAttribute・Pose など）。</summary>
    public string Kind { get; } = kind;

    public string Name { get; } = name;

    /// <summary>細かい種類（Model なら Mesh・LimbNode・Null、Deformer なら Skin・Cluster・BlendShape など）。</summary>
    public string Type { get; } = type;

    public FbxNode Node { get; } = node;

    /// <summary>この物につながる子（つながりの順）。形は Model の子、材質も Model の子、皮膚の変形は形の子、など。</summary>
    public List<FbxObject> Children { get; } = [];

    /// <summary>値につながる子（テクスチャ → 材質の「DiffuseColor」など）。</summary>
    public List<(FbxObject Child, string Property)> PropertyChildren { get; } = [];

    /// <summary>この物がつながる親（つながりの順）。</summary>
    public List<FbxObject> Parents { get; } = [];

    /// <summary>場面のいちばん上（番号 0）につながっているか。</summary>
    public bool ConnectedToRoot { get; set; }

    public FbxNode? P(string name) =>
        properties.TryGetValue(name, out FbxNode? p) ? p : template is not null && template.TryGetValue(name, out FbxNode? t) ? t : null;

    public bool Has(string name) => properties.ContainsKey(name);

    /// <summary>P の値（4 つ目から）の数。</summary>
    public double Number(string name, double fallback) => P(name) is { } p && p.Properties.Length > 4 ? p.Double(4, fallback) : fallback;

    public Vector3 Vector(string name, Vector3 fallback) =>
        P(name) is { } p && p.Properties.Length > 6
            ? new Vector3((float)p.Double(4), (float)p.Double(5), (float)p.Double(6))
            : fallback;

    public string? Text(string name) => P(name) is { } p && p.Properties.Length > 4 ? p.Properties[4] as string : null;

    public IEnumerable<FbxObject> ChildrenOf(string kind, string? type = null) =>
        Children.Where(c => c.Kind == kind && (type is null || c.Type == type));

    public override string ToString() => $"{Kind}:{Type}:{Name}";
}

/// <summary>
/// FBX の場面（物とつながり・全体の設定）。木（<see cref="FbxDocument"/>）から作る。FBX 7.0 より前の形式（物を名前で結ぶ）は読まない。
/// </summary>
internal sealed class FbxScene
{
    private FbxScene(int version, IReadOnlyList<FbxObject> objects, FbxObject? settings, string creator)
    {
        Version = version;
        Objects = objects;
        Settings = settings;
        Creator = creator;
    }

    public int Version { get; }

    /// <summary>物（ファイルの順）。</summary>
    public IReadOnlyList<FbxObject> Objects { get; }

    /// <summary>全体の設定（GlobalSettings。軸の向き・長さの単位）。</summary>
    public FbxObject? Settings { get; }

    /// <summary>作ったソフト（Creator）。</summary>
    public string Creator { get; }

    public IEnumerable<FbxObject> OfKind(string kind, string? type = null) => Objects.Where(o => o.Kind == kind && (type is null || o.Type == type));

    public static FbxScene Build(FbxDocument document)
    {
        Dictionary<string, Dictionary<string, FbxNode>> templates = ReadTemplates(document);
        var objects = new List<FbxObject>();
        var byId = new Dictionary<long, FbxObject>();
        if (document.Node("Objects") is { } objectsNode)
        {
            foreach (FbxNode n in objectsNode.Children)
            {
                if (n.Properties.Length < 1 || !FbxNode.TryNumber(n.Properties[0], out double idValue))
                {
                    continue;
                }
                long id = n.Properties[0] is long l ? l : (long)idValue;
                string rawName = n.String(1);
                var obj = new FbxObject(id, n.Name, ObjectName(rawName, document.IsBinary), n.String(2), n, ReadProperties(n),
                    templates.GetValueOrDefault(n.Name));
                objects.Add(obj);
                byId.TryAdd(id, obj);
            }
        }
        if (document.Node("Connections") is { } connections)
        {
            foreach (FbxNode c in connections.ChildrenNamed("C"))
            {
                string kind = c.String(0);
                long child = c.Properties.Length > 1 && c.Properties[1] is long lc ? lc : c.Long(1);
                long parent = c.Properties.Length > 2 && c.Properties[2] is long lp ? lp : c.Long(2);
                if (!byId.TryGetValue(child, out FbxObject? childObject))
                {
                    continue;
                }
                if (parent == 0)
                {
                    childObject.ConnectedToRoot = true;
                    continue;
                }
                if (!byId.TryGetValue(parent, out FbxObject? parentObject))
                {
                    continue;
                }
                if (kind == "OP")
                {
                    parentObject.PropertyChildren.Add((childObject, c.String(3)));
                }
                else if (kind == "OO")
                {
                    parentObject.Children.Add(childObject);
                    childObject.Parents.Add(parentObject);
                }
            }
        }
        FbxObject? settings = null;
        if (document.Node("GlobalSettings") is { } global)
        {
            settings = new FbxObject(0, "GlobalSettings", "GlobalSettings", string.Empty, global, ReadProperties(global), null);
        }
        string creator = document.Node("Creator")?.String(0)
            ?? document.Node("FBXHeaderExtension")?.ChildString("Creator")
            ?? string.Empty;
        return new FbxScene(document.Version, objects, settings, creator);
    }

    /// <summary>
    /// 物の名前。バイナリは「名前\0\x01種類」、文字の FBX は「種類::名前」と書く。
    /// </summary>
    public static string ObjectName(string raw, bool binary)
    {
        if (binary)
        {
            int sep = raw.IndexOf("\0\u0001", StringComparison.Ordinal);
            return sep >= 0 ? raw[..sep] : raw;
        }
        int colon = raw.IndexOf("::", StringComparison.Ordinal);
        return colon >= 0 ? raw[(colon + 2)..] : raw;
    }

    // 値（Properties70 の P。古い 7.0 前の Properties60 の Property も）: 名前 → 節
    private static Dictionary<string, FbxNode> ReadProperties(FbxNode node)
    {
        var result = new Dictionary<string, FbxNode>(StringComparer.Ordinal);
        FbxNode? list = node.Child("Properties70") ?? node.Child("Properties60");
        if (list is null)
        {
            return result;
        }
        foreach (FbxNode p in list.Children)
        {
            if (p.Name is "P" or "Property" && p.Properties.Length > 0 && p.Properties[0] is string name)
            {
                result[name] = p.Name == "Property" ? Normalize60(p) : p;
            }
        }
        return result;
    }

    // Properties60 の Property は「名前, 種類, 印, 値...」（P より 1 つ少ない）。P と同じ並びにそろえる
    private static FbxNode Normalize60(FbxNode p) =>
        new("P", [p.Properties[0], p.Properties.Length > 1 ? p.Properties[1] : string.Empty, string.Empty, .. p.Properties.Skip(2)], []);

    // 種類ごとの初めの値（Definitions → ObjectType「Model」→ PropertyTemplate → Properties70）
    private static Dictionary<string, Dictionary<string, FbxNode>> ReadTemplates(FbxDocument document)
    {
        var result = new Dictionary<string, Dictionary<string, FbxNode>>(StringComparer.Ordinal);
        if (document.Node("Definitions") is not { } definitions)
        {
            return result;
        }
        foreach (FbxNode type in definitions.ChildrenNamed("ObjectType"))
        {
            if (type.Child("PropertyTemplate") is { } template)
            {
                result[type.String(0)] = ReadProperties(template);
            }
        }
        return result;
    }
}
