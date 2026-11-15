using System.Numerics;

namespace Theater3D.Formats.Fbx;

/// <summary>
/// FBX のノードの位置・向き・大きさと、ファイルの軸・長さの決まり（設計判断記録 83 章）。形の読み込み（<see cref="FbxConverter"/>）と、
/// モーションの読み込み（<see cref="FbxMotionReader"/>。85 章）で使う。行列は行ベクトル（System.Numerics）。
/// </summary>
internal static class FbxTransform
{
    /// <summary>
    /// ファイルの軸（右・上・正面）を glTF の X・Y・Z（上が +Y・正面が +Z・右手系）に並べ替え、単位（UnitScaleFactor。1 = 1 cm が初め）を m にする行列。
    /// 左手系と書かれたファイルは、右の軸を逆にして右手系にそろえる（<paramref name="flippedHandedness"/>）。
    /// </summary>
    public static Matrix4x4 AxisConversion(FbxScene scene, out string text, out bool flippedHandedness)
    {
        FbxObject? s = scene.Settings;
        int up = (int)(s?.Number("UpAxis", 1) ?? 1);
        int front = (int)(s?.Number("FrontAxis", 2) ?? 2);
        int coord = (int)(s?.Number("CoordAxis", 0) ?? 0);
        float upSign = Sign(s?.Number("UpAxisSign", 1) ?? 1);
        float frontSign = Sign(s?.Number("FrontAxisSign", 1) ?? 1);
        float coordSign = Sign(s?.Number("CoordAxisSign", 1) ?? 1);
        if (up is < 0 or > 2 || front is < 0 or > 2 || coord is < 0 or > 2 || up == front || up == coord || front == coord)
        {
            (up, front, coord, upSign, frontSign, coordSign) = (1, 2, 0, 1, 1, 1);
        }
        double unit = s?.Number("UnitScaleFactor", 1) ?? 1;
        if (!(unit > 1e-9 && unit < 1e9))
        {
            unit = 1;
        }
        var m = new Matrix4x4 { M44 = 1 };
        m[coord, 0] = coordSign;
        m[up, 1] = upSign;
        m[front, 2] = frontSign;
        flippedHandedness = false;
        if (m.GetDeterminant() < 0)
        {
            m[coord, 0] = -coordSign;
            flippedHandedness = true;
        }
        string[] names = ["X", "Y", "Z"];
        text = FormattableString.Invariant($"上が {(upSign < 0 ? "-" : "+")}{names[up]}・正面が {(frontSign < 0 ? "-" : "+")}{names[front]}・長さの 1 は {unit:0.###} cm");
        return m * Matrix4x4.CreateScale((float)(unit / 100.0));

        static float Sign(double v) => v < 0 ? -1f : 1f;
    }

    /// <summary>
    /// ノードの行列（親から見た）。FBX の決まりの順（列ベクトルで書くと T × Roff × Rp × Rpre × R × Rpost⁻¹ × Rp⁻¹ × Soff × Sp × S × Sp⁻¹）。
    /// 移動・回転・大きさを渡せば、それを使う（モーションの 1 フレーム分）。無ければ、ノードに書かれた値。
    /// </summary>
    public static Matrix4x4 LocalMatrix(FbxObject m, Vector3? translation = null, Vector3? rotation = null, Vector3? scaling = null)
    {
        Vector3 t = translation ?? m.Vector("Lcl Translation", Vector3.Zero);
        Vector3 r = rotation ?? m.Vector("Lcl Rotation", Vector3.Zero);
        Vector3 s = scaling ?? m.Vector("Lcl Scaling", Vector3.One);
        Vector3 pre = m.Vector("PreRotation", Vector3.Zero);
        Vector3 post = m.Vector("PostRotation", Vector3.Zero);
        Vector3 rotationOffset = m.Vector("RotationOffset", Vector3.Zero);
        Vector3 rotationPivot = m.Vector("RotationPivot", Vector3.Zero);
        Vector3 scalingOffset = m.Vector("ScalingOffset", Vector3.Zero);
        Vector3 scalingPivot = m.Vector("ScalingPivot", Vector3.Zero);
        int order = (int)m.Number("RotationOrder", 0);
        Matrix4x4.Invert(Euler(post, 0), out Matrix4x4 postInverse);
        return Matrix4x4.CreateTranslation(-scalingPivot) * Matrix4x4.CreateScale(s) * Matrix4x4.CreateTranslation(scalingPivot)
            * Matrix4x4.CreateTranslation(scalingOffset) * Matrix4x4.CreateTranslation(-rotationPivot)
            * postInverse * Euler(r, order) * Euler(pre, 0)
            * Matrix4x4.CreateTranslation(rotationPivot) * Matrix4x4.CreateTranslation(rotationOffset) * Matrix4x4.CreateTranslation(t);
    }

    /// <summary>形だけに掛かる位置・向き・大きさ（Geometric〜。子のノードには掛からない）。</summary>
    public static Matrix4x4 GeometricMatrix(FbxObject m) =>
        Matrix4x4.CreateScale(m.Vector("GeometricScaling", Vector3.One)) * Euler(m.Vector("GeometricRotation", Vector3.Zero), 0)
        * Matrix4x4.CreateTranslation(m.Vector("GeometricTranslation", Vector3.Zero));

    /// <summary>
    /// 回転（度）の行列。順の番号は FBX の RotationOrder（0 = XYZ: X を先に回す、1 = XZY、2 = YZX、3 = YXZ、4 = ZXY、5 = ZYX）。
    /// </summary>
    public static Matrix4x4 Euler(Vector3 degrees, int order)
    {
        Matrix4x4 x = Matrix4x4.CreateRotationX(degrees.X * MathF.PI / 180f);
        Matrix4x4 y = Matrix4x4.CreateRotationY(degrees.Y * MathF.PI / 180f);
        Matrix4x4 z = Matrix4x4.CreateRotationZ(degrees.Z * MathF.PI / 180f);
        return order switch
        {
            1 => x * z * y,
            2 => y * z * x,
            3 => y * x * z,
            4 => z * x * y,
            5 => z * y * x,
            _ => x * y * z,
        };
    }

    /// <summary>ノード（Model）ごとの親の番号（つながっている 1 つ目の Model。無いか、輪になっていれば -1）。</summary>
    public static int[] Parents(IReadOnlyList<FbxObject> models)
    {
        var index = new Dictionary<long, int>();
        for (int i = 0; i < models.Count; i++)
        {
            index.TryAdd(models[i].Id, i);
        }
        var parents = new int[models.Count];
        for (int i = 0; i < models.Count; i++)
        {
            FbxObject? parent = models[i].Parents.Find(p => p.Kind == "Model");
            parents[i] = parent is not null && index.TryGetValue(parent.Id, out int p) ? p : -1;
            var seen = new HashSet<long> { models[i].Id };
            for (FbxObject? q = parent; q is not null && parents[i] >= 0; q = q.Parents.Find(x => x.Kind == "Model"))
            {
                if (!seen.Add(q.Id))
                {
                    parents[i] = -1;
                }
            }
        }
        return parents;
    }

    /// <summary>親から見た行列から、ワールドの行列（親が先でなくてもよい）。</summary>
    public static Matrix4x4[] Worlds(Matrix4x4[] locals, int[] parents)
    {
        var worlds = new Matrix4x4[locals.Length];
        var done = new bool[locals.Length];
        Matrix4x4 World(int i, int depth)
        {
            if (done[i])
            {
                return worlds[i];
            }
            worlds[i] = parents[i] >= 0 && depth < locals.Length ? locals[i] * World(parents[i], depth + 1) : locals[i];
            done[i] = true;
            return worlds[i];
        }
        for (int i = 0; i < locals.Length; i++)
        {
            World(i, 0);
        }
        return worlds;
    }

    /// <summary>glTF と同じく正面が +Z なら、モデルの左（左腕・左足）は +X にある。逆なら後ろを向いている（人型のボーンの左右で決める）。</summary>
    public static bool FacesBackward(IReadOnlyDictionary<string, int> humanoid, Matrix4x4[] worlds)
    {
        foreach (string part in new[] { "UpperArm", "Shoulder", "Hand", "UpperLeg", "Foot" })
        {
            if (humanoid.TryGetValue("left" + part, out int left) && humanoid.TryGetValue("right" + part, out int right))
            {
                float difference = worlds[left].Translation.X - worlds[right].Translation.X;
                if (MathF.Abs(difference) > 1e-4f)
                {
                    return difference < 0;
                }
            }
        }
        return false;
    }
}
