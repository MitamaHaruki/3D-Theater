#include "DemoScene.h"

namespace tt {

namespace {

struct Color {
    float r, g, b, a;
};

void AddLine(std::vector<DemoVertex>& out, float x0, float y0, float z0, float x1, float y1, float z1, Color c)
{
    out.push_back({{x0, y0, z0}, {c.r, c.g, c.b, c.a}});
    out.push_back({{x1, y1, z1}, {c.r, c.g, c.b, c.a}});
}

} // namespace

std::vector<DemoVertex> BuildGridLines(int halfLineCount, float spacing)
{
    constexpr Color minor{0.24f, 0.25f, 0.28f, 1.0f};
    constexpr Color major{0.34f, 0.35f, 0.39f, 1.0f};

    std::vector<DemoVertex> vertices;
    const float extent = static_cast<float>(halfLineCount) * spacing;

    for (int i = -halfLineCount; i <= halfLineCount; ++i) {
        if (i == 0) {
            continue; // 中心の線は座標軸として色を変えて描く
        }
        const float p = static_cast<float>(i) * spacing;
        const Color c = (i % 5 == 0) ? major : minor;
        AddLine(vertices, -extent, 0.0f, p, extent, 0.0f, p, c);
        AddLine(vertices, p, 0.0f, -extent, p, 0.0f, extent, c);
    }
    return vertices;
}

std::vector<DemoVertex> BuildAxisLines(float extent)
{
    constexpr Color xAxis{0.86f, 0.27f, 0.27f, 1.0f};
    constexpr Color yAxis{0.33f, 0.78f, 0.36f, 1.0f};
    constexpr Color zAxis{0.27f, 0.47f, 0.92f, 1.0f};

    std::vector<DemoVertex> vertices;
    AddLine(vertices, -extent, 0.0f, 0.0f, extent, 0.0f, 0.0f, xAxis);
    AddLine(vertices, 0.0f, -extent, 0.0f, 0.0f, extent, 0.0f, yAxis);
    AddLine(vertices, 0.0f, 0.0f, -extent, 0.0f, 0.0f, extent, zAxis);
    return vertices;
}

std::vector<DemoVertex> BuildCubeEdges(float scale)
{
    constexpr Color highlight{1.0f, 0.62f, 0.15f, 1.0f};
    const float h = 0.5f * scale;

    std::vector<DemoVertex> vertices;
    for (int a = 0; a < 8; ++a) {
        for (int bit = 1; bit < 8; bit <<= 1) {
            const int b = a | bit;
            if (b == a) {
                continue; // a から 1 つの軸だけ + 側へ進んだ角とつなぐと、12 本の辺がちょうど 1 回ずつ出る
            }
            auto x = [&](int i) { return (i & 1) ? h : -h; };
            auto y = [&](int i) { return (i & 2) ? h : -h; };
            auto z = [&](int i) { return (i & 4) ? h : -h; };
            AddLine(vertices, x(a), y(a), z(a), x(b), y(b), z(b), highlight);
        }
    }
    return vertices;
}

void BuildCube(std::vector<DemoVertex>& vertices, std::vector<uint16_t>& indices)
{
    constexpr float h = 0.5f;

    // 角の番号のビット: 1 = +X, 2 = +Y, 4 = +Z
    auto corner = [](int i, float out[3]) {
        out[0] = (i & 1) ? h : -h;
        out[1] = (i & 2) ? h : -h;
        out[2] = (i & 4) ? h : -h;
    };

    struct Face {
        int corners[4]; // 面の外周を一周する順
        Color color;
    };
    constexpr Face faces[] = {
        {{0, 2, 6, 4}, {0.25f, 0.65f, 0.90f, 1.0f}}, // -X
        {{1, 3, 7, 5}, {0.35f, 0.80f, 0.60f, 1.0f}}, // +X
        {{0, 1, 5, 4}, {0.50f, 0.50f, 0.52f, 1.0f}}, // -Y
        {{2, 3, 7, 6}, {0.95f, 0.85f, 0.35f, 1.0f}}, // +Y
        {{0, 1, 3, 2}, {0.95f, 0.55f, 0.25f, 1.0f}}, // -Z（初期カメラ側）
        {{4, 5, 7, 6}, {0.60f, 0.40f, 0.88f, 1.0f}}, // +Z
    };

    vertices.clear();
    indices.clear();
    for (const Face& face : faces) {
        const auto base = static_cast<uint16_t>(vertices.size());
        for (int c : face.corners) {
            DemoVertex v{};
            corner(c, v.position);
            v.color[0] = face.color.r;
            v.color[1] = face.color.g;
            v.color[2] = face.color.b;
            v.color[3] = face.color.a;
            vertices.push_back(v);
        }
        const uint16_t quad[] = {0, 1, 2, 0, 2, 3};
        for (uint16_t q : quad) {
            indices.push_back(static_cast<uint16_t>(base + q));
        }
    }
}

} // namespace tt
