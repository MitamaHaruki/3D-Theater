using System.Globalization;
using System.IO;
using System.Numerics;
using System.Text;
using Plugin.Core;
using Theater3D.Plugin.Sdk;

namespace Theater3D.SamplePlugin;

/// <summary>
/// サンプルのファイルの形式「キーの CSV」: 選んだモデルのボーン・モーフのキーを、表計算ソフトで開ける CSV にする（UTF-8・BOM 付き）。
/// 1 行が 1 つのキー: 種類（bone / morph）, 名前, フレーム, 移動 X〜Z, 回転 X〜W（四元数）, 値（モーフ）。
/// </summary>
internal static class KeyCsv
{
    public const string Header = "種類,名前,フレーム,移動X,移動Y,移動Z,回転X,回転Y,回転Z,回転W,値";

    /// <summary>キー 1 つ（ボーンなら <see cref="Bone"/>、モーフなら <see cref="Morph"/>）。</summary>
    public sealed record Row(string Name, BoneKeyInfo? Bone, MorphKeyInfo? Morph);

    /// <summary>モデルのキーを集める（画面のスレッドで。SDK はそこから使う）。</summary>
    public static List<Row> Collect(ITheaterScene scene, string modelId)
    {
        var rows = new List<Row>();
        foreach (string bone in scene.GetBoneNames(modelId))
        {
            rows.AddRange(scene.GetBoneKeys(modelId, bone).Select(k => new Row(bone, k, null)));
        }
        foreach (string morph in scene.GetMorphNames(modelId))
        {
            rows.AddRange(scene.GetMorphKeys(modelId, morph).Select(k => new Row(morph, null, k)));
        }
        return rows;
    }

    /// <summary>書く（別のスレッドで。進み具合を知らせ、取り消しを確かめる）。</summary>
    public static void Write(string path, IReadOnlyList<Row> rows, IPluginTaskContext task)
    {
        using var writer = new StreamWriter(path, append: false, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
        writer.WriteLine(Header);
        for (int i = 0; i < rows.Count; i++)
        {
            if (i % 200 == 0)
            {
                task.Cancellation.ThrowIfCancellationRequested();
                task.Report(i / (double)rows.Count, $"{i:N0} / {rows.Count:N0} 個");
            }
            writer.WriteLine(Format(rows[i]));
        }
        task.Report(1, $"{rows.Count:N0} 個");
    }

    /// <summary>読む（別のスレッドで）。読めない行があれば、行の番号を付けて <see cref="FormatException"/>。</summary>
    public static List<Row> Read(string path, IPluginTaskContext task)
    {
        string[] lines = File.ReadAllLines(path, Encoding.UTF8);
        var rows = new List<Row>();
        for (int i = 0; i < lines.Length; i++)
        {
            if (i % 200 == 0)
            {
                task.Cancellation.ThrowIfCancellationRequested();
                task.Report(i / (double)Math.Max(1, lines.Length), $"{i:N0} / {lines.Length:N0} 行");
            }
            string line = lines[i];
            if (line.Length == 0 || (i == 0 && line.StartsWith("種類,", StringComparison.Ordinal)))
            {
                continue;
            }
            try
            {
                rows.Add(Parse(SplitFields(line)));
            }
            catch (FormatException ex)
            {
                throw new FormatException($"{i + 1} 行目を読めません（{ex.Message}）: {line}", ex);
            }
        }
        return rows;
    }

    private static string Format(Row row)
    {
        static string N(float v) => v.ToString("R", CultureInfo.InvariantCulture);
        string name = Quote(row.Name);
        return row.Bone is { } b
            ? $"bone,{name},{b.Frame},{N(b.Translation.X)},{N(b.Translation.Y)},{N(b.Translation.Z)},{N(b.Rotation.X)},{N(b.Rotation.Y)},{N(b.Rotation.Z)},{N(b.Rotation.W)},"
            : $"morph,{name},{row.Morph!.Value.Frame},,,,,,,,{N(row.Morph.Value.Weight)}";
    }

    private static Row Parse(List<string> f)
    {
        if (f.Count < 11)
        {
            throw new FormatException($"項目が {f.Count} 個しかありません（11 個にしてください）");
        }
        float F(int index) => float.TryParse(f[index], NumberStyles.Float, CultureInfo.InvariantCulture, out float value) && float.IsFinite(value)
            ? value
            : throw new FormatException($"{index + 1} 番目の項目「{f[index]}」が数ではありません");
        int frame = int.TryParse(f[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsed) && parsed >= 0
            ? parsed
            : throw new FormatException($"フレーム「{f[2]}」が 0 以上の整数ではありません");
        return f[0] switch
        {
            "bone" => new Row(f[1], new BoneKeyInfo(frame, new Vector3(F(3), F(4), F(5)), new Quaternion(F(6), F(7), F(8), F(9))), null),
            "morph" => new Row(f[1], null, new MorphKeyInfo(frame, F(10))),
            _ => throw new FormatException($"種類「{f[0]}」は bone か morph にしてください"),
        };
    }

    // 「,」「"」を含む名前は "" で囲み、中の " は 2 つにする
    private static string Quote(string text) =>
        text.IndexOfAny([',', '"', '\n', '\r']) < 0 ? text : "\"" + text.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";

    private static List<string> SplitFields(string line)
    {
        var fields = new List<string>();
        var current = new StringBuilder();
        bool quoted = false;
        for (int i = 0; i < line.Length; i++)
        {
            char c = line[i];
            if (quoted)
            {
                if (c == '"' && i + 1 < line.Length && line[i + 1] == '"')
                {
                    current.Append('"');
                    i++;
                }
                else if (c == '"')
                {
                    quoted = false;
                }
                else
                {
                    current.Append(c);
                }
            }
            else if (c == '"')
            {
                quoted = true;
            }
            else if (c == ',')
            {
                fields.Add(current.ToString());
                current.Clear();
            }
            else
            {
                current.Append(c);
            }
        }
        fields.Add(current.ToString());
        return fields;
    }
}
