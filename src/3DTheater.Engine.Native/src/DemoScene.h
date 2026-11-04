// モデル読み込みができるまでの確認用の形（格子・座標軸・立方体）。
#pragma once

#include <cstdint>
#include <vector>

namespace tt {

struct DemoVertex {
    float position[3];
    float color[4];
};

// XZ 平面（Y=0）の格子を線分の頂点 2 個ずつで返す。中心の 2 本は座標軸として別に描くので含めない。
std::vector<DemoVertex> BuildGridLines(int halfLineCount, float spacing);

// X・Y・Z 軸の線（それぞれ −extent から +extent）。頂点 0-1 が X、2-3 が Y、4-5 が Z。
std::vector<DemoVertex> BuildAxisLines(float extent);

// 原点中心・1 辺 1 の立方体。面ごとに色を変える。
void BuildCube(std::vector<DemoVertex>& vertices, std::vector<uint16_t>& indices);

// 立方体の 12 本の辺。選択中の強調に使う（面と重ならないよう少し大きくする）。
std::vector<DemoVertex> BuildCubeEdges(float scale);

} // namespace tt
