// Phase 0 の確認用の形（床の格子・座標軸・立方体）。モデル読み込みが入ったら置き換える。
#pragma once

#include <cstdint>
#include <vector>

namespace tt {

struct DemoVertex {
    float position[3];
    float color[4];
};

// XZ 平面（Y=0）の格子と座標軸を線分の頂点 2 個ずつで返す。
std::vector<DemoVertex> BuildGridLines(int halfLineCount, float spacing);

// 原点中心・1 辺 1 の立方体。面ごとに色を変える。
void BuildCube(std::vector<DemoVertex>& vertices, std::vector<uint16_t>& indices);

} // namespace tt
