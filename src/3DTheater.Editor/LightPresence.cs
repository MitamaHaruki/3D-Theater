using System.Numerics;
using Theater3D.Domain;

namespace Theater3D.Editor;

/// <summary>
/// 照明を足す・外す（元に戻せる）。足した照明は照明のまとまりの最後に並ぶ。MMD の照明（決まった役目）は外せない。
/// </summary>
public sealed class LightPresenceAction : IUndoableAction
{
    private readonly SceneDocument _scene;
    private readonly LightNode _light;
    private readonly bool _added;
    private readonly int _index;

    private LightPresenceAction(SceneDocument scene, LightNode light, bool added, int index)
    {
        _scene = scene;
        _light = light;
        _added = added;
        _index = index;
    }

    public string Description => _added ? $"照明を追加（{_light.Name}）" : $"照明を外す（{_light.Name}）";

    /// <summary>種類ごとの名前（「点光源」「点光源 2」…）。</summary>
    public static string NameFor(SceneDocument scene, LightType type)
    {
        string baseName = type switch
        {
            LightType.Point => "点光源",
            LightType.Spot => "スポットライト",
            _ => "平行光",
        };
        var used = scene.LightsGroup.Children.Select(n => n.Name).ToHashSet();
        if (!used.Contains(baseName))
        {
            return baseName;
        }
        int number = 2;
        while (used.Contains($"{baseName} {number}"))
        {
            number++;
        }
        return $"{baseName} {number}";
    }

    /// <summary>
    /// 新しい照明を作る。点光源・スポットライトは、カメラの注視点のあたり（<paramref name="target"/>）の手前上に置き、
    /// スポットライトと平行光は注視点のほうへ向ける。
    /// </summary>
    public static LightNode Create(SceneDocument scene, LightType type, Vector3 target)
    {
        var light = new LightNode(NameFor(scene, type), type);
        switch (type)
        {
            case LightType.Point:
                light.Position = target + new Vector3(0f, 6f, -8f);
                light.Range = 25f;
                break;
            case LightType.Spot:
                light.Position = target + new Vector3(0f, 12f, -12f);
                light.Rotation = LookRotation(target - light.Position);
                light.Range = 40f;
                break;
            default:
                light.Rotation = new Vector3(35f, 135f, 0f); // 後ろ上から（縁を明るくする光）
                light.Intensity = 0.6f;
                break;
        }
        return light;
    }

    /// <summary>照明を足す（元に戻せる操作として積む）。</summary>
    public static void Add(SceneDocument scene, UndoService undo, LightNode light)
    {
        scene.Add(scene.LightsGroup, light);
        undo.Push(new LightPresenceAction(scene, light, added: true, scene.LightsGroup.Children.Count - 1));
    }

    /// <summary>足した照明を外す（元に戻せる）。MMD の照明・ロックした照明は外さず false。</summary>
    public static bool Remove(SceneDocument scene, UndoService undo, LightNode light)
    {
        if (ReferenceEquals(light, scene.MainLight) || !light.CanEdit || light.Parent != scene.LightsGroup)
        {
            return false;
        }
        int index = scene.LightsGroup.Children.ToList().IndexOf(light);
        scene.Remove(light);
        undo.Push(new LightPresenceAction(scene, light, added: false, index));
        return true;
    }

    public void Undo()
    {
        if (_added)
        {
            _scene.Remove(_light);
        }
        else
        {
            _scene.Insert(_scene.LightsGroup, Math.Min(_index, _scene.LightsGroup.Children.Count), _light);
        }
    }

    public void Redo()
    {
        if (_added)
        {
            _scene.Insert(_scene.LightsGroup, Math.Min(_index, _scene.LightsGroup.Children.Count), _light);
        }
        else
        {
            _scene.Remove(_light);
        }
    }

    /// <summary>+Z を <paramref name="direction"/> へ向ける回転（度。X = 下向き、Y = 左右。カメラ・照明と同じ決まり）。</summary>
    public static Vector3 LookRotation(Vector3 direction)
    {
        if (direction.LengthSquared() < 1e-12f)
        {
            return Vector3.Zero;
        }
        Vector3 d = Vector3.Normalize(direction);
        float pitch = MathF.Asin(Math.Clamp(-d.Y, -1f, 1f)) * 180f / MathF.PI;
        float yaw = MathF.Atan2(d.X, d.Z) * 180f / MathF.PI;
        return new Vector3(pitch, yaw, 0f);
    }
}
