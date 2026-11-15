using System.Numerics;
using System.Text.Json;
using Theater3D.Formats.Gltf;
using Theater3D.Formats.Pmx;
using Theater3D.Formats.Vmd;

namespace Theater3D.Formats.Fbx;

/// <summary>
/// FBX のモーション（Mixamo などのダンス・待機モーション）を読み、キャラクター（PMX と同じ中身）のボーンに当てはめた VMD の中身にする
/// （設計判断記録 85 章）。キャラクターごとに作り直す（骨組みと初めの姿勢が違うため）。
/// <list type="bullet">
/// <item>人型のボーンは、FBX の読み込みと同じく、ボーンの名前から推し量る（<see cref="FbxHumanoid"/>）。キャラクターのボーンは MMD の名前で探す。</item>
/// <item>動き: 元の各ボーンの、初めの姿勢からの向きの変化（ワールドで）を求め、キャラクターの同じボーンに同じ変化をさせる。初めの姿勢の違い
/// （元は腕を横に伸ばした T の字、キャラクターは A の字 など）は、ボーンの向き（ボーンから子のボーンへ）を元に合わせる回転で埋める。
/// そこから、キャラクターの親のボーンから見た回転（VMD のキー）にする。</item>
/// <item>腰の位置の動きは、センターの移動にする（腰の高さの比で大きさを合わせる）。</item>
/// <item>足は、元の動きをそのまま使うため、足の IK を切る（表示・IK のキー）。</item>
/// <item>MMD と同じ 30 fps で、毎フレームにキーを打つ。</item>
/// </list>
/// </summary>
public static class FbxMotionReader
{
    /// <summary>モーションのフレームの速さ（MMD と同じ）。</summary>
    public const int FrameRate = 30;

    /// <summary>読むいちばん長い長さ（10 分）。</summary>
    public const int MaxFrames = FrameRate * 60 * 10;

    private static readonly byte[] LinearInterpolation =
        VmdWriter.Interpolation((20, 20, 107, 107), (20, 20, 107, 107), (20, 20, 107, 107), (20, 20, 107, 107));

    // 人型のボーン → 向きを合わせるのに使う子のボーン（先にあるもの）
    private static readonly Dictionary<string, string[]> DirectionChild = BuildDirectionChildren();

    /// <summary>FBX に、ふつうのモデル（形）が無く、動くテイクがあるか（モーションのファイルらしいか）。</summary>
    public static bool LooksLikeMotion(string path)
    {
        try
        {
            FbxScene scene = FbxScene.Build(FbxFile.Parse(File.ReadAllBytes(path)));
            return !scene.OfKind("Geometry", "Mesh").Any() && scene.OfKind("AnimationCurve").Any();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ModelFormatException or ArgumentException)
        {
            return false;
        }
    }

    public static VmdLoadResult Load(string path, PmxDocument target)
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
        return Read(data, target, path, report);
    }

    /// <summary>メモリ上のデータから読む。</summary>
    public static VmdLoadResult Read(byte[] data, PmxDocument target) => Read(data, target, null, new LoadReport());

    private static VmdLoadResult Read(byte[] data, PmxDocument target, string? path, LoadReport report)
    {
        try
        {
            FbxDocument document = FbxFile.Parse(data);
            if (document.Version < 7000)
            {
                report.Error(LoadIssueCategory.Unsupported, "FBX の版が古すぎます（7.0 以降だけ）。Blender などで、新しい FBX（7.4 など）に書き出し直してください");
                return new VmdLoadResult(null, report, path);
            }
            FbxScene scene = FbxScene.Build(document);
            FbxObject[] models = [.. scene.OfKind("Model")];
            int[] parents = FbxTransform.Parents(models);
            Dictionary<string, int> humanoid = FbxHumanoid.Guess([.. models.Select(m => m.Name)], parents, out _);
            if (humanoid.Count == 0)
            {
                report.Error(LoadIssueCategory.Unsupported, "FBX のボーンの名前から、人型のボーン（腰・背骨・頭）を見つけられませんでした。人型のモーションだけ読めます");
                return new VmdLoadResult(null, report, path);
            }
            FbxAnimation? animation = FbxAnimation.Read(scene, models);
            if (animation is null)
            {
                report.Error(LoadIssueCategory.Format, "FBX にアニメーション（動くテイク）がありません");
                return new VmdLoadResult(null, report, path);
            }
            VmdDocument vmd = new Retarget(scene, models, parents, humanoid, animation, target, report).Build();
            return new VmdLoadResult(vmd, report, path);
        }
        catch (Exception ex) when (ex is ModelFormatException or InvalidOperationException or KeyNotFoundException or FormatException
            or OverflowException or IndexOutOfRangeException or ArgumentOutOfRangeException or ArgumentException or InvalidCastException or JsonException)
        {
            report.Error(LoadIssueCategory.Format, $"FBX のモーションを読めませんでした（{ex.Message}）");
            return new VmdLoadResult(null, report, path);
        }
    }

    private static Dictionary<string, string[]> BuildDirectionChildren()
    {
        var map = new Dictionary<string, string[]>(StringComparer.Ordinal)
        {
            ["spine"] = ["chest", "upperChest", "neck", "head"],
            ["chest"] = ["upperChest", "neck", "head"],
            ["upperChest"] = ["neck", "head"],
            ["neck"] = ["head"],
        };
        foreach (string side in new[] { "left", "right" })
        {
            map[side + "Shoulder"] = [side + "UpperArm"];
            map[side + "UpperArm"] = [side + "LowerArm"];
            map[side + "LowerArm"] = [side + "Hand"];
            map[side + "Hand"] = [side + "MiddleProximal", side + "IndexProximal", side + "RingProximal", side + "LittleProximal"];
            map[side + "UpperLeg"] = [side + "LowerLeg"];
            map[side + "LowerLeg"] = [side + "Foot"];
            map[side + "Foot"] = [side + "Toes"];
            map[side + "ThumbMetacarpal"] = [side + "ThumbProximal"];
            map[side + "ThumbProximal"] = [side + "ThumbDistal"];
            foreach (string finger in new[] { "Index", "Middle", "Ring", "Little" })
            {
                map[side + finger + "Proximal"] = [side + finger + "Intermediate"];
                map[side + finger + "Intermediate"] = [side + finger + "Distal"];
            }
        }
        return map;
    }

    /// <summary>2 つの向きの間の回転（<paramref name="from"/> を <paramref name="to"/> へ回す、いちばん小さな回転）。</summary>
    internal static Quaternion FromTo(Vector3 from, Vector3 to)
    {
        if (from.LengthSquared() < 1e-12f || to.LengthSquared() < 1e-12f)
        {
            return Quaternion.Identity;
        }
        from = Vector3.Normalize(from);
        to = Vector3.Normalize(to);
        float dot = Math.Clamp(Vector3.Dot(from, to), -1f, 1f);
        if (dot > 0.999999f)
        {
            return Quaternion.Identity;
        }
        Vector3 axis = Vector3.Cross(from, to);
        if (axis.LengthSquared() < 1e-12f)
        {
            // 逆向き: どれか直交する軸で半回転
            axis = Vector3.Cross(from, MathF.Abs(from.X) < 0.9f ? Vector3.UnitX : Vector3.UnitY);
        }
        return Quaternion.CreateFromAxisAngle(Vector3.Normalize(axis), MathF.Acos(dot));
    }

    private sealed class Retarget(FbxScene scene, FbxObject[] models, int[] parents, Dictionary<string, int> humanoid, FbxAnimation animation,
        PmxDocument target, LoadReport report)
    {
        // glTF の座標（正面が +Z）を MMD の座標（正面が -Z）にする: Z を裏返す
        private static readonly Matrix4x4 Mirror = Matrix4x4.CreateScale(1, 1, -1);

        public VmdDocument Build()
        {
            Matrix4x4 axis = FbxTransform.AxisConversion(scene, out _, out _);

            // 初めの姿勢（ノードに書かれた値）。後ろを向いていれば、前を向かせる
            Matrix4x4[] restLocals = [.. models.Select(m => FbxTransform.LocalMatrix(m))];
            ApplyRoot(restLocals, axis);
            Matrix4x4[] restWorlds = FbxTransform.Worlds(restLocals, parents);
            bool turned = FbxTransform.FacesBackward(humanoid, restWorlds);
            if (turned)
            {
                axis *= Matrix4x4.CreateRotationY(MathF.PI);
                restLocals = [.. models.Select(m => FbxTransform.LocalMatrix(m))];
                ApplyRoot(restLocals, axis);
                restWorlds = FbxTransform.Worlds(restLocals, parents);
            }

            // キャラクターのボーン（MMD の名前）。目は動かさない
            var targetOf = new Dictionary<string, int>(StringComparer.Ordinal);
            var missing = new List<string>();
            foreach (string bone in humanoid.Keys)
            {
                if (bone is "leftEye" or "rightEye" || VrmHumanoid.MmdName(bone, VrmVersion.None) is not { } name)
                {
                    continue;
                }
                int index = Array.FindIndex(target.Bones, b => b.Name == name);
                if (index >= 0)
                {
                    targetOf[bone] = index;
                }
                else
                {
                    missing.Add(name);
                }
            }
            if (!targetOf.ContainsKey("hips"))
            {
                throw new InvalidOperationException("キャラクターに「下半身」のボーンがありません（MMD の人型のモデルだけに当てはめられます）");
            }

            // 元の初めの姿勢（MMD の座標）と、キャラクターの初めの姿勢との向きの違いを埋める回転
            var restRotation = new Dictionary<string, Quaternion>();
            var restPosition = new Dictionary<string, Vector3>();
            foreach (var (bone, node) in humanoid)
            {
                (restRotation[bone], restPosition[bone]) = ToMmd(restWorlds[node]);
            }
            var correction = new Dictionary<string, Quaternion>();
            foreach (string bone in targetOf.Keys)
            {
                correction[bone] = Quaternion.Identity;
                if (!DirectionChild.TryGetValue(bone, out string[]? children))
                {
                    continue;
                }
                string? child = children.FirstOrDefault(c => targetOf.ContainsKey(c) && restPosition.ContainsKey(c));
                if (child is null)
                {
                    continue;
                }
                Vector3 targetDirection = target.Bones[targetOf[child]].Position - target.Bones[targetOf[bone]].Position;
                Vector3 sourceDirection = restPosition[child] - restPosition[bone];
                correction[bone] = FromTo(targetDirection, sourceDirection);
            }

            // 腰の位置の動きの大きさ: 腰の高さの比
            float sourceHips = restPosition["hips"].Y;
            float targetHips = target.Bones[targetOf["hips"]].Position.Y;
            float scale = sourceHips > 1e-3f && targetHips > 1e-3f ? targetHips / sourceHips : 1f;
            int center = Array.FindIndex(target.Bones, b => b.Name == "センター");

            // フレームごと
            int frames = (int)Math.Min(MaxFrames, Math.Floor((animation.Seconds * FrameRate) + 0.5) + 1);
            if (animation.Seconds * FrameRate + 1 > MaxFrames)
            {
                report.Info(LoadIssueCategory.Unsupported, $"長いモーションなので、初めの {MaxFrames / FrameRate / 60} 分だけ読みました");
            }
            var keys = new List<VmdBoneKey>(frames * (targetOf.Count + 1));
            var names = new Dictionary<int, VmdName>();
            VmdName NameOf(int bone) => names.TryGetValue(bone, out VmdName n) ? n : names[bone] = VmdName.FromText(target.Bones[bone].Name, VmdText.BoneNameBytes);
            var boneOfTarget = targetOf.ToDictionary(kv => kv.Value, kv => kv.Key);
            var locals = new Matrix4x4[models.Length];
            var global = new Quaternion?[target.Bones.Length];
            for (int f = 0; f < frames; f++)
            {
                double time = animation.Start + (f * FbxAnimation.TicksPerSecond / FrameRate);
                for (int i = 0; i < models.Length; i++)
                {
                    if (animation.IsAnimated(i))
                    {
                        var (t, r, s) = animation.Sample(i, time);
                        locals[i] = FbxTransform.LocalMatrix(models[i], t, r, s);
                        if (parents[i] < 0)
                        {
                            locals[i] *= axis;
                        }
                    }
                    else
                    {
                        locals[i] = restLocals[i];
                    }
                }
                Matrix4x4[] worlds = FbxTransform.Worlds(locals, parents);

                // キャラクターのボーンごとの、ワールドでの向きの変化（人型でないボーンは親と同じ）
                Array.Clear(global);
                Quaternion Global(int bone, int depth)
                {
                    if (global[bone] is { } known)
                    {
                        return known;
                    }
                    Quaternion value;
                    if (boneOfTarget.TryGetValue(bone, out string? human))
                    {
                        Quaternion now = ToMmd(worlds[humanoid[human]]).Rotation;
                        value = Quaternion.Normalize(now * Quaternion.Conjugate(restRotation[human]) * correction[human]);
                    }
                    else
                    {
                        int parent = target.Bones[bone].ParentIndex;
                        value = parent >= 0 && parent < target.Bones.Length && depth < target.Bones.Length ? Global(parent, depth + 1) : Quaternion.Identity;
                    }
                    global[bone] = value;
                    return value;
                }
                foreach (var (human, bone) in targetOf)
                {
                    int parent = target.Bones[bone].ParentIndex;
                    Quaternion parentGlobal = parent >= 0 && parent < target.Bones.Length ? Global(parent, 0) : Quaternion.Identity;
                    Quaternion local = Quaternion.Normalize(Quaternion.Conjugate(parentGlobal) * Global(bone, 0));
                    keys.Add(new VmdBoneKey(NameOf(bone), f, Vector3.Zero, local, LinearInterpolation));
                }
                if (center >= 0)
                {
                    Vector3 moved = (ToMmd(worlds[humanoid["hips"]]).Position - restPosition["hips"]) * scale;
                    keys.Add(new VmdBoneKey(NameOf(center), f, moved, Quaternion.Identity, LinearInterpolation));
                }
            }

            // 足などの IK を切る（元の動きをそのまま使う）
            var ikOff = new List<VmdIkState>();
            var mapped = new HashSet<int>(targetOf.Values);
            foreach (PmxBone bone in target.Bones.Where(b => b.Has(PmxBoneFlags.IK)))
            {
                if (bone.IkLinks.Any(l => mapped.Contains(l.Bone)) || mapped.Contains(bone.IkTarget))
                {
                    ikOff.Add(new VmdIkState(VmdName.FromText(bone.Name, VmdText.IkNameBytes), false));
                }
            }
            VmdVisibilityKey[] visibility = ikOff.Count > 0 ? [new VmdVisibilityKey(0, true, ikOff)] : [];

            report.Info(LoadIssueCategory.Note,
                FormattableString.CurrentCulture($"FBX のモーション（テイク「{animation.Name}」・{animation.Seconds:0.##} 秒）を、人型のボーン {targetOf.Count} 本に当てはめて、") +
                $"30 fps の {frames:N0} フレームにしました（腰の動きはセンターに。元の初めの姿勢との違いは、ボーンの向きで合わせました）");
            if (turned)
            {
                report.Info(LoadIssueCategory.Note, "左右のボーンの位置から後ろを向いていると分かったので、半回転させて当てはめました");
            }
            if (ikOff.Count > 0)
            {
                report.Info(LoadIssueCategory.Note, $"元の足の動きをそのまま使うため、IK（{string.Join("・", ikOff.Select(s => s.Bone.Text))}）を切りました");
            }
            if (center < 0)
            {
                report.Info(LoadIssueCategory.Unsupported, "キャラクターに「センター」のボーンが無いので、腰の位置の動きは使いません");
            }
            if (missing.Count > 0)
            {
                report.Info(LoadIssueCategory.Note, $"キャラクターに無いため使わないボーン: {string.Join("・", missing)}");
            }
            if (animation.StackCount > 1 || animation.LayerCount > 1)
            {
                report.Info(LoadIssueCategory.Unsupported,
                    $"FBX にテイクが {animation.StackCount} 個・レイヤーが {animation.LayerCount} 個あります。動くものの 1 つ目（テイク「{animation.Name}」の 1 つ目のレイヤー）だけ使います");
            }
            return new VmdDocument { BoneKeys = keys, VisibilityKeys = visibility };
        }

        private void ApplyRoot(Matrix4x4[] locals, Matrix4x4 axis)
        {
            for (int i = 0; i < locals.Length; i++)
            {
                if (parents[i] < 0)
                {
                    locals[i] *= axis;
                }
            }
        }

        // glTF の座標のワールドの行列から、MMD の座標の向きと位置（MMD の長さ）
        private static (Quaternion Rotation, Vector3 Position) ToMmd(Matrix4x4 world)
        {
            Matrix4x4 m = Mirror * world * Mirror;
            Vector3 x = Vector3.Normalize(new Vector3(m.M11, m.M12, m.M13));
            Vector3 y = new(m.M21, m.M22, m.M23);
            y = Vector3.Normalize(y - (Vector3.Dot(y, x) * x));
            Vector3 z = Vector3.Cross(x, y);
            var rotation = new Matrix4x4(x.X, x.Y, x.Z, 0, y.X, y.Y, y.Z, 0, z.X, z.Y, z.Z, 0, 0, 0, 0, 1);
            return (Quaternion.Normalize(Quaternion.CreateFromRotationMatrix(rotation)), m.Translation * GltfConverter.MetersToMmd);
        }
    }
}
