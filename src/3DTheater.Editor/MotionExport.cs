using System.Numerics;
using Theater3D.Domain;
using Theater3D.Formats;
using Theater3D.Formats.Vmd;

namespace Theater3D.Editor;

/// <summary>書き出した結果の数。</summary>
public sealed record MotionExportResult(int BoneKeys, int MorphKeys, int Bones, int Morphs);

/// <summary>
/// モデルのキーを VMD に、今の姿勢を VPD にする。VMD で表せないもの（段階補間・モーフの曲線・長い名前）は、
/// できるだけ近い形にしてレポートで知らせる。
/// </summary>
public static class MotionExport
{
    /// <summary>
    /// モデルのボーン・モーフのキーを VMD にする。作品のフレームの速さが 30 でなければ、30 fps に直す。
    /// 段階補間のキーは、1 つ前のフレームに前の値のキーを足して表す（VMD には段階が無いため）。
    /// </summary>
    public static VmdDocument CreateVmd(ModelNode model, int frameRate, LoadReport report)
    {
        double scale = MotionImport.MmdFrameRate / (double)frameRate;
        int Scale(int frame) => (int)Math.Round(frame * scale, MidpointRounding.AwayFromZero);
        var boneKeys = new List<VmdBoneKey>();
        var morphKeys = new List<VmdMorphKey>();
        int bones = 0, morphs = 0, steps = 0, morphCurves = 0, overlapped = 0;
        var longNames = new List<string>();

        foreach (BoneNode bone in model.Bones.Where(b => b.Keys.Count > 0))
        {
            bones++;
            VmdName name = VmdName.FromText(bone.Name, VmdText.BoneNameBytes);
            CheckLength(bone.Name, VmdText.BoneNameBytes, longNames);
            var written = new Dictionary<int, VmdBoneKey>();
            BoneKey? previous = null;
            foreach (BoneKey key in bone.Keys.Keys)
            {
                int frame = Scale(key.Frame);
                if (key.Kind == KeyInterpolationKind.Step && previous is { } p && frame - 1 > Scale(p.Frame))
                {
                    // 段階: 1 つ前のフレームまで前の値を保ち、このフレームで切り替える
                    written[frame - 1] = new VmdBoneKey(name, frame - 1, p.Translation, p.Rotation, LinearInterpolation);
                    steps++;
                }
                if (!written.TryAdd(frame, Bone(name, frame, key)))
                {
                    written[frame] = Bone(name, frame, key);
                    overlapped++;
                }
                previous = key;
            }
            boneKeys.AddRange(written.Values.OrderBy(k => k.Frame));
        }

        foreach (MorphNode morph in model.Morphs.Where(m => m.Keys.Count > 0))
        {
            morphs++;
            VmdName name = VmdName.FromText(morph.Name, VmdText.MorphNameBytes);
            CheckLength(morph.Name, VmdText.MorphNameBytes, longNames);
            var written = new Dictionary<int, VmdMorphKey>();
            MorphKey? previous = null;
            foreach (MorphKey key in morph.Keys.Keys)
            {
                int frame = Scale(key.Frame);
                if (key.Kind == KeyInterpolationKind.Step && previous is { } p && frame - 1 > Scale(p.Frame))
                {
                    written[frame - 1] = new VmdMorphKey(name, frame - 1, p.Weight);
                    steps++;
                }
                else if (key.Kind == KeyInterpolationKind.Curve && !key.Curve.IsLinear)
                {
                    morphCurves++;
                }
                if (!written.TryAdd(frame, new VmdMorphKey(name, frame, key.Weight)))
                {
                    written[frame] = new VmdMorphKey(name, frame, key.Weight);
                    overlapped++;
                }
                previous = key;
            }
            morphKeys.AddRange(written.Values.OrderBy(k => k.Frame));
        }

        if (scale != 1)
        {
            report.Info(LoadIssueCategory.Note, $"作品のフレームの速さ（{frameRate} fps）から、MMD の 30 fps に直してキーの位置を {scale:0.##} 倍にしました");
        }
        if (overlapped > 0)
        {
            report.Warning(LoadIssueCategory.Note, $"30 fps に直したときに同じフレームに重なったキーが {overlapped:N0} 件あり、後ろのキーを使いました");
        }
        if (steps > 0)
        {
            report.Info(LoadIssueCategory.Note, $"段階の補間（{steps:N0} 件）は VMD に無いため、1 つ前のフレームにキーを足して表しました");
        }
        if (morphCurves > 0)
        {
            report.Info(LoadIssueCategory.Note, $"モーフのキーの曲線（{morphCurves:N0} 件）は VMD に無いため、線形になります");
        }
        report.AddAggregated(LoadSeverity.Info, LoadIssueCategory.Note,
            "名前が VMD の長さ（15 バイト）に入らず、MMD と同じく途中で切って書いたボーン・モーフ",
            longNames, longNames.Count);
        int unregistered = model.Bones.Count(b => b.HasUnregisteredChange) + model.Morphs.Count(m => m.HasUnregisteredChange);
        if (unregistered > 0)
        {
            report.Warning(LoadIssueCategory.Note, $"キーに登録していない変更が {unregistered:N0} 件あり、書き出していません（書き出すには先に登録してください）");
        }
        if (boneKeys.Count == 0 && morphKeys.Count == 0)
        {
            report.Warning(LoadIssueCategory.Note, "書き出すキーがありません（空のモーションになります）");
        }

        return new VmdDocument
        {
            ModelName = VmdName.FromText(model.Name, VmdText.ModelNameBytes),
            BoneKeys = boneKeys,
            MorphKeys = morphKeys,
        };
    }

    /// <summary>今の姿勢を VPD にする。動かしているボーンと、値が 0 でないモーフを書く。</summary>
    public static VpdDocument CreateVpd(ModelNode model, string modelFileName)
    {
        VpdBone[] bones = [.. model.Bones.Where(b => b.IsPosed).Select(b => new VpdBone(b.Name, b.Position, b.Orientation))];
        VpdMorph[] morphs = [.. model.Morphs.Where(m => m.Weight != 0).Select(m => new VpdMorph(m.Name, m.Weight))];
        return new VpdDocument { ModelFileName = modelFileName, Bones = bones, Morphs = morphs };
    }

    private static readonly byte[] LinearInterpolation =
        VmdWriter.Interpolation((20, 20, 107, 107), (20, 20, 107, 107), (20, 20, 107, 107), (20, 20, 107, 107));

    private static VmdBoneKey Bone(VmdName name, int frame, BoneKey key)
    {
        BoneInterpolation ip = key.Kind == KeyInterpolationKind.Step ? BoneInterpolation.Linear : key.Interpolation;
        return new VmdBoneKey(name, frame, key.Translation, Quaternion.Normalize(key.Rotation),
            VmdWriter.Interpolation(Mmd(ip.X), Mmd(ip.Y), Mmd(ip.Z), Mmd(ip.Rotation)));
    }

    private static (byte, byte, byte, byte) Mmd(BezierCurve c) =>
        (ToByte(c.Control1.X), ToByte(c.Control1.Y), ToByte(c.Control2.X), ToByte(c.Control2.Y));

    private static byte ToByte(float v) => (byte)Math.Clamp((int)MathF.Round(v * 127f), 0, 127);

    private static void CheckLength(string name, int maxBytes, List<string> longNames)
    {
        if (VmdText.ShiftJis.GetByteCount(name) > maxBytes)
        {
            longNames.Add(name);
        }
    }
}
