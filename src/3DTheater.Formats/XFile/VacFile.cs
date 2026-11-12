using System.Globalization;
using System.Numerics;
using Theater3D.Formats.Vmd;

namespace Theater3D.Formats.XFile;

/// <summary>
/// MMD のアクセサリの置き方の設定（.vac。Shift-JIS の行）。
/// 1 行目 名前、2 行目 .x のファイル（.vac から見た場所）、3 行目 大きさ、4 行目 位置「x,y,z」、5 行目 回転「x,y,z」（ラジアン）、
/// 6 行目 付けるボーンの名前（空なら付けない）。位置・回転は付けるボーンから見た値（10 倍しない。頂点だけ 10 倍する）。
/// </summary>
public sealed record VacFile(string Name, string XFile, float Scale, Vector3 Position, Vector3 RotationRadians, string? BoneName)
{
    /// <summary>回転（度）。</summary>
    public Vector3 RotationDegrees => RotationRadians * (180f / MathF.PI);

    public static VacFile Load(string path) => Parse(File.ReadAllBytes(path));

    public static VacFile Parse(byte[] data)
    {
        string[] lines = VmdText.ShiftJis.GetString(data).Replace("\r\n", "\n").Split('\n');
        string Line(int i) => i < lines.Length ? lines[i].Trim() : string.Empty;
        string xfile = Line(1);
        if (xfile.Length == 0 || !xfile.EndsWith(".x", StringComparison.OrdinalIgnoreCase))
        {
            throw new ModelFormatException(".vac の 2 行目に、.x のファイルの名前がありません");
        }
        float scale = TryFloat(Line(2), out float s) && s > 0 ? s : 1f;
        string bone = Line(5);
        return new VacFile(Line(0), xfile, scale, Vector(Line(3)), Vector(Line(4)), bone.Length > 0 && !bone.StartsWith("//", StringComparison.Ordinal) ? bone : null);
    }

    private static Vector3 Vector(string text)
    {
        string[] parts = text.Split(',', StringSplitOptions.TrimEntries);
        float At(int i) => i < parts.Length && TryFloat(parts[i], out float v) ? v : 0f;
        return new Vector3(At(0), At(1), At(2));
    }

    private static bool TryFloat(string text, out float value) =>
        float.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value) && float.IsFinite(value);
}
