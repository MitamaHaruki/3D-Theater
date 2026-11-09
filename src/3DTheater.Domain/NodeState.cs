using System.Numerics;

namespace Theater3D.Domain;

/// <summary>
/// 項目の編集できる値の写し（元に戻す・やり直しに使う）。種類ごとの値は <see cref="Extra"/> に入れる。
/// 同じ値の写しどうしは等しい（変わったかどうかの判定に使う）。
/// </summary>
public sealed record NodeState(string Name, bool IsVisible, bool IsLocked, Vector3 Position, Vector3 Rotation, Vector3 Scale, object? Extra = null);

public sealed record CameraState(float FieldOfView, float NearClip, float FarClip, float TargetDistance, bool HasUnregisteredChange, KeySnapshot<CameraKey>? Keys);

public sealed record LightState(float Intensity);

public sealed record TestObjectState(bool SpinWithFrame);

public sealed record AudioState(int StartFrame, float Volume);

/// <summary>ボーンの値。<see cref="Keys"/> はキーも写したときだけ入る（値だけの編集ではキーを写さない）。</summary>
public sealed record BoneState(Quaternion Orientation, bool HasUnregisteredChange, KeySnapshot<BoneKey>? Keys);

public sealed record MorphState(float Weight, bool HasUnregisteredChange, KeySnapshot<MorphKey>? Keys);

/// <summary>キーの並びの写し。中身が同じなら等しい。</summary>
public sealed class KeySnapshot<TKey>(TKey[] keys) : IEquatable<KeySnapshot<TKey>> where TKey : struct, IKeyframe
{
    public IReadOnlyList<TKey> Keys { get; } = keys;

    public bool Equals(KeySnapshot<TKey>? other) => other is not null && Keys.SequenceEqual(other.Keys);

    public override bool Equals(object? obj) => Equals(obj as KeySnapshot<TKey>);

    public override int GetHashCode() => Keys.Count;
}
