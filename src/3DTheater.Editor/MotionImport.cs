using System.Numerics;
using Theater3D.Domain;
using Theater3D.Formats;
using Theater3D.Formats.Vmd;

namespace Theater3D.Editor;

/// <summary>モーションを当てはめた結果の数。</summary>
public sealed record MotionImportResult(int BoneKeys, int MorphKeys, int Bones, int Morphs, int LastFrame);

/// <summary>ポーズを当てはめた結果の数。</summary>
public sealed record PoseImportResult(int Bones, int Morphs);

/// <summary>
/// 読み込んだ VMD・VPD を、シーンのモデルのボーン・モーフに当てはめる。元のファイルは書き換えない。
/// 名前の対応は MMD と同じく、Shift-JIS にして決まった長さ（ボーン・モーフは 15 バイト）で切ったバイト列で比べる。
/// モデルに無いボーン・モーフ、まだ使えないキー（カメラ・照明・IK の切り替えなど）はレポートで知らせる。
/// </summary>
public static class MotionImport
{
    /// <summary>MMD のモーションのフレームの速さ。</summary>
    public const int MmdFrameRate = 30;

    /// <summary>
    /// VMD のキーをモデルへ足す。同じボーン・モーフ・フレームにキーがあれば置き換え、ほかのキーは残す。
    /// 作品のフレームの速さが 30 でなければ、フレームの位置を合わせる（60 fps なら 2 倍）。
    /// </summary>
    public static MotionImportResult ApplyVmd(VmdDocument vmd, ModelNode model, int frameRate, LoadReport report)
    {
        double scale = frameRate / (double)MmdFrameRate;
        int Scale(int frame) => (int)Math.Round(frame * scale, MidpointRounding.AwayFromZero);
        string modelName = vmd.ModelName.Text.Trim();
        if (modelName.Length > 0 && !vmd.ModelName.Matches(model.Name, VmdText.ModelNameBytes))
        {
            report.Info(LoadIssueCategory.Note, $"このモーションは「{modelName}」用に作られたものです（当てはめたモデルは「{model.Name}」）。体の大きさや骨組みの違いで、見た目が変わることがあります");
        }

        // ボーン
        var bonesByName = new Dictionary<string, BoneNode>();
        foreach (BoneNode bone in model.Bones)
        {
            bonesByName.TryAdd(NameKey(VmdText.Encode(bone.Name, VmdText.BoneNameBytes)), bone);
        }
        var boneGroups = vmd.BoneKeys.GroupBy(k => NameKey(k.Bone.Bytes)).ToList();
        int boneKeyCount = 0, usedBones = 0, overlapped = 0;
        var missingBones = new List<string>();
        foreach (var group in boneGroups)
        {
            if (!bonesByName.TryGetValue(group.Key, out BoneNode? bone))
            {
                missingBones.Add(group.First().Bone.Text);
                continue;
            }
            var keys = new List<BoneKey>();
            var frames = new HashSet<int>();
            foreach (VmdBoneKey k in group.OrderBy(k => k.Frame))
            {
                int frame = Scale(k.Frame);
                if (!frames.Add(frame))
                {
                    overlapped++;
                }
                keys.Add(new BoneKey(frame, Finite(k.Translation), Normalize(k.Rotation), Interpolation(k)));
            }
            bone.Keys.SetRange(keys);
            boneKeyCount += keys.Count;
            usedBones++;
        }

        // モーフ
        var morphsByName = new Dictionary<string, MorphNode>();
        foreach (MorphNode morph in model.Morphs)
        {
            morphsByName.TryAdd(NameKey(VmdText.Encode(morph.Name, VmdText.MorphNameBytes)), morph);
        }
        int morphKeyCount = 0, usedMorphs = 0;
        var missingMorphs = new List<string>();
        foreach (var group in vmd.MorphKeys.GroupBy(k => NameKey(k.Morph.Bytes)))
        {
            if (!morphsByName.TryGetValue(group.Key, out MorphNode? morph))
            {
                missingMorphs.Add(group.First().Morph.Text);
                continue;
            }
            var keys = new List<MorphKey>();
            var frames = new HashSet<int>();
            foreach (VmdMorphKey k in group.OrderBy(k => k.Frame))
            {
                int frame = Scale(k.Frame);
                if (!frames.Add(frame))
                {
                    overlapped++;
                }
                keys.Add(new MorphKey(frame, float.IsFinite(k.Weight) ? k.Weight : 0f, BezierCurve.Linear));
            }
            morph.Keys.SetRange(keys);
            morphKeyCount += keys.Count;
            usedMorphs++;
        }

        // 知らせること
        int motionBones = boneGroups.Count;
        LoadSeverity missingSeverity = motionBones > 0 && missingBones.Count * 2 > motionBones ? LoadSeverity.Warning : LoadSeverity.Info;
        report.AddAggregated(missingSeverity, LoadIssueCategory.InvalidReference,
            missingSeverity == LoadSeverity.Warning ? "モデルに無いボーン（半分以上が合いません。別のモデル用のモーションかもしれません）" : "モデルに無いため使わないボーン",
            missingBones, missingBones.Count);
        report.AddAggregated(LoadSeverity.Info, LoadIssueCategory.InvalidReference, "モデルに無いため使わないモーフ", missingMorphs, missingMorphs.Count);
        if (vmd.CameraKeys.Count > 0 || vmd.LightKeys.Count > 0 || vmd.ShadowKeys.Count > 0)
        {
            report.Info(LoadIssueCategory.Note,
                $"カメラ {vmd.CameraKeys.Count:N0}・照明 {vmd.LightKeys.Count:N0}・セルフ影 {vmd.ShadowKeys.Count:N0} 個のキーは、モデルには当てはめません（カメラのキーは、カメラを選んで読み込むか、カメラ用のモーションを読み込むと当てはまります）");
        }
        int ikOff = vmd.VisibilityKeys.Sum(k => k.IkStates.Count(s => !s.Enabled));
        int hidden = vmd.VisibilityKeys.Count(k => !k.Visible);
        if (ikOff > 0)
        {
            report.Warning(LoadIssueCategory.Unsupported, $"IK を切るキー（{ikOff:N0} 件）には、まだ対応していません。IK は常に効いたままです");
        }
        if (hidden > 0)
        {
            report.Info(LoadIssueCategory.Unsupported, $"モデルを隠すキー（{hidden:N0} 件）は、今は使いません");
        }
        if (scale != 1)
        {
            report.Info(LoadIssueCategory.Note, $"作品のフレームの速さ（{frameRate} fps）に合わせて、キーの位置を {scale:0.##} 倍にしました（MMD のモーションは 30 fps）");
        }
        if (overlapped > 0)
        {
            report.Info(LoadIssueCategory.Note, $"同じフレームに重なったキーが {overlapped:N0} 件あったため、後ろのキーを使いました");
        }
        if (boneKeyCount == 0 && morphKeyCount == 0)
        {
            report.Warning(LoadIssueCategory.InvalidReference, vmd.IsCameraMotion
                ? "このファイルはカメラ・照明のモーションです。モデルに当てはめるキーはありません"
                : "モデルに当てはめられるキーがありませんでした（ボーン・モーフの名前が 1 つも合いません）");
        }

        int lastFrame = Math.Max(
            model.Bones.Select(b => b.Keys.LastFrame).DefaultIfEmpty(0).Max(),
            model.Morphs.Select(m => m.Keys.LastFrame).DefaultIfEmpty(0).Max());
        return new MotionImportResult(boneKeyCount, morphKeyCount, usedBones, usedMorphs, lastFrame);
    }

    /// <summary>
    /// VPD のポーズを、モデルの今の値として置く（キーにはしない。自動キーが OFF なら未登録の変更になる）。
    /// ポーズに無いボーン・モーフは変えない。
    /// </summary>
    public static PoseImportResult ApplyVpd(VpdDocument vpd, ModelNode model, LoadReport report)
    {
        var bones = model.Bones.GroupBy(b => b.Name).ToDictionary(g => g.Key, g => g.First());
        var morphs = model.Morphs.GroupBy(m => m.Name).ToDictionary(g => g.Key, g => g.First());
        int usedBones = 0, usedMorphs = 0, locked = 0;
        var missingBones = new List<string>();
        var missingMorphs = new List<string>();
        foreach (VpdBone b in vpd.Bones)
        {
            if (!bones.TryGetValue(b.Name, out BoneNode? bone))
            {
                missingBones.Add(b.Name);
                continue;
            }
            if (!bone.CanEdit)
            {
                locked++;
                continue;
            }
            bone.Position = Finite(b.Translation);
            bone.Orientation = Normalize(b.Rotation);
            usedBones++;
        }
        foreach (VpdMorph m in vpd.Morphs)
        {
            if (!morphs.TryGetValue(m.Name, out MorphNode? morph))
            {
                missingMorphs.Add(m.Name);
                continue;
            }
            if (!morph.CanEdit)
            {
                locked++;
                continue;
            }
            morph.Weight = m.Weight;
            usedMorphs++;
        }
        report.AddAggregated(LoadSeverity.Info, LoadIssueCategory.InvalidReference, "モデルに無いため使わないボーン", missingBones, missingBones.Count);
        report.AddAggregated(LoadSeverity.Info, LoadIssueCategory.InvalidReference, "モデルに無いため使わないモーフ", missingMorphs, missingMorphs.Count);
        if (locked > 0)
        {
            report.Info(LoadIssueCategory.Note, $"ロックしているため変えなかった項目が {locked:N0} 個あります");
        }
        if (usedBones == 0 && usedMorphs == 0 && (vpd.Bones.Count > 0 || vpd.Morphs.Count > 0))
        {
            report.Warning(LoadIssueCategory.InvalidReference, "モデルに当てはめられるボーン・モーフがありませんでした（名前が 1 つも合いません）");
        }
        return new PoseImportResult(usedBones, usedMorphs);
    }

    private static BoneInterpolation Interpolation(VmdBoneKey key)
    {
        static BezierCurve Curve((byte X1, byte Y1, byte X2, byte Y2) c) => BezierCurve.FromMmd(c.X1, c.Y1, c.X2, c.Y2);
        return new BoneInterpolation(Curve(key.Curve(0)), Curve(key.Curve(1)), Curve(key.Curve(2)), Curve(key.Curve(3)));
    }

    private static Quaternion Normalize(Quaternion q) =>
        float.IsFinite(q.X) && float.IsFinite(q.Y) && float.IsFinite(q.Z) && float.IsFinite(q.W) && q.LengthSquared() > 1e-12f
            ? Quaternion.Normalize(q)
            : Quaternion.Identity;

    private static Vector3 Finite(Vector3 v) => float.IsFinite(v.X) && float.IsFinite(v.Y) && float.IsFinite(v.Z) ? v : Vector3.Zero;

    private static string NameKey(ReadOnlySpan<byte> bytes) => Convert.ToHexString(bytes);
}
