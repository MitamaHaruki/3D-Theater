using System.Globalization;
using System.IO;
using System.Numerics;
using System.Text;

namespace Theater3D.Formats.Vmd;

/// <summary>
/// VMD（版 2、モデル名 20 バイト）を書く。名前は Shift-JIS で、決まった長さに入らない分は MMD と同じく切る。
/// 補間は MMD と同じ 64 バイトの並び（最初の 16 バイトに 4 本の曲線、後ろはそれを 1 バイトずつずらした写し）。
/// </summary>
public static class VmdWriter
{
    public static byte[] Write(VmdDocument document)
    {
        using var stream = new MemoryStream();
        using var w = new BinaryWriter(stream);
        byte[] signature = new byte[30];
        Encoding.ASCII.GetBytes("Vocaloid Motion Data 0002").CopyTo(signature, 0);
        w.Write(signature);
        Name(w, document.ModelName, VmdText.ModelNameBytes);

        w.Write(document.BoneKeys.Count);
        foreach (VmdBoneKey k in document.BoneKeys)
        {
            Name(w, k.Bone, VmdText.BoneNameBytes);
            w.Write(k.Frame);
            V3(w, k.Translation);
            w.Write(k.Rotation.X);
            w.Write(k.Rotation.Y);
            w.Write(k.Rotation.Z);
            w.Write(k.Rotation.W);
            byte[] ip = k.Interpolation.Length == 64 ? k.Interpolation : Interpolation((20, 20, 107, 107), (20, 20, 107, 107), (20, 20, 107, 107), (20, 20, 107, 107));
            w.Write(ip);
        }

        w.Write(document.MorphKeys.Count);
        foreach (VmdMorphKey k in document.MorphKeys)
        {
            Name(w, k.Morph, VmdText.MorphNameBytes);
            w.Write(k.Frame);
            w.Write(k.Weight);
        }

        w.Write(document.CameraKeys.Count);
        foreach (VmdCameraKey k in document.CameraKeys)
        {
            w.Write(k.Frame);
            w.Write(k.Distance);
            V3(w, k.Target);
            V3(w, k.Rotation);
            byte[] ip = k.Interpolation.Length == 24 ? k.Interpolation : new byte[24];
            w.Write(ip);
            w.Write(k.FieldOfView);
            w.Write((byte)(k.Orthographic ? 1 : 0));
        }

        w.Write(document.LightKeys.Count);
        foreach (VmdLightKey k in document.LightKeys)
        {
            w.Write(k.Frame);
            V3(w, k.Color);
            V3(w, k.Direction);
        }

        w.Write(document.ShadowKeys.Count);
        foreach (VmdShadowKey k in document.ShadowKeys)
        {
            w.Write(k.Frame);
            w.Write(k.Mode);
            w.Write(k.Distance);
        }

        w.Write(document.VisibilityKeys.Count);
        foreach (VmdVisibilityKey k in document.VisibilityKeys)
        {
            w.Write(k.Frame);
            w.Write((byte)(k.Visible ? 1 : 0));
            w.Write(k.IkStates.Count);
            foreach (VmdIkState s in k.IkStates)
            {
                Name(w, s.Bone, VmdText.IkNameBytes);
                w.Write((byte)(s.Enabled ? 1 : 0));
            }
        }
        w.Flush();
        return stream.ToArray();
    }

    /// <summary>X・Y・Z の移動と回転の曲線（制御点 0〜127）から、64 バイトの補間を作る。</summary>
    public static byte[] Interpolation((byte X1, byte Y1, byte X2, byte Y2) x, (byte X1, byte Y1, byte X2, byte Y2) y,
        (byte X1, byte Y1, byte X2, byte Y2) z, (byte X1, byte Y1, byte X2, byte Y2) rotation)
    {
        var curves = new[] { x, y, z, rotation };
        var row = new byte[16];
        for (int ch = 0; ch < 4; ch++)
        {
            row[ch] = Math.Min(curves[ch].X1, (byte)127);
            row[ch + 4] = Math.Min(curves[ch].Y1, (byte)127);
            row[ch + 8] = Math.Min(curves[ch].X2, (byte)127);
            row[ch + 12] = Math.Min(curves[ch].Y2, (byte)127);
        }
        // MMD は同じ並びを 1 バイトずつずらして 4 行書く（読むソフトは 1 行目を使う）
        var bytes = new byte[64];
        for (int r = 0; r < 4; r++)
        {
            for (int i = 0; i + r < 16; i++)
            {
                bytes[(r * 16) + i] = row[i + r];
            }
        }
        return bytes;
    }

    private static void Name(BinaryWriter w, VmdName name, int bytes)
    {
        var field = new byte[bytes];
        name.Bytes[..Math.Min(bytes, name.Bytes.Length)].CopyTo(field);
        w.Write(field);
    }

    private static void V3(BinaryWriter w, Vector3 v)
    {
        w.Write(v.X);
        w.Write(v.Y);
        w.Write(v.Z);
    }
}

/// <summary>VPD（ポーズ、Shift-JIS の文字）を書く。MMD と同じ書き方（小数 6 桁、注釈つき）。</summary>
public static class VpdWriter
{
    public static byte[] Write(VpdDocument document)
    {
        var text = new StringBuilder();
        text.Append("Vocaloid Pose Data file\r\n\r\n");
        text.Append(CultureInfo.InvariantCulture, $"{document.ModelFileName};\t\t// 親ファイル名\r\n");
        text.Append(CultureInfo.InvariantCulture, $"{document.Bones.Count};\t\t\t\t// 総ポーズボーン数\r\n\r\n");
        for (int i = 0; i < document.Bones.Count; i++)
        {
            VpdBone b = document.Bones[i];
            text.Append(CultureInfo.InvariantCulture, $"Bone{i}{{{b.Name}\r\n");
            text.Append(CultureInfo.InvariantCulture, $"  {F(b.Translation.X)},{F(b.Translation.Y)},{F(b.Translation.Z)};\t\t\t\t// trans x,y,z\r\n");
            text.Append(CultureInfo.InvariantCulture, $"  {F(b.Rotation.X)},{F(b.Rotation.Y)},{F(b.Rotation.Z)},{F(b.Rotation.W)};\t\t// Quaternion x,y,z,w\r\n");
            text.Append("}\r\n\r\n");
        }
        for (int i = 0; i < document.Morphs.Count; i++)
        {
            VpdMorph m = document.Morphs[i];
            text.Append(CultureInfo.InvariantCulture, $"Morph{i}{{{m.Name}\r\n  {F(m.Weight)};\r\n}}\r\n\r\n");
        }
        return VmdText.ShiftJis.GetBytes(text.ToString());
    }

    private static string F(float value) => value.ToString("0.000000", CultureInfo.InvariantCulture);
}
