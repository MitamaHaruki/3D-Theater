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

    /// <summary>物理で動く剛体を床（高さ 0）に当てるか（無い古いファイルは当てる）。</summary>
    public bool PhysicsFloor { get; set; } = true;

    /// <summary>物理で動く剛体を、ほかのモデルの体にも当てるか（無い古いファイルは当てる）。</summary>
    public bool PhysicsBetweenModels { get; set; } = true;

    /// <summary>描き方（無い古いファイルは初期の設定）。</summary>
    public RenderingEntry Rendering { get; set; } = new();

    /// <summary>カメラ・照明などの決まった項目（キーは <see cref="SceneDocument.FixedNodes"/> の役目の名前）。</summary>
    public Dictionary<string, NodeEntry> Nodes { get; set; } = [];

    public List<ModelEntry> Models { get; set; } = [];

    /// <summary>音声（無ければ null）。</summary>
    public AudioEntry? Audio { get; set; }

    /// <summary>「照明を追加」で足した照明（無ければ書かない）。</summary>
    public List<LightEntry>? Lights { get; set; }
}

/// <summary>足した照明 1 つ。色は #RRGGBB、種類は「directional」「point」「spot」。</summary>
public sealed class LightEntry
{
    public string Name { get; set; } = string.Empty;

    public string Type { get; set; } = "point";

    public bool Visible { get; set; } = true;

    public bool Locked { get; set; }

    public float[] Position { get; set; } = [0, 0, 0];

    public float[] Rotation { get; set; } = [0, 0, 0];

    public string Color { get; set; } = "#FFFFFF";

    public float Intensity { get; set; } = 1f;

    public float Range { get; set; } = 30f;

    public float SpotAngle { get; set; } = 40f;

    public float SpotSoftness { get; set; } = 0.3f;

    /// <summary>影を落とすか（影をレイトレーシングで求めるとき）。無い前の版のファイルは落とす。</summary>
    public bool CastShadow { get; set; } = true;

    /// <summary>未登録の変更（キーのある照明を手で変えたまま）。</summary>
    public bool Unregistered { get; set; }

    /// <summary>キー（<see cref="NodeEntry.Keys"/> の照明と同じ並び）。無ければ書かない。</summary>
    public float[][]? Keys { get; set; }

    internal static string TypeName(LightType type) => type switch
    {
        LightType.Directional => "directional",
        LightType.Spot => "spot",
        _ => "point",
    };

    internal static LightType? ParseType(string? text) => text?.ToLowerInvariant() switch
    {
        "directional" => LightType.Directional,
        "point" => LightType.Point,
        "spot" => LightType.Spot,
        _ => null,
    };
}

/// <summary>描き方の設定（<see cref="RenderSettings"/>）。</summary>
public sealed class RenderingEntry
{
    public bool SelfShadow { get; set; } = true;

    public bool GroundShadow { get; set; } = true;

    /// <summary>映像効果（初期のまま＝何も使わず値も初期値なら書かない。無い古いファイルは映像効果なし）。</summary>
    public VisualEffects? Effects { get; set; }

    /// <summary>イラスト風の表現（映像効果と同じく、初期のままなら書かない）。</summary>
    public IllustrationStyle? Illustration { get; set; }

    /// <summary>床の映り込み（初期のままなら書かない）。</summary>
    public FloorReflection? Reflection { get; set; }

    /// <summary>霧（初期のままなら書かない）。</summary>
    public SceneFog? Fog { get; set; }

    /// <summary>影の見え方（初期のままなら書かない）。</summary>
    public ShadowStyle? ShadowStyle { get; set; }

    /// <summary>環境の画像（初期のままなら書かない）。</summary>
    public EnvironmentEntry? Environment { get; set; }

    /// <param name="projectDirectory">環境の画像の場所を相対で書くときの基準（無題なら null で、絶対パスで書く）。</param>
    public static RenderingEntry From(RenderSettings settings, string? projectDirectory = null) => new()
    {
        SelfShadow = settings.SelfShadow,
        GroundShadow = settings.GroundShadow,
        Effects = settings.Effects == VisualEffects.None ? null : settings.Effects,
        Illustration = settings.Illustration == IllustrationStyle.None ? null : settings.Illustration,
        Reflection = settings.Reflection == FloorReflection.Off ? null : settings.Reflection,
        Fog = settings.Fog == SceneFog.Off ? null : settings.Fog,
        ShadowStyle = settings.ShadowStyle == ShadowStyle.Default ? null : settings.ShadowStyle,
        Environment = EnvironmentEntry.From(settings.Environment, projectDirectory),
    };

    /// <param name="projectDirectory">環境の画像を探す基準（プロジェクトのフォルダー）。</param>
    public void ApplyTo(RenderSettings settings, string? projectDirectory = null)
    {
        settings.SelfShadow = SelfShadow;
        settings.GroundShadow = GroundShadow;
        settings.Effects = Effects ?? VisualEffects.None;
        settings.Illustration = Illustration ?? IllustrationStyle.None;
        settings.Reflection = Reflection ?? FloorReflection.Off;
        settings.Fog = Fog ?? SceneFog.Off;
        settings.ShadowStyle = ShadowStyle ?? Editor.ShadowStyle.Default;
        settings.Environment = Environment?.ToSetting(projectDirectory) ?? SceneEnvironment.Off;
    }
}

/// <summary>環境の画像の設定（<see cref="SceneEnvironment"/>）。画像の場所は、モデル・音声と同じく相対と絶対の両方で書く。</summary>
public sealed class EnvironmentEntry
{
    public bool Enabled { get; set; }

    /// <summary>画像のファイル。プロジェクトのフォルダーから見た場所（ドライブが違えば絶対パス）。</summary>
    public string? File { get; set; }

    /// <summary>保存したときの絶対パス（フォルダーごと動かしていないときの予備）。</summary>
    public string? AbsoluteFile { get; set; }

    public float Intensity { get; set; } = 1f;

    public float Rotation { get; set; }

    public bool ShowBackground { get; set; } = true;

    public static EnvironmentEntry? From(SceneEnvironment environment, string? projectDirectory) =>
        environment == SceneEnvironment.Off
            ? null
            : new EnvironmentEntry
            {
                Enabled = environment.Enabled,
                File = environment.File is { } file ? ProjectCapture.RelativeOrAbsolute(file, projectDirectory) : null,
                AbsoluteFile = environment.File,
                Intensity = environment.Intensity,
                Rotation = environment.Rotation,
                ShowBackground = environment.ShowBackground,
            };

    /// <summary>
    /// 設定に戻す。画像はプロジェクトのフォルダーから見た場所 → 保存したときの絶対パスの順に探し、
    /// どちらにも無ければ前者のまま（画面に「見つかりません」と出す）。
    /// </summary>
    public SceneEnvironment ToSetting(string? projectDirectory)
    {
        string? relative = string.IsNullOrWhiteSpace(File) ? null
            : Path.IsPathRooted(File) || projectDirectory is null ? File : Path.GetFullPath(Path.Combine(projectDirectory, File));
        string? file = relative is not null && System.IO.File.Exists(relative) ? relative
            : !string.IsNullOrWhiteSpace(AbsoluteFile) && System.IO.File.Exists(AbsoluteFile) ? AbsoluteFile
            : relative ?? AbsoluteFile;
        return new SceneEnvironment
        {
            Enabled = Enabled,
            File = file,
            Intensity = Intensity,
            Rotation = Rotation,
            ShowBackground = ShowBackground,
        };
    }
}

public sealed class AudioEntry
{
    /// <summary>音声のファイル。プロジェクトのフォルダーから見た場所（ドライブが違えば絶対パス）。</summary>
    public string File { get; set; } = string.Empty;

    /// <summary>保存したときの絶対パス（フォルダーごと動かしていないときの予備）。</summary>
    public string? AbsoluteFile { get; set; }

    public string Name { get; set; } = string.Empty;

    /// <summary>鳴り始めのフレーム。</summary>
    public int StartFrame { get; set; }

    /// <summary>音量（0〜1）。</summary>
    public float Volume { get; set; } = 1f;

    /// <summary>鳴らすか（シーン一覧の目の印）。</summary>
    public bool Visible { get; set; } = true;

    public bool Locked { get; set; }
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

    /// <summary>照明の色（#RRGGBB。無い古いファイルは白）。</summary>
    public string? Color { get; set; }

    public bool? SpinWithFrame { get; set; }

    /// <summary>カメラの注視点までの距離。</summary>
    public float? TargetDistance { get; set; }

    /// <summary>カメラ・照明の未登録の変更。</summary>
    public bool Unregistered { get; set; }

    /// <summary>
    /// カメラ・照明・映像効果のキー。カメラ: フレーム・注視点 3・回転 3・距離・視野角・種類・平行投影、曲線が直線でなければ 24 個足す。
    /// 照明: フレーム・位置 3・回転 3・色 3・明るさ・届く距離・広がり・ぼかし・種類、曲線が直線でなければ 4 個足す。
    /// 映像効果: フレーム・明るさ・コントラスト・彩度・色温度・ブルーム 3・周辺減光・被写界深度 3・霧の始まり・見通せる距離・霧の色 3・種類、
    /// 曲線が直線でなければ 4 個足す。
    /// </summary>
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

    /// <summary>材質の上書き（拡張材質）。上書きのある材質だけ。無ければ書かない。</summary>
    public List<MaterialEntry>? Materials { get; set; }

    /// <summary>表示・IK（初期のまま＝表示・IK を全部解く・キーなしなら書かない。無い前の版のファイルは初期のまま）。</summary>
    public DisplayEntry? Display { get; set; }
}

/// <summary>モデルの表示・IK。切った IK は IK ボーンの名前で書く。</summary>
public sealed class DisplayEntry
{
    public bool Shown { get; set; } = true;

    public string[]? IkOff { get; set; }

    public bool Locked { get; set; }

    public bool Unregistered { get; set; }

    public List<DisplayKeyEntry>? Keys { get; set; }
}

/// <summary>表示・IK のキー 1 つ。</summary>
public sealed class DisplayKeyEntry
{
    public int Frame { get; set; }

    public bool Shown { get; set; } = true;

    public string[]? IkOff { get; set; }
}

/// <summary>材質の上書き 1 つ。色は #RRGGBB。</summary>
public sealed class MaterialEntry
{
    public int Index { get; set; }

    public string Name { get; set; } = string.Empty;

    /// <summary>「toon」か「pbr」。</summary>
    public string Shading { get; set; } = "toon";

    public string Tint { get; set; } = "#FFFFFF";

    public float Metallic { get; set; }

    public float Roughness { get; set; } = 0.5f;

    public string Emissive { get; set; } = "#FFFFFF";

    public float EmissiveStrength { get; set; }

    public static MaterialEntry From(int index, string name, MaterialOverride value) => new()
    {
        Index = index,
        Name = name,
        Shading = value.Shading == MaterialShading.Pbr ? "pbr" : "toon",
        Tint = ImageExport.FormatColor(value.Tint),
        Metallic = value.Metallic,
        Roughness = value.Roughness,
        Emissive = ImageExport.FormatColor(value.EmissiveColor),
        EmissiveStrength = value.EmissiveStrength,
    };

    public MaterialOverride ToOverride() => new MaterialOverride
    {
        Shading = string.Equals(Shading, "pbr", StringComparison.OrdinalIgnoreCase) ? MaterialShading.Pbr : MaterialShading.Toon,
        Tint = ImageExport.TryParseColor(Tint, out Vector3 tint) ? tint : Vector3.One,
        Metallic = Metallic,
        Roughness = Roughness,
        EmissiveColor = ImageExport.TryParseColor(Emissive, out Vector3 emissive) ? emissive : Vector3.One,
        EmissiveStrength = EmissiveStrength,
    }.Normalized();
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
    /// <param name="missingAudio">開いたときに見つからなかった音声（シーンに音声が無ければ、そのまま書き戻す）。</param>
    public static ProjectFile Capture(SceneDocument scene, PlaybackService playback, AnimationService animation,
        string? projectDirectory, IEnumerable<ModelEntry> missingModels, AudioEntry? missingAudio = null)
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
        List<LightEntry> lights = [.. scene.AddedLights.Select(l => new LightEntry
        {
            Name = l.Name,
            Type = LightEntry.TypeName(l.Type),
            Visible = l.IsVisible,
            Locked = l.IsLocked,
            Position = V(l.Position),
            Rotation = V(l.Rotation),
            Color = ImageExport.FormatColor(l.Color),
            Intensity = l.Intensity,
            Range = l.Range,
            SpotAngle = l.SpotAngle,
            SpotSoftness = l.SpotSoftness,
            CastShadow = l.CastsShadow,
            Unregistered = l.HasUnregisteredChange,
            Keys = l.Keys.Count > 0 ? [.. l.Keys.Keys.Select(KeyCodec.Encode)] : null,
        })];
        project.Lights = lights.Count > 0 ? lights : null;
        foreach (ModelNode model in scene.ModelsGroup.Children.OfType<ModelNode>())
        {
            project.Models.Add(Model(model, projectDirectory));
        }
        project.Models.AddRange(missingModels);
        project.Audio = scene.Audio is { } audio
            ? new AudioEntry
            {
                File = RelativeOrAbsolute(audio.FilePath, projectDirectory),
                AbsoluteFile = audio.FilePath,
                Name = audio.Name,
                StartFrame = audio.StartFrame,
                Volume = audio.Volume,
                Visible = audio.IsVisible,
                Locked = audio.IsLocked,
            }
            : missingAudio;
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
                entry.Color = light.Color == Vector3.One ? null : ImageExport.FormatColor(light.Color);
                entry.Unregistered = light.HasUnregisteredChange;
                entry.Keys = light.Keys.Count > 0 ? [.. light.Keys.Keys.Select(KeyCodec.Encode)] : null;
                break;
            case EffectsNode effects:
                entry.Unregistered = effects.HasUnregisteredChange;
                entry.Keys = effects.Keys.Count > 0 ? [.. effects.Keys.Keys.Select(KeyCodec.Encode)] : null;
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

        var materials = model.MaterialOverrides.Items
            .Select(pair => MaterialEntry.From(pair.Key, pair.Key < model.MaterialNames.Count ? model.MaterialNames[pair.Key] : string.Empty,
                pair.Value))
            .ToList();
        entry.Materials = materials.Count > 0 ? materials : null;

        if (model.Display is { } display
            && (!display.Shown || !display.Ik.AllEnabled || display.IsLocked || display.HasUnregisteredChange || display.Keys.Count > 0))
        {
            entry.Display = new DisplayEntry
            {
                Shown = display.Shown,
                IkOff = IkNames(display, display.Ik),
                Locked = display.IsLocked,
                Unregistered = display.HasUnregisteredChange,
                Keys = display.Keys.Count > 0
                    ? [.. display.Keys.Keys.Select(k => new DisplayKeyEntry { Frame = k.Frame, Shown = k.Shown, IkOff = IkNames(display, k.Ik) })]
                    : null,
            };
        }
        return entry;
    }

    // 切った IK ボーンの名前（無ければ null）
    private static string[]? IkNames(ModelDisplayNode display, IkSwitchSet? ik)
    {
        if (ik is null || ik.AllEnabled)
        {
            return null;
        }
        string[] names = [.. display.IkBones.Where(b => !ik.IsEnabled(b.Index)).Select(b => b.Name)];
        return names.Length > 0 ? names : null;
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
    public static string? ResolveModelPath(ModelEntry entry, string? projectDirectory) => ResolvePath(entry.File, entry.AbsoluteFile, projectDirectory);

    /// <summary>音声のファイルを探す（モデルと同じ順）。見つからなければ null。</summary>
    public static string? ResolveAudioPath(AudioEntry entry, string? projectDirectory) => ResolvePath(entry.File, entry.AbsoluteFile, projectDirectory);

    private static string? ResolvePath(string file, string? absoluteFile, string? projectDirectory)
    {
        var candidates = new List<string>();
        if (!string.IsNullOrWhiteSpace(file))
        {
            candidates.Add(Path.IsPathRooted(file) || projectDirectory is null ? file : Path.GetFullPath(Path.Combine(projectDirectory, file)));
        }
        if (!string.IsNullOrWhiteSpace(absoluteFile))
        {
            candidates.Add(absoluteFile);
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
                    light.Color = ImageExport.TryParseColor(entry.Color, out Vector3 color) ? color : Vector3.One;
                    light.Keys.ReplaceAll((entry.Keys ?? []).Select(KeyCodec.DecodeLight).OfType<LightKey>());
                    light.HasUnregisteredChange = entry.Unregistered;
                    break;
                case EffectsNode effects:
                    effects.Keys.ReplaceAll((entry.Keys ?? []).Select(KeyCodec.DecodeEffect).OfType<EffectKey>());
                    effects.HasUnregisteredChange = entry.Unregistered;
                    break;
                case TestObjectNode test:
                    test.SpinWithFrame = entry.SpinWithFrame ?? test.SpinWithFrame;
                    break;
            }
            node.IsLocked = entry.Locked; // ロックは値を戻した後に掛ける
        }

        // 足した照明（前の照明は外してから、保存した順に足す）
        foreach (LightNode old in scene.AddedLights.ToArray())
        {
            scene.Remove(old);
        }
        foreach (LightEntry entry in project.Lights ?? [])
        {
            if (LightEntry.ParseType(entry.Type) is not { } type)
            {
                report.Warning(LoadIssueCategory.Format, $"照明「{entry.Name}」の種類（{entry.Type}）が分からないため、読み込みませんでした");
                continue;
            }
            var light = new LightNode(string.IsNullOrWhiteSpace(entry.Name) ? LightPresenceAction.NameFor(scene, type) : entry.Name, type)
            {
                Position = V(entry.Position, Vector3.Zero),
                Rotation = V(entry.Rotation, Vector3.Zero),
                Color = ImageExport.TryParseColor(entry.Color, out Vector3 color) ? color : Vector3.One,
                Intensity = entry.Intensity,
                Range = entry.Range,
                SpotAngle = entry.SpotAngle,
                SpotSoftness = entry.SpotSoftness,
                CastsShadow = entry.CastShadow,
                IsVisible = entry.Visible,
            };
            light.Keys.ReplaceAll((entry.Keys ?? []).Select(KeyCodec.DecodeLight).OfType<LightKey>());
            light.HasUnregisteredChange = entry.Unregistered;
            scene.Add(scene.LightsGroup, light);
            light.IsLocked = entry.Locked;
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
        var missingMaterials = new List<string>();
        var missingIk = new List<string>();
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
            // 材質の上書き: 番号と名前が合えばその材質、合わなければ名前で探す
            var overrides = new List<KeyValuePair<int, MaterialOverride>>();
            foreach (MaterialEntry m in entry.Materials ?? [])
            {
                int index = (uint)m.Index < (uint)model.MaterialNames.Count && model.MaterialNames[m.Index] == m.Name
                    ? m.Index
                    : IndexOfName(model.MaterialNames, m.Name);
                if (index < 0)
                {
                    missingMaterials.Add(m.Name);
                    continue;
                }
                overrides.Add(new(index, m.ToOverride()));
            }
            model.MaterialOverrides = MaterialOverrideSet.From(overrides);
            // 表示・IK（書いていなければ初期のまま。IK は名前で探す）
            if (model.Display is { } display)
            {
                DisplayEntry d = entry.Display ?? new DisplayEntry();
                display.Keys.ReplaceAll((d.Keys ?? []).Where(k => k.Frame >= 0)
                    .Select(k => new DisplayKey(k.Frame, k.Shown, IkSet(display, k.IkOff, missingIk))));
                display.Shown = d.Shown;
                display.Ik = IkSet(display, d.IkOff, missingIk);
                display.HasUnregisteredChange = d.Unregistered;
                display.IsLocked = d.Locked;
            }
            model.IsLocked = entry.Locked;
        }
        report.AddAggregated(LoadSeverity.Warning, LoadIssueCategory.InvalidReference,
            $"モデル「{model.Name}」に無いため戻せなかった IK の切り替え", missingIk, missingIk.Count);
        report.AddAggregated(LoadSeverity.Warning, LoadIssueCategory.InvalidReference,
            $"モデル「{model.Name}」に無いため戻せなかったボーン（モデルのファイルが保存したときと変わった可能性があります）", missingBones, missingBones.Count);
        report.AddAggregated(LoadSeverity.Warning, LoadIssueCategory.InvalidReference,
            $"モデル「{model.Name}」に無いため戻せなかった材質の上書き", missingMaterials, missingMaterials.Count);
        report.AddAggregated(LoadSeverity.Warning, LoadIssueCategory.InvalidReference,
            $"モデル「{model.Name}」に無いため戻せなかったモーフ", missingMorphs, missingMorphs.Count);
    }

    // 切った IK ボーンの名前から、切った IK の集まり（モデルに無い名前は missing へ）
    private static IkSwitchSet IkSet(ModelDisplayNode display, string[]? names, List<string> missing)
    {
        if (names is not { Length: > 0 })
        {
            return IkSwitchSet.AllOn;
        }
        var disabled = new List<int>();
        foreach (string name in names)
        {
            if (display.IkBones.FirstOrDefault(b => b.Name == name) is { } bone)
            {
                disabled.Add(bone.Index);
            }
            else if (!missing.Contains(name))
            {
                missing.Add(name);
            }
        }
        return disabled.Count > 0 ? new IkSwitchSet(disabled) : IkSwitchSet.AllOn;
    }

    private static int IndexOfName(IReadOnlyList<string> names, string name)
    {
        for (int i = 0; i < names.Count; i++)
        {
            if (names[i] == name)
            {
                return i;
            }
        }
        return -1;
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

    public static float[] Encode(LightKey k)
    {
        LightPose p = k.Pose;
        var values = new List<float>
        {
            k.Frame, p.Position.X, p.Position.Y, p.Position.Z, p.Rotation.X, p.Rotation.Y, p.Rotation.Z, p.Color.X, p.Color.Y, p.Color.Z,
            p.Intensity, p.Range, p.SpotAngle, p.SpotSoftness, k.Kind == KeyInterpolationKind.Step ? 1 : 0,
        };
        if (!k.Curve.IsLinear)
        {
            values.AddRange([k.Curve.Control1.X, k.Curve.Control1.Y, k.Curve.Control2.X, k.Curve.Control2.Y]);
        }
        return [.. values];
    }

    public static LightKey? DecodeLight(float[] v)
    {
        if (v is not { Length: 15 or 19 } || !v.All(float.IsFinite) || v[0] < 0)
        {
            return null;
        }
        BezierCurve curve = v.Length == 19 ? Curve(v, 15) : BezierCurve.Linear;
        var pose = new LightPose(new Vector3(v[1], v[2], v[3]), new Vector3(v[4], v[5], v[6]),
            Vector3.Clamp(new Vector3(v[7], v[8], v[9]), Vector3.Zero, Vector3.One), Math.Max(0f, v[10]),
            Math.Clamp(v[11], LightNode.MinRange, LightNode.MaxRange), Math.Clamp(v[12], 1f, 179f), Math.Clamp(v[13], 0f, 1f));
        return new LightKey((int)v[0], pose, curve, v[14] == 1 ? KeyInterpolationKind.Step : KeyInterpolationKind.Curve);
    }

    public static float[] Encode(EffectKey k)
    {
        EffectPose p = k.Pose;
        var values = new List<float>
        {
            k.Frame, p.Exposure, p.Contrast, p.Saturation, p.Temperature, p.BloomIntensity, p.BloomThreshold, p.BloomSpread, p.Vignette,
            p.DofFocusDistance, p.DofFocusRange, p.DofBlur, p.FogStart, p.FogVisibility, p.FogColor.X, p.FogColor.Y, p.FogColor.Z,
            k.Kind == KeyInterpolationKind.Step ? 1 : 0,
        };
        if (!k.Curve.IsLinear)
        {
            values.AddRange([k.Curve.Control1.X, k.Curve.Control1.Y, k.Curve.Control2.X, k.Curve.Control2.Y]);
        }
        return [.. values];
    }

    public static EffectKey? DecodeEffect(float[] v)
    {
        if (v is not { Length: 18 or 22 } || !v.All(float.IsFinite) || v[0] < 0)
        {
            return null;
        }
        BezierCurve curve = v.Length == 22 ? Curve(v, 18) : BezierCurve.Linear;
        var pose = new EffectPose(v[1], v[2], v[3], v[4], v[5], v[6], v[7], v[8], v[9], v[10], v[11], v[12], v[13],
            Vector3.Clamp(new Vector3(v[14], v[15], v[16]), Vector3.Zero, Vector3.One));
        return new EffectKey((int)v[0], pose, curve, v[17] == 1 ? KeyInterpolationKind.Step : KeyInterpolationKind.Curve);
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
