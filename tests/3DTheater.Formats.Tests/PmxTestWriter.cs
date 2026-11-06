using System.IO;
using System.Numerics;
using System.Text;
using Theater3D.Formats.Pmx;

namespace Theater3D.Formats.Tests;

/// <summary>
/// テスト用に <see cref="PmxDocument"/> を PMX のバイト列にする（製品には PMX の書き出しは無い。元ファイルを書き換えないため）。
/// 読み込み側と同じ思い込みで間違えないよう、PMX の仕様の並びをそのまま 1 つずつ書く。
/// </summary>
internal sealed class PmxTestWriter
{
    private readonly MemoryStream _stream = new();
    private readonly BinaryWriter _w;
    private readonly PmxDocument _d;
    private readonly Encoding _encoding;

    private PmxTestWriter(PmxDocument document)
    {
        _d = document;
        _w = new BinaryWriter(_stream);
        _encoding = document.Encoding == PmxTextEncoding.Utf16 ? Encoding.Unicode : Encoding.UTF8;
    }

    /// <param name="stopAfterMorphs">true なら表示枠から後ろを書かない（古い書き出し器の再現）。</param>
    /// <param name="trailer">最後に足すバイト列（2.1 のソフトボディなど）。</param>
    public static byte[] Write(PmxDocument document, bool stopAfterMorphs = false, byte[]? trailer = null)
    {
        var writer = new PmxTestWriter(document);
        writer.WriteAll(stopAfterMorphs);
        if (trailer is not null)
        {
            writer._w.Write(trailer);
        }
        writer._w.Flush();
        return writer._stream.ToArray();
    }

    private void WriteAll(bool stopAfterMorphs)
    {
        PmxIndexSizes s = _d.IndexSizes;
        _w.Write("PMX "u8);
        _w.Write(_d.Version);
        _w.Write((byte)8);
        _w.Write([(byte)_d.Encoding, (byte)_d.AdditionalUvCount, s.Vertex, s.Texture, s.Material, s.Bone, s.Morph, s.RigidBody]);
        Text(_d.Name);
        Text(_d.NameUniversal);
        Text(_d.Comment);
        Text(_d.CommentUniversal);

        _w.Write(_d.Vertices.Length);
        for (int i = 0; i < _d.Vertices.Length; i++)
        {
            PmxVertex v = _d.Vertices[i];
            V3(v.Position);
            V3(v.Normal);
            _w.Write(v.Uv.X);
            _w.Write(v.Uv.Y);
            for (int j = 0; j < _d.AdditionalUvCount; j++)
            {
                V4(_d.AdditionalUvs[(i * _d.AdditionalUvCount) + j]);
            }
            _w.Write((byte)v.WeightType);
            switch (v.WeightType)
            {
                case PmxWeightType.Bdef1:
                    Index(v.Bone1, s.Bone);
                    break;
                case PmxWeightType.Bdef2:
                    Index(v.Bone1, s.Bone);
                    Index(v.Bone2, s.Bone);
                    _w.Write(v.Weights.X);
                    break;
                case PmxWeightType.Sdef:
                    Index(v.Bone1, s.Bone);
                    Index(v.Bone2, s.Bone);
                    _w.Write(v.Weights.X);
                    V3(v.SdefC);
                    V3(v.SdefR0);
                    V3(v.SdefR1);
                    break;
                default:
                    Index(v.Bone1, s.Bone);
                    Index(v.Bone2, s.Bone);
                    Index(v.Bone3, s.Bone);
                    Index(v.Bone4, s.Bone);
                    V4(v.Weights);
                    break;
            }
            _w.Write(v.EdgeScale);
        }

        _w.Write(_d.Indices.Length);
        foreach (int index in _d.Indices)
        {
            VertexIndex(index, s.Vertex);
        }

        _w.Write(_d.Textures.Length);
        foreach (string texture in _d.Textures)
        {
            Text(texture);
        }

        _w.Write(_d.Materials.Length);
        foreach (PmxMaterial m in _d.Materials)
        {
            Text(m.Name);
            Text(m.NameUniversal);
            V4(m.Diffuse);
            V3(m.Specular);
            _w.Write(m.Specularity);
            V3(m.Ambient);
            _w.Write((byte)m.Flags);
            V4(m.EdgeColor);
            _w.Write(m.EdgeSize);
            Index(m.TextureIndex, s.Texture);
            Index(m.SphereTextureIndex, s.Texture);
            _w.Write((byte)m.SphereMode);
            _w.Write(m.SharedToon ? (byte)1 : (byte)0);
            if (m.SharedToon)
            {
                _w.Write((byte)m.ToonIndex);
            }
            else
            {
                Index(m.ToonIndex, s.Texture);
            }
            Text(m.Memo);
            _w.Write(m.IndexCount);
        }

        _w.Write(_d.Bones.Length);
        foreach (PmxBone b in _d.Bones)
        {
            Text(b.Name);
            Text(b.NameUniversal);
            V3(b.Position);
            Index(b.ParentIndex, s.Bone);
            _w.Write(b.Layer);
            _w.Write((ushort)b.Flags);
            if (b.Has(PmxBoneFlags.TailIsBone))
            {
                Index(b.TailBone, s.Bone);
            }
            else
            {
                V3(b.TailOffset);
            }
            if ((b.Flags & (PmxBoneFlags.InheritRotation | PmxBoneFlags.InheritTranslation)) != 0)
            {
                Index(b.InheritParent, s.Bone);
                _w.Write(b.InheritWeight);
            }
            if (b.Has(PmxBoneFlags.FixedAxis))
            {
                V3(b.FixedAxis);
            }
            if (b.Has(PmxBoneFlags.LocalAxis))
            {
                V3(b.LocalAxisX);
                V3(b.LocalAxisZ);
            }
            if (b.Has(PmxBoneFlags.ExternalParent))
            {
                _w.Write(b.ExternalParentKey);
            }
            if (b.Has(PmxBoneFlags.IK))
            {
                Index(b.IkTarget, s.Bone);
                _w.Write(b.IkLoopCount);
                _w.Write(b.IkLimitAngle);
                _w.Write(b.IkLinks.Count);
                foreach (PmxIkLink link in b.IkLinks)
                {
                    Index(link.Bone, s.Bone);
                    _w.Write(link.HasLimits ? (byte)1 : (byte)0);
                    if (link.HasLimits)
                    {
                        V3(link.MinAngle);
                        V3(link.MaxAngle);
                    }
                }
            }
        }

        _w.Write(_d.Morphs.Length);
        foreach (PmxMorph m in _d.Morphs)
        {
            Text(m.Name);
            Text(m.NameUniversal);
            _w.Write((byte)m.Panel);
            _w.Write((byte)m.Type);
            _w.Write(m.OffsetCount);
            foreach (PmxGroupOffset o in m.GroupOffsets)
            {
                Index(o.Morph, s.Morph);
                _w.Write(o.Weight);
            }
            foreach (PmxVertexOffset o in m.VertexOffsets)
            {
                VertexIndex(o.Vertex, s.Vertex);
                V3(o.Translation);
            }
            foreach (PmxBoneOffset o in m.BoneOffsets)
            {
                Index(o.Bone, s.Bone);
                V3(o.Translation);
                V4(new Vector4(o.Rotation.X, o.Rotation.Y, o.Rotation.Z, o.Rotation.W));
            }
            foreach (PmxUvOffset o in m.UvOffsets)
            {
                VertexIndex(o.Vertex, s.Vertex);
                V4(o.Value);
            }
            foreach (PmxMaterialOffset o in m.MaterialOffsets)
            {
                Index(o.Material, s.Material);
                _w.Write((byte)o.Operation);
                V4(o.Diffuse);
                V3(o.Specular);
                _w.Write(o.Specularity);
                V3(o.Ambient);
                V4(o.EdgeColor);
                _w.Write(o.EdgeSize);
                V4(o.TextureTint);
                V4(o.SphereTint);
                V4(o.ToonTint);
            }
            foreach (PmxImpulseOffset o in m.ImpulseOffsets)
            {
                Index(o.RigidBody, s.RigidBody);
                _w.Write(o.Local ? (byte)1 : (byte)0);
                V3(o.Velocity);
                V3(o.Torque);
            }
        }
        if (stopAfterMorphs)
        {
            return;
        }

        _w.Write(_d.DisplayFrames.Length);
        foreach (PmxDisplayFrame f in _d.DisplayFrames)
        {
            Text(f.Name);
            Text(f.NameUniversal);
            _w.Write(f.IsSpecial ? (byte)1 : (byte)0);
            _w.Write(f.Elements.Count);
            foreach (PmxDisplayElement e in f.Elements)
            {
                _w.Write((byte)e.Target);
                Index(e.Index, e.Target == PmxDisplayTarget.Bone ? s.Bone : s.Morph);
            }
        }

        _w.Write(_d.RigidBodies.Length);
        foreach (PmxRigidBody r in _d.RigidBodies)
        {
            Text(r.Name);
            Text(r.NameUniversal);
            Index(r.BoneIndex, s.Bone);
            _w.Write(r.Group);
            _w.Write(r.NoCollisionMask);
            _w.Write((byte)r.Shape);
            V3(r.Size);
            V3(r.Position);
            V3(r.Rotation);
            _w.Write(r.Mass);
            _w.Write(r.LinearDamping);
            _w.Write(r.AngularDamping);
            _w.Write(r.Restitution);
            _w.Write(r.Friction);
            _w.Write((byte)r.Mode);
        }

        _w.Write(_d.Joints.Length);
        foreach (PmxJoint j in _d.Joints)
        {
            Text(j.Name);
            Text(j.NameUniversal);
            _w.Write((byte)j.Type);
            Index(j.RigidBodyA, s.RigidBody);
            Index(j.RigidBodyB, s.RigidBody);
            V3(j.Position);
            V3(j.Rotation);
            V3(j.PositionMin);
            V3(j.PositionMax);
            V3(j.RotationMin);
            V3(j.RotationMax);
            V3(j.PositionSpring);
            V3(j.RotationSpring);
        }
    }

    private void Text(string text)
    {
        byte[] bytes = _encoding.GetBytes(text);
        _w.Write(bytes.Length);
        _w.Write(bytes);
    }

    private void V3(Vector3 v)
    {
        _w.Write(v.X);
        _w.Write(v.Y);
        _w.Write(v.Z);
    }

    private void V4(Vector4 v)
    {
        _w.Write(v.X);
        _w.Write(v.Y);
        _w.Write(v.Z);
        _w.Write(v.W);
    }

    // 頂点以外の番号は符号付き（-1 =「なし」）
    private void Index(int value, byte size)
    {
        switch (size)
        {
            case 1:
                _w.Write((sbyte)value);
                break;
            case 2:
                _w.Write((short)value);
                break;
            default:
                _w.Write(value);
                break;
        }
    }

    // 頂点の番号は 1・2 バイトのとき符号なし、4 バイトのとき符号付き
    private void VertexIndex(int value, byte size)
    {
        switch (size)
        {
            case 1:
                _w.Write((byte)value);
                break;
            case 2:
                _w.Write((ushort)value);
                break;
            default:
                _w.Write(value);
                break;
        }
    }
}
