using System.Numerics;
using System.Runtime.InteropServices;
using Theater3D.Formats.Pmx;

namespace Theater3D.Animation;

/// <summary>変形した後の頂点（描画エンジンの頂点と同じ並び: 位置・法線・UV）。</summary>
[StructLayout(LayoutKind.Sequential)]
public struct SkinnedVertex
{
    public Vector3 Position;
    public Vector3 Normal;
    public Vector2 Uv;
}

/// <summary>
/// 頂点をボーンに合わせて曲げる（スキニング）。BDEF1・BDEF2・BDEF4 は重みで混ぜた行列で、SDEF は球面の補間で曲げる。
/// QDEF（PMX 2.1）は今は BDEF4 として扱う。頂点モーフと UV モーフの分は先に足す。
/// 頂点が多いときは複数の CPU で分けて計算する。
/// </summary>
public static class Skinner
{
    private const int ParallelThreshold = 20_000;
    private const int ChunkSize = 8_192;

    public static void Apply(ModelRig rig, PoseState state, Span<SkinnedVertex> output, out Vector3 boundsMin, out Vector3 boundsMax)
    {
        int count = rig.RestPositions.Length;
        if (output.Length < count)
        {
            throw new ArgumentException("出力の頂点の数が足りません", nameof(output));
        }
        if (count == 0)
        {
            boundsMin = boundsMax = Vector3.Zero;
            return;
        }

        if (count < ParallelThreshold)
        {
            SkinRange(rig, state, output[..count], 0, out boundsMin, out boundsMax);
            return;
        }

        // Span はスレッドをまたげないので、配列の上で分けて計算してから写す
        var buffer = new SkinnedVertex[count];
        Apply(rig, state, buffer, out boundsMin, out boundsMax);
        buffer.AsSpan().CopyTo(output);
    }

    /// <summary>出力先が配列のとき（画面に出すとき）はこちらを使う。頂点が多ければ複数の CPU で分けて計算する。</summary>
    public static void Apply(ModelRig rig, PoseState state, SkinnedVertex[] output, out Vector3 boundsMin, out Vector3 boundsMax)
    {
        int count = rig.RestPositions.Length;
        if (output.Length < count)
        {
            throw new ArgumentException("出力の頂点の数が足りません", nameof(output));
        }
        if (count == 0)
        {
            boundsMin = boundsMax = Vector3.Zero;
            return;
        }
        if (count < ParallelThreshold)
        {
            SkinRange(rig, state, output.AsSpan(0, count), 0, out boundsMin, out boundsMax);
            return;
        }
        int chunks = (count + ChunkSize - 1) / ChunkSize;
        var mins = new Vector3[chunks];
        var maxs = new Vector3[chunks];
        Parallel.For(0, chunks, chunk =>
        {
            int start = chunk * ChunkSize;
            int length = Math.Min(ChunkSize, count - start);
            SkinRange(rig, state, output.AsSpan(start, length), start, out mins[chunk], out maxs[chunk]);
        });
        boundsMin = mins[0];
        boundsMax = maxs[0];
        for (int i = 1; i < chunks; i++)
        {
            boundsMin = Vector3.Min(boundsMin, mins[i]);
            boundsMax = Vector3.Max(boundsMax, maxs[i]);
        }
    }

    private static void SkinRange(ModelRig rig, PoseState state, Span<SkinnedVertex> output, int start, out Vector3 min, out Vector3 max)
    {
        min = new Vector3(float.MaxValue);
        max = new Vector3(float.MinValue);
        Matrix4x4[] skin = state.Skinning;
        for (int k = 0; k < output.Length; k++)
        {
            int v = start + k;
            ref readonly SkinWeight s = ref rig.Skin[v];
            Vector3 position = rig.RestPositions[v] + state.VertexOffsets[v];
            Vector3 normal = rig.RestNormals[v];
            Vector3 p;
            Vector3 n;

            switch (s.Type)
            {
                case PmxWeightType.Bdef1:
                    if (s.Bone0 >= 0)
                    {
                        p = Vector3.Transform(position, skin[s.Bone0]);
                        n = Vector3.TransformNormal(normal, skin[s.Bone0]);
                    }
                    else
                    {
                        (p, n) = (position, normal);
                    }
                    break;
                case PmxWeightType.Sdef:
                {
                    // 2 本のボーンの回転を球面で補間し、回転の中心（C）まわりに回す
                    float w0 = s.Weights.X, w1 = s.Weights.Y;
                    Quaternion rotation = Quaternion.Slerp(state.GlobalRotation[s.Bone0], state.GlobalRotation[s.Bone1], w1);
                    p = Vector3.Transform(position - s.SdefC, rotation)
                        + (Vector3.Transform(s.SdefCr0, skin[s.Bone0]) * w0)
                        + (Vector3.Transform(s.SdefCr1, skin[s.Bone1]) * w1);
                    n = Vector3.Transform(normal, rotation);
                    break;
                }
                default:
                {
                    Matrix4x4 blended = default;
                    float total = 0;
                    Accumulate(ref blended, ref total, s.Bone0, s.Weights.X, skin);
                    Accumulate(ref blended, ref total, s.Bone1, s.Weights.Y, skin);
                    if (s.Type != PmxWeightType.Bdef2)
                    {
                        Accumulate(ref blended, ref total, s.Bone2, s.Weights.Z, skin);
                        Accumulate(ref blended, ref total, s.Bone3, s.Weights.W, skin);
                    }
                    if (total > 1e-6f)
                    {
                        if (MathF.Abs(total - 1f) > 1e-5f)
                        {
                            blended *= 1f / total; // 欠けたボーンの分は残りのボーンへ割り振る
                        }
                        p = Vector3.Transform(position, blended);
                        n = Vector3.TransformNormal(normal, blended);
                    }
                    else
                    {
                        (p, n) = (position, normal);
                    }
                    break;
                }
            }

            float lengthSquared = n.LengthSquared();
            output[k] = new SkinnedVertex
            {
                Position = p,
                Normal = lengthSquared > 1e-12f ? n / MathF.Sqrt(lengthSquared) : Vector3.UnitY,
                Uv = rig.RestUvs[v] + state.UvOffsets[v],
            };
            min = Vector3.Min(min, p);
            max = Vector3.Max(max, p);
        }
    }

    private static void Accumulate(ref Matrix4x4 blended, ref float total, int bone, float weight, Matrix4x4[] skin)
    {
        if (bone < 0 || weight == 0)
        {
            return;
        }
        blended += skin[bone] * weight;
        total += weight;
    }
}
