using System.Numerics;

namespace Theater3D.Engine.Interop;

/// <summary>tt_engine.h の TT_FRAME_* と同じ値。</summary>
[Flags]
public enum FrameFlags : uint
{
    None = 0,
    ShowGrid = 1u << 0,
    ShowAxisX = 1u << 1,
    ShowAxisY = 1u << 2,
    ShowAxisZ = 1u << 3,
    ShowTestObject = 1u << 4,
    TestObjectSelected = 1u << 5,
}

/// <summary>
/// 1 つのビューを 1 枚描くための条件。行列は System.Numerics と同じ「行ベクトル × 行列」で、左手系・Y が上。
/// </summary>
public readonly record struct FrameParameters(
    FrameFlags Flags,
    Matrix4x4 View,
    Matrix4x4 Projection,
    Matrix4x4 GridWorld,
    Matrix4x4 TestObjectWorld,
    IReadOnlyList<DrawItem>? Items = null);
