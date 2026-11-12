namespace Theater3D.Formats.XFile;

/// <summary>.x のデータの 1 つ（Mesh・Material など）。中の数・文字列と、入れ子のデータ・参照を、書かれた順に持つ。</summary>
internal sealed class XObject(string type, string? name)
{
    public string Type { get; } = type;

    public string? Name { get; } = name;

    /// <summary>中の数（整数も小数も、書かれた順に）。</summary>
    public List<double> Numbers { get; } = [];

    public List<string> Strings { get; } = [];

    /// <summary>入れ子のデータ（<see cref="XObject"/>）と参照（<see cref="XReference"/>）を、書かれた順に。</summary>
    public List<object> Members { get; } = [];

    public IEnumerable<XObject> Children => Members.OfType<XObject>();

    public XObject? Child(string type) => Children.FirstOrDefault(c => c.Type.Equals(type, StringComparison.OrdinalIgnoreCase));
}

/// <summary>ほかの場所に名前付きで書いたデータを指す（「{ 名前 }」）。</summary>
internal sealed record XReference(string Name);

/// <summary>字の並びから、データの入れ子を組み立てる（テンプレートの定めは読み飛ばす）。</summary>
internal static class XFileParser
{
    public static List<XObject> Parse(IReadOnlyList<XToken> tokens)
    {
        var objects = new List<XObject>();
        int i = 0;
        while (i < tokens.Count)
        {
            XToken token = tokens[i];
            if (token.Kind == XTokenKind.Template)
            {
                i = SkipTemplate(tokens, i + 1);
            }
            else if (token.Kind == XTokenKind.Name && StartsObject(tokens, i))
            {
                objects.Add(ParseObject(tokens, ref i));
            }
            else
            {
                i++;
            }
        }
        return objects;
    }

    // 名前の後ろに、名前（そのデータの名前）・GUID を挟んで「{」が来れば、データの始まり
    private static bool StartsObject(IReadOnlyList<XToken> tokens, int i)
    {
        int j = i + 1;
        if (j < tokens.Count && tokens[j].Kind == XTokenKind.Name)
        {
            j++;
        }
        if (j < tokens.Count && tokens[j].Kind == XTokenKind.Guid)
        {
            j++;
        }
        return j < tokens.Count && tokens[j].Kind == XTokenKind.OpenBrace;
    }

    private static int SkipTemplate(IReadOnlyList<XToken> tokens, int i)
    {
        while (i < tokens.Count && tokens[i].Kind != XTokenKind.OpenBrace)
        {
            i++;
        }
        int depth = 0;
        for (; i < tokens.Count; i++)
        {
            if (tokens[i].Kind == XTokenKind.OpenBrace)
            {
                depth++;
            }
            else if (tokens[i].Kind == XTokenKind.CloseBrace && --depth == 0)
            {
                return i + 1;
            }
        }
        return i;
    }

    private static XObject ParseObject(IReadOnlyList<XToken> tokens, ref int i)
    {
        string type = tokens[i++].Text!;
        string? name = null;
        if (tokens[i].Kind == XTokenKind.Name)
        {
            name = tokens[i++].Text;
        }
        if (tokens[i].Kind == XTokenKind.Guid)
        {
            i++;
        }
        i++; // {
        var obj = new XObject(type, name);
        while (i < tokens.Count)
        {
            XToken token = tokens[i];
            switch (token.Kind)
            {
                case XTokenKind.CloseBrace:
                    i++;
                    return obj;
                case XTokenKind.Integer or XTokenKind.Float:
                    obj.Numbers.Add(token.Number);
                    i++;
                    break;
                case XTokenKind.String:
                    obj.Strings.Add(token.Text!);
                    i++;
                    break;
                case XTokenKind.OpenBrace:
                {
                    // 参照: { 名前 } か { GUID } か { 名前 GUID }
                    i++;
                    string? target = null;
                    while (i < tokens.Count && tokens[i].Kind != XTokenKind.CloseBrace)
                    {
                        if (tokens[i].Kind == XTokenKind.Name)
                        {
                            target ??= tokens[i].Text;
                        }
                        i++;
                    }
                    i++;
                    if (target is not null)
                    {
                        obj.Members.Add(new XReference(target));
                    }
                    break;
                }
                case XTokenKind.Name when StartsObject(tokens, i):
                    obj.Members.Add(ParseObject(tokens, ref i));
                    break;
                default:
                    i++; // 区切り・GUID・（値の位置の）名前は読み飛ばす
                    break;
            }
        }
        throw new ModelFormatException($"「{type}」の終わり（}}）がありません");
    }
}
