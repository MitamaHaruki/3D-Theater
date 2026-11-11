using System.Diagnostics;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Theater3D.App.Logging;
using Theater3D.Animation;
using Theater3D.Domain;
using Theater3D.Editor;
using Theater3D.Engine.Interop;
using Theater3D.Physics;

namespace Theater3D.App.Rendering;

/// <summary>
/// ボーンの移動・回転とモーフの値が変わったモデルを、次の描画の前に変形し直して GPU へ送る。
/// 1 回の描画の前にまとめて 1 度だけ計算するので、値を続けて変えても無駄な計算をしない。
/// 頂点の変形は CPU で行う（選択の当たり判定とテストで同じ計算を使うため。docs/設計判断記録.md 参照）。
/// 物理演算が ON なら、物理の前まで計算 → 物理（<see cref="PhysicsController.Current"/> の進め方で）→ 物理の後、の順に求める。
/// </summary>
public sealed class PoseUpdater
{
    private readonly EditorContext _context;
    private readonly FrameTimer _timer = new();

    static PoseUpdater()
    {
        // 変形した頂点をそのまま描画エンジンへ渡すため、並びが同じであること
        Debug.Assert(Unsafe.SizeOf<SkinnedVertex>() == Unsafe.SizeOf<MeshVertex>());
    }

    internal PoseUpdater(EditorContext context)
    {
        _context = context;
        context.Scene.NodeChanged += OnNodeChanged;
        context.RenderLoop.BeforeRender += (_, _) => UpdateDirtyModels();
    }

    /// <summary>直近の変形 1 回（1 体）にかかった時間の平均。</summary>
    public double AverageMilliseconds => _timer.AverageMilliseconds;

    /// <summary>
    /// 書き出しのモーションブラーで、物理の焼き込みを読む時刻（小数のフレーム）。null なら今のフレーム。
    /// </summary>
    public float? FrameTimeOverride { get; set; }

    public void UpdateDirtyModels()
    {
        PhysicsStep step = _context.Physics.Current;
        _context.Physics.Consume(); // 同じ進め方で 2 度進めない（当たり判定のために先に呼ばれたときなど）
        foreach (ModelNode node in _context.Scene.ModelsGroup.Children.OfType<ModelNode>())
        {
            if (_context.GetLoaded(node) is { IsPoseDirty: true } model)
            {
                Update(node, model, step);
            }
        }
    }

    private void OnNodeChanged(object? sender, SceneNodeChangedEventArgs e)
    {
        ModelNode? owner = e.Node switch
        {
            BoneNode bone when e.PropertyName is nameof(SceneNode.Position) or nameof(SceneNode.Rotation) => bone.Model,
            MorphNode morph when e.PropertyName == nameof(MorphNode.Weight) => morph.Model,
            ModelNode overridden when e.PropertyName == nameof(ModelNode.MaterialOverrides) => overridden,
            _ => null,
        };
        if (_context.GetLoaded(owner) is { } model)
        {
            model.IsPoseDirty = true;
        }
    }

    private void Update(ModelNode node, LoadedModel model, PhysicsStep step)
    {
        long start = Stopwatch.GetTimestamp();
        model.IsPoseDirty = false;

        PoseInput input = model.Input;
        foreach (BoneNode bone in node.Bones)
        {
            input.Rotations[bone.Index] = bone.Orientation;
            input.Translations[bone.Index] = bone.Position;
        }
        foreach (MorphNode morph in node.Morphs)
        {
            input.MorphWeights[morph.Index] = morph.Weight;
        }

        if (_context.Physics.Enabled && model.Physics is { } physics)
        {
            PoseEvaluator.EvaluateBeforePhysics(model.Rig, input, model.State);
            if (_context.Physics.TryApplyBaked(node, model, FrameTimeOverride ?? _context.Playback.CurrentFrame))
            {
                model.ShowingBakedPhysics = true;
            }
            else
            {
                if (model.ShowingBakedPhysics)
                {
                    // 画面の物理は焼き込みの間は止まっていたので、続きではなく今の姿勢から始める
                    model.ShowingBakedPhysics = false;
                    step = PhysicsStep.Reset;
                }
                if (!TryPhysics(node, model, physics, step, _context.Playback.FrameRate))
                {
                    PoseEvaluator.EvaluateBeforePhysics(model.Rig, input, model.State); // 物理の途中の結果を捨てる
                }
            }
            PoseEvaluator.EvaluateAfterPhysics(model.Rig, model.State);
        }
        else
        {
            PoseEvaluator.Evaluate(model.Rig, input, model.State);
        }
        Skinner.Apply(model.Rig, model.State, model.Vertices, out Vector3 min, out Vector3 max);
        for (int i = 0; i < model.Positions.Length; i++)
        {
            model.Positions[i] = model.Vertices[i].Position;
        }
        model.BoundsMin = min;
        model.BoundsMax = max;
        (model.DisplayBoundsMin, model.DisplayBoundsMax) = DisplayBounds.Compute(model.Positions, model.CoreVertices);

        if (model.Mesh is { } mesh)
        {
            // エンジンの箱は選択の枠を描くだけなので、見た目の箱を渡す
            mesh.UpdateVertices(MemoryMarshal.Cast<SkinnedVertex, MeshVertex>(model.Vertices), model.DisplayBoundsMin, model.DisplayBoundsMax);
            // 材質の値は、材質モーフがあるとき（毎回）と、材質の上書きが変わったときに送る
            MaterialOverrideSet overrides = node.MaterialOverrides;
            if (model.HasMaterialMorphs || !ReferenceEquals(overrides, model.AppliedMaterialOverrides))
            {
                for (int i = 0; i < model.MaterialStates.Length; i++)
                {
                    MaterialValues v = model.State.Materials[i];
                    MaterialOverride o = overrides[i];
                    model.MaterialStates[i] = new MaterialState
                    {
                        Diffuse = v.Diffuse,
                        Specular = v.Specular,
                        SpecularPower = v.SpecularPower,
                        Ambient = v.Ambient,
                        TextureMultiply = v.TextureMultiply,
                        TextureAdd = v.TextureAdd,
                        SphereMultiply = v.SphereMultiply,
                        SphereAdd = v.SphereAdd,
                        ToonMultiply = v.ToonMultiply,
                        ToonAdd = v.ToonAdd,
                        EdgeColor = v.EdgeColor,
                        EdgeSize = v.EdgeSize,
                        Shading = o.Shading == MaterialShading.Pbr ? MaterialShadingKind.Pbr : MaterialShadingKind.Toon,
                        Metallic = o.Metallic,
                        Roughness = o.Roughness,
                        BaseTint = new Vector4(o.Tint, 1f),
                        Emissive = new Vector4(o.EmissiveColor * o.EmissiveStrength, 0f),
                    };
                }
                mesh.UpdateMaterials(model.MaterialStates);
                model.AppliedMaterialOverrides = overrides;
            }
        }
        _timer.Add(Stopwatch.GetElapsedTime(start));
    }

    private static bool TryPhysics(ModelNode node, LoadedModel model, ModelPhysics physics, PhysicsStep step, int frameRate)
    {
        try
        {
            // 物理はモデルの座標で計算するので、モデルを傾けて置いたときは重力も傾ける
            physics.SetGravity(PhysicsController.GravityFor(node));
            switch (step.Kind)
            {
                case PhysicsStepKind.Reset:
                    physics.Reset(model.State);
                    break;
                case PhysicsStepKind.Step:
                    // インパルスモーフは 1 フレームにつき 1 回（止まっているときに落ち着かせる間は、進めた時間の割合で）
                    physics.Step(model.State, step.Seconds, step.Seconds * frameRate);
                    break;
                default:
                    physics.Apply(model.State);
                    break;
            }
            return true;
        }
        catch (PhysicsException ex)
        {
            AppLog.Error($"モデル「{node.Name}」の物理演算に失敗したため、このモデルの物理をやめます", ex);
            model.DisablePhysics();
            return false;
        }
    }
}
