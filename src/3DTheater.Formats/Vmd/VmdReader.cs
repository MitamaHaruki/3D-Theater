using System.IO;
using System.Text;

namespace Theater3D.Formats.Vmd;

/// <summary>読み込みの結果。失敗したときは <see cref="Document"/> が null で、理由は <see cref="Report"/> にある。</summary>
public sealed record VmdLoadResult(VmdDocument? Document, LoadReport Report, string? SourcePath)
{
    public bool Succeeded => Document is not null;
}

/// <summary>
/// VMD（MMD のモーション）を読む（指示書 4.1）。元のファイルは読むだけで書き換えない。
/// ボーン・モーフのキーの後ろ（カメラ・照明・影・表示と IK）は古いファイルには無いので、無ければ 0 個として扱う。
/// 形式の誤りで読めないときは例外ではなく、失敗した結果（エラー入りのレポート）を返す。
/// </summary>
public static class VmdReader
{
    private const string Signature2 = "Vocaloid Motion Data 0002";
    private const string Signature1 = "Vocaloid Motion Data file";
    private const int BoneKeyBytes = 15 + 4 + 12 + 16 + 64;
    private const int MorphKeyBytes = 15 + 4 + 4;
    private const int CameraKeyBytes = 4 + 4 + 12 + 12 + 24 + 4 + 1;
    private const int LightKeyBytes = 4 + 12 + 12;
    private const int ShadowKeyBytes = 4 + 1 + 4;

    public static VmdLoadResult Load(string path)
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
            return new VmdLoadResult(null, report, path);
        }
        return Read(data, path, report);
    }

    public static VmdLoadResult Read(byte[] data) => Read(data, null, new LoadReport());

    private static VmdLoadResult Read(byte[] data, string? path, LoadReport report)
    {
        try
        {
            return new VmdLoadResult(Parse(data, report), report, path);
        }
        catch (ModelFormatException ex)
        {
            report.Error(LoadIssueCategory.Format, ex.Message);
            return new VmdLoadResult(null, report, path);
        }
    }

    private static VmdDocument Parse(byte[] data, LoadReport report)
    {
        var c = new BinaryCursor(data);
        if (data.Length < 30)
        {
            throw new ModelFormatException("VMD ファイルではありません（ファイルが短すぎます）");
        }
        string signature = Encoding.ASCII.GetString(c.Take(30)).TrimEnd('\0', ' ');
        int version;
        if (signature.StartsWith(Signature2, StringComparison.Ordinal))
        {
            version = 2;
        }
        else if (signature.StartsWith(Signature1, StringComparison.Ordinal))
        {
            version = 1;
        }
        else if (signature.StartsWith("Vocaloid Pose Data", StringComparison.Ordinal))
        {
            throw new ModelFormatException("これはポーズのファイル（VPD）です。拡張子を .vpd にして読み込んでください");
        }
        else
        {
            throw new ModelFormatException("VMD ファイルではありません（ファイルの先頭の印が「Vocaloid Motion Data」ではありません）");
        }

        c.Section = "モデル名";
        VmdName modelName = VmdText.Decode(c.Take(version == 2 ? VmdText.ModelNameBytes : 10));

        c.Section = "ボーンのキー";
        int boneCount = c.ReadCount("ボーンのキー", BoneKeyBytes);
        var bones = new List<VmdBoneKey>(boneCount);
        for (int i = 0; i < boneCount; i++)
        {
            c.Section = $"ボーンのキー {i} 番";
            VmdName name = VmdText.Decode(c.Take(VmdText.BoneNameBytes));
            int frame = ReadFrame(c);
            var translation = c.ReadVector3();
            var rotation = new System.Numerics.Quaternion(c.ReadSingle(), c.ReadSingle(), c.ReadSingle(), c.ReadSingle());
            byte[] interpolation = c.Take(64).ToArray();
            bones.Add(new VmdBoneKey(name, frame, translation, rotation, interpolation));
        }

        var morphs = new List<VmdMorphKey>();
        if (TryReadCount(c, "モーフのキー", MorphKeyBytes, report, out int morphCount))
        {
            morphs.Capacity = morphCount;
            for (int i = 0; i < morphCount; i++)
            {
                c.Section = $"モーフのキー {i} 番";
                VmdName name = VmdText.Decode(c.Take(VmdText.MorphNameBytes));
                morphs.Add(new VmdMorphKey(name, ReadFrame(c), c.ReadSingle()));
            }
        }

        var cameras = new List<VmdCameraKey>();
        var lights = new List<VmdLightKey>();
        var shadows = new List<VmdShadowKey>();
        var visibility = new List<VmdVisibilityKey>();
        if (TryReadCount(c, "カメラのキー", CameraKeyBytes, report, out int cameraCount))
        {
            for (int i = 0; i < cameraCount; i++)
            {
                c.Section = $"カメラのキー {i} 番";
                int frame = ReadFrame(c);
                float distance = c.ReadSingle();
                var target = c.ReadVector3();
                var rotation = c.ReadVector3();
                byte[] interpolation = c.Take(24).ToArray();
                int fov = c.ReadInt32();
                bool orthographic = c.ReadByte() != 0;
                cameras.Add(new VmdCameraKey(frame, distance, target, rotation, interpolation, fov, orthographic));
            }
            if (TryReadCount(c, "照明のキー", LightKeyBytes, report, out int lightCount))
            {
                for (int i = 0; i < lightCount; i++)
                {
                    c.Section = $"照明のキー {i} 番";
                    lights.Add(new VmdLightKey(ReadFrame(c), c.ReadVector3(), c.ReadVector3()));
                }
                if (TryReadCount(c, "セルフ影のキー", ShadowKeyBytes, report, out int shadowCount))
                {
                    for (int i = 0; i < shadowCount; i++)
                    {
                        c.Section = $"セルフ影のキー {i} 番";
                        shadows.Add(new VmdShadowKey(ReadFrame(c), c.ReadByte(), c.ReadSingle()));
                    }
                    if (TryReadCount(c, "表示・IK のキー", 4 + 1 + 4, report, out int visibilityCount))
                    {
                        for (int i = 0; i < visibilityCount; i++)
                        {
                            c.Section = $"表示・IK のキー {i} 番";
                            int frame = ReadFrame(c);
                            bool visible = c.ReadByte() != 0;
                            int ikCount = c.ReadCount("IK の有効・無効", VmdText.IkNameBytes + 1);
                            var states = new VmdIkState[ikCount];
                            for (int k = 0; k < ikCount; k++)
                            {
                                states[k] = new VmdIkState(VmdText.Decode(c.Take(VmdText.IkNameBytes)), c.ReadByte() != 0);
                            }
                            visibility.Add(new VmdVisibilityKey(frame, visible, states));
                        }
                    }
                }
            }
        }
        if (c.Remaining > 0)
        {
            report.Info(LoadIssueCategory.Note, $"ファイルの最後に、読まなかったデータが {c.Remaining:N0} バイトあります（新しい版の MMD やほかのソフトが足したものかもしれません）");
        }

        return new VmdDocument
        {
            Version = version,
            ModelName = modelName,
            BoneKeys = bones,
            MorphKeys = morphs,
            CameraKeys = cameras,
            LightKeys = lights,
            ShadowKeys = shadows,
            VisibilityKeys = visibility,
        };
    }

    private static int ReadFrame(BinaryCursor c)
    {
        uint frame = c.ReadUInt32();
        if (frame > int.MaxValue)
        {
            throw new ModelFormatException($"{c.Section}のフレーム番号（{frame:N0}）が大きすぎます。ファイルが壊れている可能性があります");
        }
        return (int)frame;
    }

    // 後ろの区切りは古いファイルには無い。残りが無ければ 0 個、数の途中で切れていれば注意を出して 0 個にする
    private static bool TryReadCount(BinaryCursor c, string what, int minimumBytesPerItem, LoadReport report, out int count)
    {
        c.Section = what;
        count = 0;
        if (c.Remaining == 0)
        {
            return false;
        }
        if (c.Remaining < 4)
        {
            report.Warning(LoadIssueCategory.Format, $"{what}の数を読む途中でファイルが終わっています（残り {c.Remaining} バイト）。ここから後ろは無いものとして読みます");
            c.Skip(c.Remaining);
            return false;
        }
        count = c.ReadCount(what, minimumBytesPerItem);
        return true;
    }
}
