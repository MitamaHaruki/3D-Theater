using System.IO;
using System.Numerics;
using System.Text.Json;
using System.Text.Json.Serialization;
using Theater3D.Domain;
using Theater3D.Formats;

namespace Theater3D.Editor;

// プロジェクトファイル（.3dtproj）の中身。UTF-8 の JSON で、先頭に形式の名前と版を持つ（指示書 8 章）。
// モデルは場所だけを持ち（中身は元のファイルから読む。原本は書き換えない）、ボーン・モーフは
// 初期の状態から変えたもの（キー・姿勢・未登録の変更・ロック・非表示）だけを書く。
// キーは数の並びで短く書く（ボーン: フレーム・移動 3・回転 4・種類、曲線が直線でなければ 16 個足す）。

public sealed class ProjectFile
{
    public string Format { get; set; } = ProjectSerializer.FormatId;

    public int Version { get; set; } = ProjectSerializer.CurrentVersion;

    /// <summary>保存したアプリの版（参考）。</summary>
    public string? App { get; set; }

    public DateTime SavedAt { get; set; }

    /// <summary>自動保存のときだけ: 元のプロジェクトの場所（無題なら null）。</summary>
    public string? AutosaveOf { get; set; }

    public PlaybackEntry Playback { get; set; } = new();

    public bool AutoKey { get; set; }

    /// <summary>物理演算を使うか（無い古いファイルは使う）。</summary>
    public bool Physics { get; set; } = true;

    /// <summary>カメラ・照明などの決まった項目（キーは <see cref="SceneDocument.FixedNodes"/> の役目の名前）。</summary>
    public Dictionary<string, NodeEntry> Nodes { get; set; } = [];

    public List<ModelEntry> Models { get; set; } = [];
}

public sealed class PlaybackEntry
{
    public int FrameRate { get; set; } = 30;

    public int EndFrame { get; set; } = 300;

    public int CurrentFrame { get; set; }

    public bool Loop { get; set; } = true;
}

public sealed class NodeEntry
{
    public string? Name { get; set; }

    public bool Visible { get; set; } = true;

    public bool Locked { get; set; }

    public float[]? Position { get; set; }

    public float[]? Rotation { get; set; }

    public float[]? Scale { get; set; }

    public float? FieldOfView { get; set; }

    public float? NearClip { get; set; }

    public float? FarClip { get; set; }

    public float? Intensity { get; set; }

    public bool? SpinWithFrame { get; set; }

    /// <summary>カメラの注視点までの距離。</summary>
    public float? TargetDistance { get; set; }

    /// <summary>カメラの未登録の変更。</summary>
    public bool Unregistered { get; set; }

    /// <summary>カメラのキー（フレーム・注視点 3・回転 3・距離・視野角・種類・平行投影、曲線が直線でなければ 24 個足す）。</summary>
    public float[][]? Keys { get; set; }
}

public sealed class ModelEntry
{
    /// <summary>モデルのファイル。プロジェクトのフォルダーから見た場所（ドライブが違えば絶対パス）。</summary>
    public string File { get; set; } = string.Empty;

    /// <summary>保存したときの絶対パス（フォルダーごと動かしていないときの予備）。</summary>
    public string? AbsoluteFile { get; set; }

    public string Name { get; set; } = string.Empty;

    public bool Visible { get; set; } = true;

    public bool Locked { get; set; }

    public float[] Position { get; set; } = [0, 0, 0];

    public float[] Rotation { get; set; } = [0, 0, 0];

    public float[] Scale { get; set; } = [1, 1, 1];

    /// <summary>モデルの中のまとまり（「ボーン/腕」など）の表示とロック。初期のままなら書かない。</summary>
    public List<GroupEntry>? Groups { get; set; }

    public List<BoneEntry>? Bones { get; set; }

    public List<MorphEntry>? Morphs { get; set; }
}

public sealed class GroupEntry
{
    public string Path { get; set; } = string.Empty;

    public bool Visible { get; set; } = true;

    public bool Locked { get; set; }
}

public sealed class BoneEntry
{
    public string Name { get; set; } = string.Empty;

    public int Index { get; set; }

    public float[]? Position { get; set; }

    /// <summary>回転（四元数 x・y・z・w）。</summary>
    public float[]? Rotation { get; set; }

    public bool Visible { get; set; } = true;

    public bool Locked { get; set; }

    public bool Unregistered { get; set; }

    public float[][]? Keys { get; set; }
}

public sealed class MorphEntry
{
    public string Name { get; set; } = string.Empty;

    public int Index { get; set; }

    public float Weight { get; set; }

    public bool Locked { get; set; }

    public bool Unregistered { get; set; }

    public float[][]? Keys { get; set; }
}

/// <summary>プロジェクトファイルの読み書き（JSON）。</summary>
public static class ProjectSerializer
{
    public const string FormatId = "3DTheater.Project";
    public const int CurrentVersion = 1;
    public const string Extension = ".3dtproj";

    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = false,
    };

    public static string Serialize(ProjectFile project) => JsonSerializer.Serialize(project, Options);

    /// <summary>読む。形式が違う・新しい版で保存された・壊れているときは null を返し、理由をレポートに載せる。</summary>
    public static ProjectFile? Deserialize(string json, LoadReport report)
    {
        ProjectFile? project;
        try
        {
            project = JsonSerializer.Deserialize<ProjectFile>(json, Options);
        }
        catch (JsonException ex)
        {
            report.Error(LoadIssueCategory.Format, $"プロジェクトファイルが壊れているため開けません（{ex.Message}）");
            return null;
        }
        if (project is null || project.Format != FormatId)
        {
            report.Error(LoadIssueCategory.Format, "3D Theater のプロジェクトファイルではありません");
            return null;
        }
        if (project.Version > CurrentVersion)
        {
            report.Error(LoadIssueCategory.Format,
                $"新しい版の 3D Theater で保存されたプロジェクトです（形式の版 {project.Version}、この版で読めるのは {CurrentVersion} まで）。3D Theater を新しくしてください");
            return null;
        }
        project.Playback ??= new PlaybackEntry();
        project.Nodes ??= [];
        project.Models ??= [];
        return project;
    }
}

/// <summary>今のシーンをプロジェクトファイルの中身にする。</summary>
public static class ProjectCapture
{
    /// <param name="projectDirectory">モデルの場所を相対で書くときの基準（無題なら null で、絶対パスで書く）。</param>
    /// <param name="missingModels">開いたときに見つからなかったモデル（消さずにそのまま書き戻す）。</param>
    public static ProjectFile Capture(SceneDocument scene, PlaybackService playback, AnimationService animation,
        string? projectDirectory, IEnumerable<ModelEntry> missingModels)
    {
        var project = new ProjectFile
        {
            App = typeof(ProjectCapture).Assembly.GetName().Version?.ToString(3),
            SavedAt = DateTime.Now,
            Playback = new PlaybackEntry
            {
                FrameRate = playback.FrameRate,
                EndFrame = playback.EndFrame,
                CurrentFrame = playback.CurrentFrame,
                Loop = playback.Loop,
            },
            AutoKey = animation.AutoKey,
        };
        foreach (var (role, node) in scene.FixedNodes)
        {
            project.Nodes[role] = Node(node);
        }
        foreach (ModelNode model in scene.ModelsGroup.Children.OfType<ModelNode>())
        {
            project.Models.Add(Model(model, projectDirectory));
        }
        project.Models.AddRange(missingModels);
        return project;
    }

    private static NodeEntry Node(SceneNode node)
    {
        var entry = new NodeEntry { Name = node.Name, Visible = node.IsVisible, Locked = node.IsLocked };
        if (node.HasTransform)
        {
            entry.Position = V(node.Position);
            entry.Rotation = V(node.Rotation);
            entry.Scale = node.CanScale ? V(node.Scale) : null;
        }
        switch (node)
        {
            case CameraNode camera:
                entry.FieldOfView = camera.FieldOfView;
                entry.NearClip = camera.NearClip;
                entry.FarClip = camera.FarClip;
                entry.TargetDistance = camera.TargetDistance;
                entry.Unregistered = camera.HasUnregisteredChange;
                entry.Keys = camera.Keys.Count > 0 ? [.. camera.Keys.Keys.Select(KeyCodec.Encode)] : null;
                break;
            case LightNode light:
                entry.Intensity = light.Intensity;
                break;
            case TestObjectNode test:
                entry.SpinWithFrame = test.SpinWithFrame;
                break;
        }
        return entry;
    }

    private static ModelEntry Model(ModelNode model, string? projectDirectory)
    {
        var entry = new ModelEntry
        {
            File = RelativeOrAbsolute(model.FilePath, projectDirectory),
            AbsoluteFile = model.FilePath,
            Name = model.Name,
            Visible = model.IsVisible,
            Locked = model.IsLocked,
            Position = V(model.Position),
            Rotation = V(model.Rotation),
            Scale = V(model.Scale),
        };
        var groups = new List<GroupEntry>();
        foreach (SceneNode group in model.DescendantsAndSelf().Where(n => n.Kind == SceneNodeKind.Group))
        {
            if (!group.IsVisible || group.IsLocked)
            {
                groups.Add(new GroupEntry { Path = GroupPath(model, group), Visible = group.IsVisible, Locked = group.IsLocked });
            }
        }
        entry.Groups = groups.Count > 0 ? groups : null;

        var bones = new List<BoneEntry>();
        foreach (BoneNode bone in model.Bones)
        {
            if (bone.Keys.Count == 0 && !bone.IsPosed && !bone.HasUnregisteredChange && !bone.IsLocked && bone.IsVisible)
            {
                continue;
            }
            bones.Add(new BoneEntry
            {
                Name = bone.Name,
                Index = bone.Index,
                Position = bone.Position != Vector3.Zero ? V(bone.Position) : null,
                Rotation = bone.Orientation != Quaternion.Identity ? Q(bone.Orientation) : null,
                Visible = bone.IsVisible,
                Locked = bone.IsLocked,
                Unregistered = bone.HasUnregisteredChange,
                Keys = bone.Keys.Count > 0 ? [.. bone.Keys.Keys.Select(KeyCodec.Encode)] : null,
            });
        }
        entry.Bones = bones.Count > 0 ? bones : null;

        var morphs = new List<MorphEntry>();
        foreach (MorphNode morph in model.Morphs)
        {
            if (morph.Keys.Count == 0 && morph.Weight == 0 && !morph.HasUnregisteredChange && !morph.IsLocked)
            {
                continue;
            }
            morphs.Add(new MorphEntry
            {
                Name = morph.Name,
                Index = morph.Index,
                Weight = morph.Weight,
                Locked = morph.IsLocked,
                Unregistered = morph.HasUnregisteredChange,
                Keys = morph.Keys.Count > 0 ? [.. morph.Keys.Keys.Select(KeyCodec.Encode)] : null,
            });
        }
        entry.Morphs = morphs.Count > 0 ? morphs : null;
        return entry;
    }

    /// <summary>モデルから見たまとまりの場所（「ボーン/腕」など）。</summary>
    public static string GroupPath(ModelNode model, SceneNode group)
    {
        var names = new List<string>();
        for (SceneNode? n = group; n is not null && n != model; n = n.Parent)
        {
            names.Add(n.Name);
        }
        names.Reverse();
        return string.Join("/", names);
    }

    /// <summary>プロジェクトのフォルダーから見た場所。ドライブが違うか、無題なら絶対パス。</summary>
    public static string RelativeOrAbsolute(string file, string? projectDirectory)
    {
        if (projectDirectory is null || !string.Equals(Path.GetPathRoot(file), Path.GetPathRoot(projectDirectory), StringComparison.OrdinalIgnoreCase))
        {
            return file;
        }
        return Path.GetRelativePath(projectDirectory, file);
    }

    private static float[] V(Vector3 v) => [v.X, v.Y, v.Z];

    private static float[] Q(Quaternion q) => [q.X, q.Y, q.Z, q.W];
}

/// <summary>保存したプロジェクトを、シーンに当てはめる。</summary>
public static class ProjectApply
{
    /// <summary>モデルのファイルを探す。プロジェクトのフォルダーから見た場所 → 保存したときの絶対パスの順。見つからなければ null。</summary>
    public static string? ResolveModelPath(ModelEntry entry, string? projectDirectory)
    {
        var candidates = new List<string>();
        if (!string.IsNullOrWhiteSpace(entry.File))
        {
            candidates.Add(Path.IsPathRooted(entry.File) || projectDirectory is null ? entry.File : Path.GetFullPath(Path.Combine(projectDirectory, entry.File)));
        }
        if (!string.IsNullOrWhiteSpace(entry.AbsoluteFile))
        {
            candidates.Add(entry.AbsoluteFile);
        }
        return candidates.FirstOrDefault(File.Exists);
    }

    /// <summary>再生の設定・自動キー・カメラや照明などの決まった項目を戻す。</summary>
    public static void ApplyScene(SceneDocument scene, PlaybackService playback, AnimationService animation, ProjectFile project, LoadReport report)
    {
        if (PlaybackService.SupportedFrameRates.Contains(project.Playback.FrameRate))
        {
            playback.FrameRate = project.Playback.FrameRate;
        }
        else
        {
            report.Warning(LoadIssueCategory.Format, $"フレームの速さ（{project.Playback.FrameRate} fps）には対応していないため、{playback.FrameRate} fps のままにしました");
        }
        playback.EndFrame = project.Playback.EndFrame;
        playback.Loop = project.Playback.Loop;
        animation.AutoKey = project.AutoKey;

        // 値を戻す間は「手で変えた」とみなさない（カメラのキーの当てはめも止める）
        using IDisposable suspended = animation.SuspendTracking();
        foreach (var (role, entry) in project.Nodes)
        {
            if (!scene.FixedNodes.TryGetValue(role, out SceneNode? node))
            {
                report.Info(LoadIssueCategory.Note, $"この版にない項目「{role}」の設定は使いませんでした");
                continue;
            }
            if (node.CanRename && !string.IsNullOrWhiteSpace(entry.Name))
            {
                node.Name = entry.Name;
            }
            node.IsVisible = entry.Visible;
            if (node.HasTransform)
            {
                node.Position = V(entry.Position, node.Position);
                node.Rotation = V(entry.Rotation, node.Rotation);
                if (node.CanScale)
                {
                    node.Scale = V(entry.Scale, node.Scale);
                }
            }
            switch (node)
            {
                case CameraNode camera:
                    camera.FieldOfView = entry.FieldOfView ?? camera.FieldOfView;
                    camera.FarClip = entry.FarClip ?? camera.FarClip;
                    camera.NearClip = entry.NearClip ?? camera.NearClip;
                    camera.FarClip = entry.FarClip ?? camera.FarClip;
                    camera.TargetDistance = entry.TargetDistance ?? camera.TargetDistance;
                    camera.Keys.ReplaceAll((entry.Keys ?? []).Select(KeyCodec.DecodeCamera).OfType<CameraKey>());
                    camera.HasUnregisteredChange = entry.Unregistered;
                    break;
                case LightNode light:
                    light.Intensity = entry.Intensity ?? light.Intensity;
                    break;
                case TestObjectNode test:
                    test.SpinWithFrame = entry.SpinWithFrame ?? test.SpinWithFrame;
                    break;
            }
            node.IsLocked = entry.Locked; // ロックは値を戻した後に掛ける
        }
    }

    /// <summary>
    /// 読み込んだモデルに、保存した状態（置き方・まとまりの表示とロック・ボーンとモーフのキーと値）を戻す。
    /// ボーン・モーフは番号と名前が合えばそれを、合わなければ名前で探す（モデルのファイルが変わったとき）。
    /// </summary>
    public static void ApplyModel(ModelNode model, ModelEntry entry, AnimationService animation, LoadReport report)
    {
        var missingBones = new List<string>();
        var missingMorphs = new List<string>();
        using (animation.SuspendTracking())
        {
            if (!string.IsNullOrWhiteSpace(entry.Name))
            {
                model.Name = entry.Name;
            }
            model.Position = V(entry.Position, Vector3.Zero);
            model.Rotation = V(entry.Rotation, Vector3.Zero);
            model.Scale = V(entry.Scale, Vector3.One);
            model.IsVisible = entry.Visible;

            var groups = model.DescendantsAndSelf().Where(n => n.Kind == SceneNodeKind.Group)
                .GroupBy(g => ProjectCapture.GroupPath(model, g)).ToDictionary(g => g.Key, g => g.First());
            foreach (GroupEntry g in entry.Groups ?? [])
            {
                if (groups.TryGetValue(g.Path, out SceneNode? group))
                {
                    group.IsVisible = g.Visible;
                    group.IsLocked = g.Locked;
                }
            }

            foreach (BoneEntry b in entry.Bones ?? [])
            {
                BoneNode? bone = Find(model.Bones, b.Index, b.Name, n => n.Name);
                if (bone is null)
                {
                    missingBones.Add(b.Name);
                    continue;
                }
                bone.Keys.ReplaceAll((b.Keys ?? []).Select(KeyCodec.DecodeBone).OfType<BoneKey>());
                bone.Position = V(b.Position, Vector3.Zero);
                bone.Orientation = Q(b.Rotation);
                bone.IsVisible = b.Visible;
                bone.HasUnregisteredChange = b.Unregistered;
                bone.IsLocked = b.Locked;
            }
            foreach (MorphEntry m in entry.Morphs ?? [])
            {
                MorphNode? morph = Find(model.Morphs, m.Index, m.Name, n => n.Name);
                if (morph is null)
                {
                    missingMorphs.Add(m.Name);
                    continue;
                }
                morph.Keys.ReplaceAll((m.Keys ?? []).Select(KeyCodec.DecodeMorph).OfType<MorphKey>());
                morph.Weight = m.Weight;
                morph.HasUnregisteredChange = m.Unregistered;
                morph.IsLocked = m.Locked;
            }
            model.IsLocked = entry.Locked;
        }
        report.AddAggregated(LoadSeverity.Warning, LoadIssueCategory.InvalidReference,
            $"モデル「{model.Name}」に無いため戻せなかったボーン（モデルのファイルが保存したときと変わった可能性があります）", missingBones, missingBones.Count);
        report.AddAggregated(LoadSeverity.Warning, LoadIssueCategory.InvalidReference,
            $"モデル「{model.Name}」に無いため戻せなかったモーフ", missingMorphs, missingMorphs.Count);
    }

    private static T? Find<T>(IReadOnlyList<T> items, int index, string name, Func<T, string> nameOf) where T : class
    {
        if ((uint)index < (uint)items.Count && nameOf(items[index]) == name)
        {
            return items[index];
        }
        return items.FirstOrDefault(i => nameOf(i) == name);
    }

    private static Vector3 V(float[]? v, Vector3 fallback) =>
        v is { Length: 3 } && v.All(float.IsFinite) ? new Vector3(v[0], v[1], v[2]) : fallback;

    private static Quaternion Q(float[]? q) =>
        q is { Length: 4 } && q.All(float.IsFinite) && new Quaternion(q[0], q[1], q[2], q[3]).LengthSquared() > 1e-12f
            ? Quaternion.Normalize(new Quaternion(q[0], q[1], q[2], q[3]))
            : Quaternion.Identity;
}

/// <summary>キーを数の並びにする・戻す。</summary>
internal static class KeyCodec
{
    public static float[] Encode(BoneKey k)
    {
        BoneInterpolation ip = k.Interpolation;
        bool linear = ip.X.IsLinear && ip.Y.IsLinear && ip.Z.IsLinear && ip.Rotation.IsLinear;
        var values = new List<float>
        {
            k.Frame, k.Translation.X, k.Translation.Y, k.Translation.Z, k.Rotation.X, k.Rotation.Y, k.Rotation.Z, k.Rotation.W,
            k.Kind == KeyInterpolationKind.Step ? 1 : 0,
        };
        if (!linear)
        {
            foreach (BezierCurve c in new[] { ip.X, ip.Y, ip.Z, ip.Rotation })
            {
                values.AddRange([c.Control1.X, c.Control1.Y, c.Control2.X, c.Control2.Y]);
            }
        }
        return [.. values];
    }

    public static BoneKey? DecodeBone(float[] v)
    {
        if (v is not { Length: 9 or 25 } || !v.All(float.IsFinite) || v[0] < 0)
        {
            return null;
        }
        BoneInterpolation ip = BoneInterpolation.Linear;
        if (v.Length == 25)
        {
            ip = new BoneInterpolation(Curve(v, 9), Curve(v, 13), Curve(v, 17), Curve(v, 21));
        }
        var rotation = new Quaternion(v[4], v[5], v[6], v[7]);
        rotation = rotation.LengthSquared() > 1e-12f ? Quaternion.Normalize(rotation) : Quaternion.Identity;
        return new BoneKey((int)v[0], new Vector3(v[1], v[2], v[3]), rotation, ip, v[8] == 1 ? KeyInterpolationKind.Step : KeyInterpolationKind.Curve);
    }

    public static float[] Encode(CameraKey k)
    {
        var values = new List<float>
        {
            k.Frame, k.Target.X, k.Target.Y, k.Target.Z, k.Rotation.X, k.Rotation.Y, k.Rotation.Z, k.Distance, k.FieldOfView,
            k.Kind == KeyInterpolationKind.Step ? 1 : 0, k.Orthographic ? 1 : 0,
        };
        CameraInterpolation ip = k.Interpolation;
        if (!ip.IsLinear)
        {
            foreach (BezierCurve c in new[] { ip.X, ip.Y, ip.Z, ip.Rotation, ip.Distance, ip.FieldOfView })
            {
                values.AddRange([c.Control1.X, c.Control1.Y, c.Control2.X, c.Control2.Y]);
            }
        }
        return [.. values];
    }

    public static CameraKey? DecodeCamera(float[] v)
    {
        if (v is not { Length: 11 or 35 } || !v.All(float.IsFinite) || v[0] < 0)
        {
            return null;
        }
        CameraInterpolation ip = v.Length == 35
            ? new CameraInterpolation(Curve(v, 11), Curve(v, 15), Curve(v, 19), Curve(v, 23), Curve(v, 27), Curve(v, 31))
            : CameraInterpolation.Linear;
        return new CameraKey((int)v[0], new Vector3(v[1], v[2], v[3]), new Vector3(v[4], v[5], v[6]), v[7], v[8], ip,
            v[9] == 1 ? KeyInterpolationKind.Step : KeyInterpolationKind.Curve, v[10] == 1);
    }

    public static float[] Encode(MorphKey k)
    {
        var values = new List<float> { k.Frame, k.Weight, k.Kind == KeyInterpolationKind.Step ? 1 : 0 };
        if (!k.Curve.IsLinear)
        {
            values.AddRange([k.Curve.Control1.X, k.Curve.Control1.Y, k.Curve.Control2.X, k.Curve.Control2.Y]);
        }
        return [.. values];
    }

    public static MorphKey? DecodeMorph(float[] v)
    {
        if (v is not { Length: 3 or 7 } || !v.All(float.IsFinite) || v[0] < 0)
        {
            return null;
        }
        BezierCurve curve = v.Length == 7 ? Curve(v, 3) : BezierCurve.Linear;
        return new MorphKey((int)v[0], v[1], curve, v[2] == 1 ? KeyInterpolationKind.Step : KeyInterpolationKind.Curve);
    }

    private static BezierCurve Curve(float[] v, int i) =>
        new(new Vector2(Math.Clamp(v[i], 0, 1), Math.Clamp(v[i + 1], 0, 1)), new Vector2(Math.Clamp(v[i + 2], 0, 1), Math.Clamp(v[i + 3], 0, 1)));
}
