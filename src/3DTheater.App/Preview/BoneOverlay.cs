using System.Numerics;
using Theater3D.Animation;
using Theater3D.Domain;
using Theater3D.Engine.Interop;

namespace Theater3D.App.Preview;

/// <summary>プレビューに線で重ねるボーン 1 本（ワールドの座標で）。</summary>
internal readonly record struct DisplayedBone(BoneNode Node, Vector3 Head, Vector3 Tail, bool HasLength);

/// <summary>
/// 今操作しているモデル（<see cref="EditorContext.ActiveModel"/>）のボーンを、モデルより手前に線で描く。
/// 描くのはプレビューだけで、書き出しには映らない（指示書 5 章）。MMD で非表示のボーンと、シーン一覧で隠したボーンは描かない。
/// 長さの無いボーン（先端が根元と同じ）は小さな十字で描く。
/// </summary>
internal sealed class BoneOverlay
{
    private static readonly Vector4 NormalColor = new(0.55f, 0.75f, 1f, 1f);
    private static readonly Vector4 TranslatableColor = new(0.45f, 0.9f, 0.55f, 1f);
    private static readonly Vector4 IkColor = new(1f, 0.55f, 0.15f, 1f);
    private static readonly Vector4 FixedColor = new(0.62f, 0.62f, 0.66f, 0.9f);
    private static readonly Vector4 SelectedColor = new(1f, 0.9f, 0.25f, 1f);

    private LineVertex[] _lines = new LineVertex[512];
    private int _count;

    /// <summary>描くボーンを並べる（選択の当たり判定にも使う）。</summary>
    public static IEnumerable<DisplayedBone> Enumerate(EditorContext context)
    {
        ModelNode? model = context.ActiveModel;
        if (model is null || !model.IsEffectivelyVisible || context.GetLoaded(model) is not { } loaded)
        {
            yield break;
        }
        Matrix4x4 world = model.GetLocalMatrix();
        float minLength = MarkerSize(loaded) * 0.25f;
        foreach (BoneNode bone in model.Bones)
        {
            if (!bone.Traits.IsShown || !bone.IsEffectivelyVisible)
            {
                continue;
            }
            Vector3 head = Vector3.Transform(loaded.State.BonePosition(bone.Index), world);
            Vector3 tail = Vector3.Transform(PoseEvaluator.TailPosition(loaded.Rig, loaded.State, bone.Index), world);
            yield return new DisplayedBone(bone, head, tail, Vector3.Distance(head, tail) > minLength);
        }
    }

    /// <summary>十字の大きさ（モデルの大きさに合わせる）。</summary>
    public static float MarkerSize(LoadedModel model) => Math.Max(0.002f, (model.BoundsMax - model.BoundsMin).Length() * 0.006f);

    public ReadOnlyMemory<LineVertex> Build(EditorContext context)
    {
        _count = 0;
        LoadedModel? loaded = context.GetLoaded(context.ActiveModel);
        if (loaded is null)
        {
            return ReadOnlyMemory<LineVertex>.Empty;
        }
        float size = MarkerSize(loaded);
        foreach (DisplayedBone bone in Enumerate(context))
        {
            Vector4 color = ColorOf(bone.Node, context.Selection.Contains(bone.Node));
            if (bone.HasLength)
            {
                Add(bone.Head, bone.Tail, color);
            }
            if (!bone.HasLength || context.Selection.Contains(bone.Node))
            {
                // 根元の印（長さの無いボーンと、選んだボーン）
                float s = bone.HasLength ? size : size * 1.5f;
                Add(bone.Head - new Vector3(s, 0, 0), bone.Head + new Vector3(s, 0, 0), color);
                Add(bone.Head - new Vector3(0, s, 0), bone.Head + new Vector3(0, s, 0), color);
                Add(bone.Head - new Vector3(0, 0, s), bone.Head + new Vector3(0, 0, s), color);
            }
        }
        return _lines.AsMemory(0, _count);
    }

    private static Vector4 ColorOf(BoneNode bone, bool selected)
    {
        if (selected)
        {
            return SelectedColor;
        }
        BoneTraits t = bone.Traits;
        if (t.IsIk)
        {
            return IkColor;
        }
        if (t.CanTranslate)
        {
            return TranslatableColor;
        }
        return t.CanRotate ? NormalColor : FixedColor;
    }

    private void Add(Vector3 a, Vector3 b, Vector4 color)
    {
        if (_count + 2 > _lines.Length)
        {
            Array.Resize(ref _lines, _lines.Length * 2);
        }
        _lines[_count++] = new LineVertex(a, color);
        _lines[_count++] = new LineVertex(b, color);
    }
}
