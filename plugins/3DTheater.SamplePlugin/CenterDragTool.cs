using System.Numerics;
using Plugin.UI.Wpf;
using Theater3D.Plugin.Sdk;

namespace Theater3D.SamplePlugin;

/// <summary>
/// 編集の道具の見本「センターを床の上で動かす」: 選んだモデルの「センター」を、プレビューの床（高さ 0）の上でドラッグして前後左右に動かす。
/// 押した所から今の所までの床の上の動きを、押したときのセンターの移動に足す（ドラッグ 1 回が 1 つの「元に戻す」になる）。
/// 見本なので、モデルを回したり大きさを変えたりしていない（センターの親が回っていない）ときの動きにしている。
/// </summary>
internal sealed class CenterDragTool(ITheaterScene scene) : IEditToolHandler
{
    /// <summary>動かすボーンの名前（MMD のモデルでよく使う名前）。</summary>
    internal const string CenterBone = "センター";

    private string? _modelId;
    private Vector3 _startFloor;
    private BonePose _startPose;

    public bool Pressed(EditToolPointer pointer)
    {
        ITheaterView? view = pointer.GetView<ITheaterView>();
        if (view?.DragEdit is null || scene.SelectedModelId is not { } modelId
            || scene.GetBonePose(modelId, CenterBone) is not { } pose || FloorPoint(view) is not { } floor)
        {
            return false; // モデルを選んでいない・センターが無い・床が見えない（真横からの視点など）ときは、アプリの操作（選ぶ）に任せる
        }
        _modelId = modelId;
        _startFloor = floor;
        _startPose = pose;
        return true;
    }

    public void Moved(EditToolPointer pointer)
    {
        if (_modelId is null || pointer.GetView<ITheaterView>() is not { DragEdit: { } edit } view || FloorPoint(view) is not { } floor)
        {
            return;
        }
        Vector3 delta = floor - _startFloor;
        edit.SetBonePose(_modelId, CenterBone, _startPose with { Translation = _startPose.Translation + new Vector3(delta.X, 0f, delta.Z) });
    }

    public void Released(EditToolPointer pointer) => _modelId = null;

    public void Cancelled() => _modelId = null;

    public void Deactivated() => _modelId = null;

    /// <summary>視線と床（高さ 0 の平面）の交わる所。床と平行か、床が視点の後ろなら null。</summary>
    internal static Vector3? FloorPoint(ITheaterView view)
    {
        Vector3 origin = view.RayOrigin;
        Vector3 direction = view.RayDirection;
        if (MathF.Abs(direction.Y) < 1e-4f)
        {
            return null;
        }
        float t = -origin.Y / direction.Y;
        return t > 0f ? origin + (direction * t) : null;
    }
}
