using System.IO;
using System.Text.RegularExpressions;

namespace Theater3D.Formats.Pmx;

/// <summary>
/// 読み込んだ PMX を点検し、範囲外の参照・見つからない素材・未対応の要素をレポートに載せる（指示書 4.1）。
/// データは直さない（元の情報を保つため）。範囲外の参照は、使う側（描画・変形）で無視する前提。
/// 同じ種類の問題はまとめて 1 行にし、件数と最初のいくつかの例だけを載せる。
/// </summary>
internal static partial class PmxValidator
{
    private const int MaxListedMissingFiles = 10;

    public static void Validate(PmxDocument document, LoadReport report, string? baseDirectory)
    {
        CheckVertices(document, report);
        CheckFaces(document, report);
        CheckMaterials(document, report, baseDirectory);
        CheckBones(document, report);
        CheckMorphs(document, report);
        CheckDisplayFrames(document, report);
        CheckPhysics(document, report);
        CheckVersion21Features(document, report);
    }

    private static void CheckVertices(PmxDocument d, LoadReport report)
    {
        int boneCount = d.Bones.Length;
        var invalid = new Tally("頂点が存在しないボーンを指しています（その頂点は正しく動きません）", LoadSeverity.Warning, LoadIssueCategory.InvalidReference);
        for (int i = 0; i < d.Vertices.Length; i++)
        {
            ref readonly PmxVertex v = ref d.Vertices[i];
            bool bad = v.WeightType switch
            {
                PmxWeightType.Bdef1 => !IsValid(v.Bone1, boneCount),
                PmxWeightType.Bdef2 or PmxWeightType.Sdef => Bad(v.Bone1, v.Weights.X) || Bad(v.Bone2, v.Weights.Y),
                _ => Bad(v.Bone1, v.Weights.X) || Bad(v.Bone2, v.Weights.Y) || Bad(v.Bone3, v.Weights.Z) || Bad(v.Bone4, v.Weights.W),
            };
            if (bad)
            {
                invalid.Add($"頂点 {i}");
            }
        }
        invalid.Flush(report);

        // 重みが 0 の「なし（-1）」は問題にしない
        bool Bad(int bone, float weight) => bone == -1 ? weight > 0f : !IsValid(bone, boneCount);
    }

    private static void CheckFaces(PmxDocument d, LoadReport report)
    {
        if (d.Indices.Length % 3 != 0)
        {
            report.Warning(LoadIssueCategory.Format, $"面の頂点番号の数（{d.Indices.Length:N0}）が 3 の倍数ではありません（余りの {d.Indices.Length % 3} 個は使いません）");
        }
        var invalid = new Tally("面が存在しない頂点を指しています（その面は表示しません）", LoadSeverity.Warning, LoadIssueCategory.InvalidReference);
        for (int i = 0; i + 2 < d.Indices.Length; i += 3)
        {
            if (!IsValid(d.Indices[i], d.Vertices.Length) || !IsValid(d.Indices[i + 1], d.Vertices.Length) || !IsValid(d.Indices[i + 2], d.Vertices.Length))
            {
                invalid.Add($"面 {i / 3}");
            }
        }
        invalid.Flush(report);
    }

    private static void CheckMaterials(PmxDocument d, LoadReport report, string? baseDirectory)
    {
        long total = 0;
        var badTexture = new Tally("材質が存在しないテクスチャを指しています（テクスチャなしで表示します）", LoadSeverity.Warning, LoadIssueCategory.InvalidReference);
        var newDrawModes = new Tally("材質の頂点色・点・線での描画（PMX 2.1）には未対応です", LoadSeverity.Warning, LoadIssueCategory.Unsupported);
        var toonTextures = new HashSet<int>();
        var usedTextures = new SortedSet<int>();

        foreach (PmxMaterial m in d.Materials)
        {
            total += m.IndexCount;
            if (m.IndexCount < 0 || m.IndexCount % 3 != 0)
            {
                report.Warning(LoadIssueCategory.Format, $"材質「{m.Name}」の面の数（頂点番号 {m.IndexCount:N0} 個）が 3 の倍数ではありません");
            }
            CheckTexture(m.TextureIndex, m, "テクスチャ");
            CheckTexture(m.SphereTextureIndex, m, "スフィア");
            if (m.SharedToon)
            {
                if (m.ToonIndex is < 0 or > 9)
                {
                    report.Warning(LoadIssueCategory.InvalidReference, $"材質「{m.Name}」の共有トゥーンの番号（{m.ToonIndex}）が範囲外です（0〜9）");
                }
            }
            else if (CheckTexture(m.ToonIndex, m, "トゥーン"))
            {
                toonTextures.Add(m.ToonIndex);
            }
            if ((byte)m.SphereMode > 3)
            {
                report.Warning(LoadIssueCategory.Format, $"材質「{m.Name}」のスフィアの合成方法（{(byte)m.SphereMode}）が正しくありません（無しとして扱います）");
            }
            if ((m.Flags & (PmxMaterialFlags.VertexColor | PmxMaterialFlags.PointDraw | PmxMaterialFlags.LineDraw)) != 0)
            {
                newDrawModes.Add($"「{m.Name}」");
            }
        }
        if (total != d.Indices.Length)
        {
            report.Warning(LoadIssueCategory.Format,
                $"材質が使う面の頂点番号の合計（{total:N0}）と、面の頂点番号の数（{d.Indices.Length:N0}）が合いません（足りない部分は表示しません）");
        }
        badTexture.Flush(report);
        newDrawModes.Flush(report);

        if (baseDirectory is not null)
        {
            CheckTextureFiles(d, report, baseDirectory, usedTextures, toonTextures);
        }

        bool CheckTexture(int index, PmxMaterial m, string role)
        {
            if (index == -1)
            {
                return false;
            }
            if (!IsValid(index, d.Textures.Length))
            {
                badTexture.Add($"「{m.Name}」の{role}");
                return false;
            }
            usedTextures.Add(index);
            return true;
        }
    }

    private static void CheckTextureFiles(PmxDocument d, LoadReport report, string baseDirectory, IEnumerable<int> used, HashSet<int> toonTextures)
    {
        var missing = new List<string>();
        foreach (int index in used)
        {
            if (d.EmbeddedTextures.ContainsKey(index))
            {
                continue; // ファイルの中に入っている画像（VRM・GLB）は、探さない
            }
            string relative = d.Textures[index];
            string? fullPath = ResolvePath(baseDirectory, relative);
            if (fullPath is not null && File.Exists(fullPath))
            {
                continue;
            }
            if (toonTextures.Contains(index) && SharedToonName().IsMatch(Path.GetFileName(relative.Replace('\\', '/'))))
            {
                // MMD は toon01.bmp〜toon10.bmp が手元に無ければ、付属の共有トゥーンを使う
                report.Info(LoadIssueCategory.Note, $"トゥーン「{relative}」がモデルの場所に無いため、MMD 付属の共有トゥーンとして扱います");
                continue;
            }
            missing.Add(relative);
        }

        foreach (string file in missing.Take(MaxListedMissingFiles))
        {
            report.Warning(LoadIssueCategory.MissingFile, $"テクスチャ「{file}」が見つかりません（そのテクスチャなしで表示します）");
        }
        if (missing.Count > MaxListedMissingFiles)
        {
            report.Warning(LoadIssueCategory.MissingFile, $"ほかにも見つからないテクスチャが {missing.Count - MaxListedMissingFiles} 個あります");
        }
    }

    internal static string? ResolvePath(string baseDirectory, string relative) => PmxPaths.Resolve(baseDirectory, relative);

    private static void CheckBones(PmxDocument d, LoadReport report)
    {
        int count = d.Bones.Length;
        var badParent = new Tally("ボーンの親が存在しないか自分自身です（親なしとして扱います）", LoadSeverity.Warning, LoadIssueCategory.InvalidReference);
        var badTail = new Tally("ボーンの先端が存在しないボーンを指しています（表示だけに影響します）", LoadSeverity.Info, LoadIssueCategory.InvalidReference);
        var badInherit = new Tally("回転・移動の付与元が存在しないボーンです（付与なしとして扱います）", LoadSeverity.Warning, LoadIssueCategory.InvalidReference);
        var badIk = new Tally("IK の目標やリンクが存在しないボーンを指しています（その IK は働きません）", LoadSeverity.Warning, LoadIssueCategory.InvalidReference);
        var externalParent = new Tally("外部親変形のボーンには未対応です（通常のボーンとして扱います）", LoadSeverity.Warning, LoadIssueCategory.Unsupported);

        for (int i = 0; i < count; i++)
        {
            PmxBone b = d.Bones[i];
            if (b.ParentIndex != -1 && (!IsValid(b.ParentIndex, count) || b.ParentIndex == i))
            {
                badParent.Add($"「{b.Name}」");
            }
            if (b.Has(PmxBoneFlags.TailIsBone) && b.TailBone != -1 && !IsValid(b.TailBone, count))
            {
                badTail.Add($"「{b.Name}」");
            }
            if ((b.Flags & (PmxBoneFlags.InheritRotation | PmxBoneFlags.InheritTranslation)) != 0
                && (!IsValid(b.InheritParent, count) || b.InheritParent == i))
            {
                badInherit.Add($"「{b.Name}」");
            }
            if (b.Has(PmxBoneFlags.IK) && (!IsValid(b.IkTarget, count) || b.IkLinks.Any(l => !IsValid(l.Bone, count))))
            {
                badIk.Add($"「{b.Name}」");
            }
            if (b.Has(PmxBoneFlags.ExternalParent))
            {
                externalParent.Add($"「{b.Name}」");
            }
        }
        badParent.Flush(report);
        badTail.Flush(report);
        badInherit.Flush(report);
        badIk.Flush(report);
        externalParent.Flush(report);

        // 親をたどって元に戻ってくると、変形の計算が終わらなくなる
        var cycle = new Tally("ボーンの親子関係が輪になっています（そのボーンは親なしとして扱います）", LoadSeverity.Warning, LoadIssueCategory.InvalidReference);
        for (int i = 0; i < count; i++)
        {
            int current = d.Bones[i].ParentIndex;
            for (int steps = 0; IsValid(current, count) && steps <= count; steps++)
            {
                if (current == i)
                {
                    cycle.Add($"「{d.Bones[i].Name}」");
                    break;
                }
                current = d.Bones[current].ParentIndex;
            }
        }
        cycle.Flush(report);

        ReportDuplicateNames(report, d.Bones.Select(b => b.Name), "ボーン");
    }

    private static void CheckMorphs(PmxDocument d, LoadReport report)
    {
        var badRef = new Tally("モーフが存在しない頂点・ボーン・材質などを指しています（その部分は効きません）", LoadSeverity.Warning, LoadIssueCategory.InvalidReference);
        var nestedGroup = new Tally("グループモーフの中にグループモーフか自分自身があります（MMD と同じく、その部分は効きません）", LoadSeverity.Warning, LoadIssueCategory.Format);
        var newTypes = new Tally("PMX 2.1 のフリップ・インパルスモーフがあります（MMD では効きませんが、このアプリでは効きます。インパルスは物理演算が ON のとき）",
            LoadSeverity.Info, LoadIssueCategory.Note);
        var missingUvChannel = new Tally("追加 UV モーフが、モデルに無い追加 UV を指しています（効きません）", LoadSeverity.Warning, LoadIssueCategory.Format);

        for (int i = 0; i < d.Morphs.Length; i++)
        {
            PmxMorph m = d.Morphs[i];
            string label = $"「{m.Name}」";
            bool bad = m.Type switch
            {
                PmxMorphType.Group or PmxMorphType.Flip => m.GroupOffsets.Any(o => !IsValid(o.Morph, d.Morphs.Length)),
                PmxMorphType.Vertex => m.VertexOffsets.Any(o => !IsValid(o.Vertex, d.Vertices.Length)),
                PmxMorphType.Bone => m.BoneOffsets.Any(o => !IsValid(o.Bone, d.Bones.Length)),
                PmxMorphType.Material => m.MaterialOffsets.Any(o => o.Material != -1 && !IsValid(o.Material, d.Materials.Length)),
                PmxMorphType.Impulse => m.ImpulseOffsets.Any(o => !IsValid(o.RigidBody, d.RigidBodies.Length)),
                _ => m.UvOffsets.Any(o => !IsValid(o.Vertex, d.Vertices.Length)),
            };
            if (bad)
            {
                badRef.Add(label);
            }
            if (m.Type == PmxMorphType.Group
                && m.GroupOffsets.Any(o => o.Morph == i || (IsValid(o.Morph, d.Morphs.Length) && d.Morphs[o.Morph].Type == PmxMorphType.Group)))
            {
                nestedGroup.Add(label);
            }
            if (m.Type is PmxMorphType.Flip or PmxMorphType.Impulse)
            {
                newTypes.Add(label);
            }
            if (m.Type is >= PmxMorphType.AdditionalUv1 and <= PmxMorphType.AdditionalUv4
                && (m.Type - PmxMorphType.AdditionalUv1 + 1) > d.AdditionalUvCount)
            {
                missingUvChannel.Add(label);
            }
        }
        badRef.Flush(report);
        nestedGroup.Flush(report);
        newTypes.Flush(report);
        missingUvChannel.Flush(report);

        ReportDuplicateNames(report, d.Morphs.Select(m => m.Name), "モーフ");
    }

    private static void CheckDisplayFrames(PmxDocument d, LoadReport report)
    {
        var badRef = new Tally("表示枠が存在しないボーン・モーフを指しています（一覧に出しません）", LoadSeverity.Info, LoadIssueCategory.InvalidReference);
        foreach (PmxDisplayFrame frame in d.DisplayFrames)
        {
            if (frame.Elements.Any(e => !IsValid(e.Index, e.Target == PmxDisplayTarget.Bone ? d.Bones.Length : d.Morphs.Length)))
            {
                badRef.Add($"「{frame.Name}」");
            }
        }
        badRef.Flush(report);
    }

    private static void CheckPhysics(PmxDocument d, LoadReport report)
    {
        var badBone = new Tally("剛体の関連ボーンが存在しません", LoadSeverity.Warning, LoadIssueCategory.InvalidReference);
        var badShape = new Tally("剛体の形・演算方法の値が正しくありません", LoadSeverity.Warning, LoadIssueCategory.Format);
        foreach (PmxRigidBody r in d.RigidBodies)
        {
            if (r.BoneIndex != -1 && !IsValid(r.BoneIndex, d.Bones.Length))
            {
                badBone.Add($"「{r.Name}」");
            }
            if ((byte)r.Shape > 2 || (byte)r.Mode > 2)
            {
                badShape.Add($"「{r.Name}」");
            }
        }
        badBone.Flush(report);
        badShape.Flush(report);

        var badJoint = new Tally("ジョイントが存在しない剛体をつないでいます（そのジョイントは働きません）", LoadSeverity.Warning, LoadIssueCategory.InvalidReference);
        var newJointTypes = new Tally("ジョイントの種類（PMX 2.1 の 6DOF・ヒンジなど）には未対応です", LoadSeverity.Warning, LoadIssueCategory.Unsupported);
        foreach (PmxJoint j in d.Joints)
        {
            if (!IsValid(j.RigidBodyA, d.RigidBodies.Length) || !IsValid(j.RigidBodyB, d.RigidBodies.Length))
            {
                badJoint.Add($"「{j.Name}」");
            }
            if (j.Type != PmxJointType.Spring6Dof)
            {
                newJointTypes.Add($"「{j.Name}」");
            }
        }
        badJoint.Flush(report);
        newJointTypes.Flush(report);
    }

    private static void CheckVersion21Features(PmxDocument d, LoadReport report)
    {
        int qdef = d.Vertices.Count(v => v.WeightType == PmxWeightType.Qdef);
        if (qdef > 0)
        {
            report.Warning(LoadIssueCategory.Unsupported, $"QDEF 変形（PMX 2.1）の頂点が {qdef:N0} 個あります。読み込むだけで、QDEF としての変形には未対応です");
        }
        if (d.SoftBodyCount > 0)
        {
            report.Warning(LoadIssueCategory.Unsupported, $"ソフトボディ（PMX 2.1）が {d.SoftBodyCount:N0} 個ありますが、未対応のため読み込んでいません");
        }
    }

    // モーションはボーン・モーフを名前で当てはめるので、同じ名前があると区別できない
    private static void ReportDuplicateNames(LoadReport report, IEnumerable<string> names, string what)
    {
        string[] duplicates = names.GroupBy(n => n).Where(g => g.Count() > 1 && g.Key.Length > 0).Select(g => $"「{g.Key}」").ToArray();
        report.AddAggregated(LoadSeverity.Warning, LoadIssueCategory.Format,
            $"同じ名前の{what}があります（モーションの当てはめで区別できません）", duplicates, duplicates.Length);
    }

    private static bool IsValid(int index, int count) => index >= 0 && index < count;

    [GeneratedRegex(@"^toon(0[1-9]|10)\.bmp$", RegexOptions.IgnoreCase)]
    private static partial Regex SharedToonName();

    /// <summary>同じ種類の問題を数え、例をいくつか覚えておく。</summary>
    private sealed class Tally(string what, LoadSeverity severity, LoadIssueCategory category)
    {
        private readonly List<string> _examples = [];
        private int _count;

        public void Add(string example)
        {
            _count++;
            if (_examples.Count < 3)
            {
                _examples.Add(example);
            }
        }

        public void Flush(LoadReport report) => report.AddAggregated(severity, category, what, _examples, _count);
    }
}
