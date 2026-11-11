// 公開 C API を通したネイティブエンジンのテスト。
// GPU の無い環境でも動くよう、ソフトウェア描画（WARP）を使う。
#include <windows.h>

#include <CppUnitTest.h>
#include <DirectXMath.h>

#include <algorithm>
#include <cmath>
#include <cstdlib>
#include <limits>
#include <string>
#include <thread>
#include <utility>
#include <vector>

#include "tt_engine.h"

using namespace Microsoft::VisualStudio::CppUnitTestFramework;

namespace {

TtEngine* CreateWarpEngine()
{
    const TtEngineDesc desc{sizeof(TtEngineDesc), TT_ENGINE_FLAG_USE_WARP};
    TtEngine* engine = nullptr;
    Assert::AreEqual<int>(TT_OK, tt_engine_create(&desc, &engine));
    Assert::IsNotNull(engine);
    return engine;
}

std::wstring LastErrorMessage()
{
    wchar_t buffer[512]{};
    tt_get_last_error_message(buffer, 512);
    return buffer;
}

bool IsNear(uint8_t actual, int expected, int tolerance)
{
    return std::abs(static_cast<int>(actual) - expected) <= tolerance;
}

// 背景色 (0.13, 0.14, 0.16) を BGRA 8 ビットにしたもの
constexpr int kBackgroundB = 41;
constexpr int kBackgroundG = 36;
constexpr int kBackgroundR = 33;

constexpr uint32_t kDefaultFlags = TT_FRAME_SHOW_GRID | TT_FRAME_SHOW_AXIS_X | TT_FRAME_SHOW_AXIS_Z | TT_FRAME_SHOW_TEST_OBJECT;

// 斜め上から確認用の立方体（中心 (0, 0.5, 0)）を見る描画条件
TtFrameParams MakeParams(uint32_t flags = kDefaultFlags)
{
    using namespace DirectX;
    auto store = [](float (&dst)[16], FXMMATRIX m) { XMStoreFloat4x4(reinterpret_cast<XMFLOAT4X4*>(dst), m); };

    TtFrameParams params{};
    params.struct_size = sizeof(params);
    params.flags = flags;
    store(params.view, XMMatrixLookAtLH(XMVectorSet(4.0f, 3.0f, -6.0f, 1.0f), XMVectorSet(0.0f, 0.5f, 0.0f, 1.0f),
                           XMVectorSet(0.0f, 1.0f, 0.0f, 0.0f)));
    store(params.projection, XMMatrixPerspectiveFovLH(XMConvertToRadians(45.0f), 4.0f / 3.0f, 0.1f, 1000.0f));
    store(params.grid_world, XMMatrixIdentity());
    store(params.test_object_world, XMMatrixTranslation(0.0f, 0.5f, 0.0f));
    return params;
}

bool IsBackground(const uint8_t* pixel, int tolerance)
{
    return IsNear(pixel[0], kBackgroundB, tolerance) && IsNear(pixel[1], kBackgroundG, tolerance)
        && IsNear(pixel[2], kBackgroundR, tolerance);
}

// 正面（-Z 側）から原点を見る描画条件。格子などは出さない
TtFrameParams MakeFrontParams()
{
    using namespace DirectX;
    TtFrameParams params = MakeParams(0);
    XMStoreFloat4x4(reinterpret_cast<XMFLOAT4X4*>(params.view),
        XMMatrixLookAtLH(XMVectorSet(0.0f, 0.0f, -5.0f, 1.0f), XMVectorZero(), XMVectorSet(0.0f, 1.0f, 0.0f, 0.0f)));
    return params;
}

// XY 平面の 1 辺 2 の四角形（-Z 側を向く）。clockwise = true なら正面から見て時計回り（表）
TtMeshVertex QuadVertex(float x, float y)
{
    return TtMeshVertex{{x, y, 0.0f}, {0.0f, 0.0f, -1.0f}, {(x + 1.0f) / 2.0f, (1.0f - y) / 2.0f}};
}

const TtMeshVertex kQuad[] = {QuadVertex(-1, 1), QuadVertex(1, 1), QuadVertex(1, -1), QuadVertex(-1, -1)};
const uint32_t kClockwise[] = {0, 1, 2, 0, 2, 3};
const uint32_t kCounterClockwise[] = {0, 2, 1, 0, 3, 2};

TtMeshMaterial QuadMaterial(TtTexture* texture, uint32_t flags = 0)
{
    TtMeshMaterial m{};
    m.index_start = 0;
    m.index_count = 6;
    m.diffuse[0] = m.diffuse[1] = m.diffuse[2] = m.diffuse[3] = 1.0f;
    m.ambient[0] = m.ambient[1] = m.ambient[2] = 1.0f; // 材質の色を白にして、テクスチャの色がそのまま出るようにする
    m.flags = flags;
    m.texture = texture;
    return m;
}

TtMeshDesc QuadDesc(const uint32_t* indices, const TtMeshMaterial* material)
{
    TtMeshDesc desc{};
    desc.struct_size = sizeof(desc);
    desc.vertex_count = 4;
    desc.vertices = kQuad;
    desc.index_count = 6;
    desc.indices = indices;
    desc.material_count = 1;
    desc.materials = material;
    return desc;
}

TtDrawItem DrawItem(TtMesh* mesh, uint32_t flags = 0)
{
    TtDrawItem item{};
    item.mesh = mesh;
    item.flags = flags;
    DirectX::XMStoreFloat4x4(reinterpret_cast<DirectX::XMFLOAT4X4*>(item.world), DirectX::XMMatrixIdentity());
    return item;
}

TtTexture* CreateSolidTexture(TtEngine* engine, uint8_t b, uint8_t g, uint8_t r)
{
    const uint8_t pixels[] = {b, g, r, 255, b, g, r, 255, b, g, r, 255, b, g, r, 255};
    TtTexture* texture = nullptr;
    Assert::AreEqual<int>(TT_OK, tt_texture_create(engine, 2, 2, pixels, 8, &texture));
    return texture;
}

// 描いて中央の画素を返す
std::vector<uint8_t> RenderAndRead(TtEngine* engine, TtView* view, const TtFrameParams& params, int width, int height)
{
    Assert::AreEqual<int>(TT_OK, tt_view_render(view, &params));
    Assert::AreEqual<int>(TT_OK, tt_engine_wait_for_gpu(engine));
    std::vector<uint8_t> pixels(static_cast<size_t>(width) * height * 4);
    Assert::AreEqual<int>(TT_OK, tt_view_read_pixels(view, pixels.data(), width * 4, static_cast<int>(pixels.size())));
    return pixels;
}

} // namespace

TEST_CLASS(EngineApiTests)
{
public:
    TEST_METHOD(ApiVersionMatchesHeader)
    {
        Assert::AreEqual<uint32_t>(TT_API_VERSION, tt_get_api_version());
    }

    TEST_METHOD(CreateRejectsInvalidArguments)
    {
        TtEngine* engine = reinterpret_cast<TtEngine*>(1);
        Assert::AreEqual<int>(TT_E_INVALID_ARG, tt_engine_create(nullptr, &engine));
        Assert::IsNull(engine);

        const TtEngineDesc wrongSize{sizeof(TtEngineDesc) + 4, 0};
        Assert::AreEqual<int>(TT_E_INVALID_ARG, tt_engine_create(&wrongSize, &engine));
        Assert::IsFalse(LastErrorMessage().empty(), L"失敗の説明が取れること");
    }

    TEST_METHOD(DestroyNullIsNoOp)
    {
        Assert::AreEqual<int>(TT_OK, tt_engine_destroy(nullptr));
        Assert::AreEqual<int>(TT_OK, tt_view_destroy(nullptr));
    }

    TEST_METHOD(WarpEngineReportsSoftwareAdapter)
    {
        TtEngine* engine = CreateWarpEngine();
        TtEngineInfo info{};
        info.struct_size = sizeof(info);
        Assert::AreEqual<int>(TT_OK, tt_engine_get_info(engine, &info));
        Assert::AreEqual<uint32_t>(1, info.is_software);
        Assert::IsTrue(info.feature_level >= 0xb000, L"機能レベル 11_0 以上");
        Assert::IsTrue(wcslen(info.adapter_name) > 0);
        Assert::AreEqual<int>(TT_OK, tt_engine_destroy(engine));
    }

    TEST_METHOD(OffscreenRenderDrawsBackgroundAndCube)
    {
        TtEngine* engine = CreateWarpEngine();
        constexpr int width = 128;
        constexpr int height = 96;
        TtView* view = nullptr;
        Assert::AreEqual<int>(TT_OK, tt_view_create_offscreen(engine, width, height, 0, &view));

        const TtFrameParams params = MakeParams();
        Assert::AreEqual<int>(TT_OK, tt_view_render(view, &params));
        Assert::AreEqual<int>(TT_OK, tt_engine_wait_for_gpu(engine));

        std::vector<uint8_t> pixels(static_cast<size_t>(width) * height * 4);
        Assert::AreEqual<int>(TT_OK, tt_view_read_pixels(view, pixels.data(), width * 4, static_cast<int>(pixels.size())));
        auto pixel = [&](int x, int y) { return &pixels[(static_cast<size_t>(y) * width + x) * 4]; };

        // 左上は空（背景色）
        const uint8_t* corner = pixel(0, 0);
        Assert::IsTrue(IsBackground(corner, 1) && corner[3] == 255, L"左上は背景色であること");

        // 中央にはカメラが見ている立方体が写る
        Assert::IsFalse(IsBackground(pixel(width / 2, height / 2), 40), L"中央は背景色ではないこと");

        Assert::AreEqual<int>(TT_OK, tt_view_destroy(view));
        Assert::AreEqual<int>(TT_OK, tt_engine_destroy(engine));
    }

    TEST_METHOD(FlagsControlWhatIsDrawn)
    {
        TtEngine* engine = CreateWarpEngine();
        constexpr int width = 64;
        constexpr int height = 48;
        TtView* view = nullptr;
        Assert::AreEqual<int>(TT_OK, tt_view_create_offscreen(engine, width, height, 0, &view));
        std::vector<uint8_t> pixels(static_cast<size_t>(width) * height * 4);
        const uint8_t* center = &pixels[(static_cast<size_t>(height / 2) * width + width / 2) * 4];

        // 何も表示しない指定なら、中央も背景色のまま
        const TtFrameParams nothing = MakeParams(0);
        Assert::AreEqual<int>(TT_OK, tt_view_render(view, &nothing));
        Assert::AreEqual<int>(TT_OK, tt_view_read_pixels(view, pixels.data(), width * 4, static_cast<int>(pixels.size())));
        Assert::IsTrue(IsBackground(center, 1), L"立方体を表示しない指定なら写らないこと");

        const TtFrameParams cube = MakeParams(TT_FRAME_SHOW_TEST_OBJECT);
        Assert::AreEqual<int>(TT_OK, tt_view_render(view, &cube));
        Assert::AreEqual<int>(TT_OK, tt_view_read_pixels(view, pixels.data(), width * 4, static_cast<int>(pixels.size())));
        Assert::IsFalse(IsBackground(center, 40), L"立方体を表示する指定なら写ること");

        // 選択中の指定では、立方体の辺が強調色（橙）で描かれる
        auto countHighlight = [&] {
            int count = 0;
            for (size_t i = 0; i < pixels.size(); i += 4) {
                if (pixels[i + 2] > 230 && pixels[i + 1] > 130 && pixels[i + 1] < 190 && pixels[i] < 80) {
                    ++count;
                }
            }
            return count;
        };
        const int before = countHighlight();
        const TtFrameParams selected = MakeParams(TT_FRAME_SHOW_TEST_OBJECT | TT_FRAME_TEST_OBJECT_SELECTED);
        Assert::AreEqual<int>(TT_OK, tt_view_render(view, &selected));
        Assert::AreEqual<int>(TT_OK, tt_view_read_pixels(view, pixels.data(), width * 4, static_cast<int>(pixels.size())));
        Assert::IsTrue(countHighlight() > before + 20, L"選択中は強調色の辺が増えること");

        Assert::AreEqual<int>(TT_OK, tt_view_destroy(view));
        Assert::AreEqual<int>(TT_OK, tt_engine_destroy(engine));
    }

    TEST_METHOD(ResizeToZeroStopsRendering)
    {
        TtEngine* engine = CreateWarpEngine();
        TtView* view = nullptr;
        Assert::AreEqual<int>(TT_OK, tt_view_create_offscreen(engine, 32, 32, 0, &view));
        const TtFrameParams params = MakeParams();

        Assert::AreEqual<int>(TT_OK, tt_view_resize(view, 0, 0));
        Assert::AreEqual<int>(TT_OK, tt_view_render(view, &params));
        uint8_t dummy[4]{};
        Assert::AreEqual<int>(TT_E_INVALID_STATE, tt_view_read_pixels(view, dummy, 4, 4));

        Assert::AreEqual<int>(TT_OK, tt_view_resize(view, 16, 8));
        Assert::AreEqual<int>(TT_OK, tt_view_render(view, &params));
        std::vector<uint8_t> pixels(16 * 8 * 4);
        Assert::AreEqual<int>(TT_OK, tt_view_read_pixels(view, pixels.data(), 16 * 4, static_cast<int>(pixels.size())));

        Assert::AreEqual<int>(TT_OK, tt_view_destroy(view));
        Assert::AreEqual<int>(TT_OK, tt_engine_destroy(engine));
    }

    TEST_METHOD(ReadPixelsRejectsSmallBuffer)
    {
        TtEngine* engine = CreateWarpEngine();
        TtView* view = nullptr;
        Assert::AreEqual<int>(TT_OK, tt_view_create_offscreen(engine, 8, 8, 0, &view));
        std::vector<uint8_t> pixels(8 * 8 * 4 - 1);
        Assert::AreEqual<int>(TT_E_INVALID_ARG, tt_view_read_pixels(view, pixels.data(), 8 * 4, static_cast<int>(pixels.size())));
        Assert::AreEqual<int>(TT_OK, tt_view_destroy(view));
        Assert::AreEqual<int>(TT_OK, tt_engine_destroy(engine));
    }

    TEST_METHOD(EngineCannotBeDestroyedWhileViewsAlive)
    {
        TtEngine* engine = CreateWarpEngine();
        TtView* view = nullptr;
        Assert::AreEqual<int>(TT_OK, tt_view_create_offscreen(engine, 8, 8, 0, &view));
        Assert::AreEqual<int>(TT_E_INVALID_STATE, tt_engine_destroy(engine));
        Assert::AreEqual<int>(TT_OK, tt_view_destroy(view));
        Assert::AreEqual<int>(TT_OK, tt_engine_destroy(engine));
    }

    TEST_METHOD(CallsFromOtherThreadAreRejected)
    {
        TtEngine* engine = CreateWarpEngine();
        TtResult result = TT_OK;
        std::thread worker([&] {
            TtEngineInfo info{};
            info.struct_size = sizeof(info);
            result = tt_engine_get_info(engine, &info);
        });
        worker.join();
        Assert::AreEqual<int>(TT_E_WRONG_THREAD, result);
        Assert::AreEqual<int>(TT_OK, tt_engine_destroy(engine));
    }

    TEST_METHOD(SharedViewIsNotSupportedOnWarp)
    {
        // D3DImage 用の共有は、画面につながった GPU でしかできない
        TtEngine* engine = CreateWarpEngine();
        TtView* view = reinterpret_cast<TtView*>(1);
        Assert::AreEqual<int>(TT_E_NOT_SUPPORTED, tt_view_create_offscreen(engine, 16, 16, TT_VIEW_FLAG_SHARE_D3D9, &view));
        Assert::IsNull(view);
        Assert::AreEqual<int>(TT_OK, tt_engine_destroy(engine), L"作りかけのビューが数に残らないこと");
    }

    TEST_METHOD(WindowViewRendersIntoChildWindow)
    {
        HWND parent = CreateWindowExW(0, L"STATIC", L"test", WS_OVERLAPPEDWINDOW, 0, 0, 200, 200, nullptr, nullptr,
            GetModuleHandleW(nullptr), nullptr);
        Assert::IsNotNull(parent);

        TtEngine* engine = CreateWarpEngine();
        TtView* view = nullptr;
        Assert::AreEqual<int>(TT_OK, tt_view_create_for_window(engine, parent, 64, 48, &view));
        void* child = nullptr;
        Assert::AreEqual<int>(TT_OK, tt_view_get_window(view, &child));
        Assert::IsTrue(IsWindow(static_cast<HWND>(child)) != FALSE);
        Assert::IsTrue(GetParent(static_cast<HWND>(child)) == parent);

        const TtFrameParams params = MakeParams();
        const TtResult first = tt_view_render(view, &params);
        Assert::IsTrue(first == TT_OK || first == TT_S_NOT_READY);
        Assert::AreEqual<int>(TT_OK, tt_view_resize(view, 80, 60));
        Assert::IsTrue(tt_view_render(view, &params) >= 0);
        // なめらかに描いて（MSAA）、スワップチェーンの面へまとめられる
        Assert::AreEqual<int>(TT_OK, tt_view_set_sample_count(view, 4, nullptr));
        Assert::IsTrue(tt_view_render(view, &params) >= 0);

        Assert::AreEqual<int>(TT_OK, tt_view_destroy(view));
        Assert::IsFalse(IsWindow(static_cast<HWND>(child)) != FALSE, L"ビューを消すと子ウィンドウも消えること");
        Assert::AreEqual<int>(TT_OK, tt_engine_destroy(engine));
        DestroyWindow(parent);
    }

    TEST_METHOD(TexturedMeshIsDrawnWithTextureColor)
    {
        TtEngine* engine = CreateWarpEngine();
        constexpr int width = 64;
        constexpr int height = 48;
        TtView* view = nullptr;
        Assert::AreEqual<int>(TT_OK, tt_view_create_offscreen(engine, width, height, 0, &view));

        TtTexture* red = CreateSolidTexture(engine, 0, 0, 255);
        const TtMeshMaterial material = QuadMaterial(red);
        const TtMeshDesc desc = QuadDesc(kClockwise, &material);
        TtMesh* mesh = nullptr;
        Assert::AreEqual<int>(TT_OK, tt_mesh_create(engine, &desc, &mesh));
        Assert::AreEqual<int>(TT_OK, tt_texture_destroy(red), L"メッシュを作った後ならテクスチャを破棄してよい");

        TtFrameParams params = MakeFrontParams();
        const TtDrawItem item = DrawItem(mesh);
        params.items = &item;
        params.item_count = 1;
        std::vector<uint8_t> pixels = RenderAndRead(engine, view, params, width, height);
        const uint8_t* center = &pixels[(static_cast<size_t>(height / 2) * width + width / 2) * 4];
        Assert::IsTrue(center[2] > 150 && center[1] < 20 && center[0] < 20, L"中央はテクスチャの赤（陰影で少し暗い）");
        Assert::IsTrue(IsBackground(&pixels[0], 1), L"四角形の外は背景色");

        Assert::AreEqual<int>(TT_OK, tt_mesh_destroy(mesh));
        Assert::AreEqual<int>(TT_OK, tt_view_destroy(view));
        Assert::AreEqual<int>(TT_OK, tt_engine_destroy(engine));
    }

    TEST_METHOD(BackFacesAreCulledUnlessDoubleSided)
    {
        // 時計回りの面が表（MMD と同じ）。反時計回りの面は、両面描画の材質でなければ描かない
        TtEngine* engine = CreateWarpEngine();
        constexpr int width = 32;
        constexpr int height = 32;
        TtView* view = nullptr;
        Assert::AreEqual<int>(TT_OK, tt_view_create_offscreen(engine, width, height, 0, &view));

        auto centerOf = [&](uint32_t flags) {
            const TtMeshMaterial material = QuadMaterial(nullptr, flags);
            const TtMeshDesc desc = QuadDesc(kCounterClockwise, &material);
            TtMesh* mesh = nullptr;
            Assert::AreEqual<int>(TT_OK, tt_mesh_create(engine, &desc, &mesh));
            TtFrameParams params = MakeFrontParams();
            const TtDrawItem item = DrawItem(mesh);
            params.items = &item;
            params.item_count = 1;
            std::vector<uint8_t> pixels = RenderAndRead(engine, view, params, width, height);
            Assert::AreEqual<int>(TT_OK, tt_mesh_destroy(mesh));
            const size_t index = (static_cast<size_t>(height / 2) * width + width / 2) * 4;
            return std::vector<uint8_t>(pixels.begin() + index, pixels.begin() + index + 4);
        };

        Assert::IsTrue(IsBackground(centerOf(0).data(), 1), L"裏向きの面は描かない");
        Assert::IsFalse(IsBackground(centerOf(TT_MATERIAL_DOUBLE_SIDED).data(), 40), L"両面描画なら裏向きでも描く");

        Assert::AreEqual<int>(TT_OK, tt_view_destroy(view));
        Assert::AreEqual<int>(TT_OK, tt_engine_destroy(engine));
    }

    TEST_METHOD(ToonTextureShadesBySideOfTheLight)
    {
        // トゥーン: 光の当たる側はトゥーンの上の色、反対側は下の色を掛ける。照明の色は材質の拡散色に掛かる
        TtEngine* engine = CreateWarpEngine();
        constexpr int width = 32;
        constexpr int height = 32;
        TtView* view = nullptr;
        Assert::AreEqual<int>(TT_OK, tt_view_create_offscreen(engine, width, height, 0, &view));

        const uint8_t toonPixels[] = {255, 255, 255, 255, 255, 255, 255, 255, 0, 0, 200, 255, 0, 0, 200, 255}; // 上が白、下が赤
        TtTexture* toon = nullptr;
        Assert::AreEqual<int>(TT_OK, tt_texture_create(engine, 2, 2, toonPixels, 8, &toon));
        TtMeshMaterial material = QuadMaterial(nullptr);
        material.toon_texture = toon;
        const TtMeshDesc desc = QuadDesc(kClockwise, &material);
        TtMesh* mesh = nullptr;
        Assert::AreEqual<int>(TT_OK, tt_mesh_create(engine, &desc, &mesh));
        Assert::AreEqual<int>(TT_OK, tt_texture_destroy(toon));

        auto centerWith = [&](float lightZ, float lightColor) {
            TtFrameParams params = MakeFrontParams();
            params.flags |= TT_FRAME_USE_LIGHT;
            params.light_direction[2] = lightZ; // 四角形は -Z を向いている
            params.light_color[0] = params.light_color[1] = params.light_color[2] = lightColor;
            const TtDrawItem item = DrawItem(mesh);
            params.items = &item;
            params.item_count = 1;
            std::vector<uint8_t> pixels = RenderAndRead(engine, view, params, width, height);
            const size_t index = (static_cast<size_t>(height / 2) * width + width / 2) * 4;
            return std::vector<uint8_t>(pixels.begin() + index, pixels.begin() + index + 4);
        };

        std::vector<uint8_t> lit = centerWith(1.0f, 0.6f); // 正面から照らす
        Assert::IsTrue(IsNear(lit[0], 255, 3) && IsNear(lit[1], 255, 3) && IsNear(lit[2], 255, 3), L"光の当たる側はトゥーンの上（白）");
        std::vector<uint8_t> shadow = centerWith(-1.0f, 0.6f); // 裏から照らす
        Assert::IsTrue(IsNear(shadow[0], 0, 3) && IsNear(shadow[1], 0, 3) && IsNear(shadow[2], 200, 3), L"反対側はトゥーンの下（赤）");

        // 環境色 0 なら、色は 拡散色 × 照明の色
        material.ambient[0] = material.ambient[1] = material.ambient[2] = 0.0f;
        material.toon_texture = nullptr;
        const TtMeshDesc dark = QuadDesc(kClockwise, &material);
        Assert::AreEqual<int>(TT_OK, tt_mesh_destroy(mesh));
        Assert::AreEqual<int>(TT_OK, tt_mesh_create(engine, &dark, &mesh));
        std::vector<uint8_t> half = centerWith(1.0f, 0.5f);
        Assert::IsTrue(IsNear(half[0], 128, 3) && IsNear(half[1], 128, 3) && IsNear(half[2], 128, 3), L"照明の色 0.5 で半分の明るさ");

        Assert::AreEqual<int>(TT_OK, tt_mesh_destroy(mesh));
        Assert::AreEqual<int>(TT_OK, tt_view_destroy(view));
        Assert::AreEqual<int>(TT_OK, tt_engine_destroy(engine));
    }

    TEST_METHOD(SphereMapMultipliesOrAdds)
    {
        // 正面を向いた面はスフィアの中央を読む。掛ける（.sph）と足す（.spa）
        TtEngine* engine = CreateWarpEngine();
        constexpr int width = 32;
        constexpr int height = 32;
        TtView* view = nullptr;
        Assert::AreEqual<int>(TT_OK, tt_view_create_offscreen(engine, width, height, 0, &view));
        TtTexture* red = CreateSolidTexture(engine, 0, 0, 255);
        TtTexture* green = CreateSolidTexture(engine, 0, 102, 0);

        auto centerWith = [&](TtTexture* sphere, uint32_t mode) {
            TtMeshMaterial material = QuadMaterial(nullptr);
            material.diffuse[0] = material.diffuse[1] = material.diffuse[2] = 0.0f;
            material.ambient[0] = material.ambient[1] = material.ambient[2] = 0.3f; // 灰色（77）
            material.sphere_texture = sphere;
            material.sphere_mode = mode;
            const TtMeshDesc desc = QuadDesc(kClockwise, &material);
            TtMesh* mesh = nullptr;
            Assert::AreEqual<int>(TT_OK, tt_mesh_create(engine, &desc, &mesh));
            TtFrameParams params = MakeFrontParams();
            const TtDrawItem item = DrawItem(mesh);
            params.items = &item;
            params.item_count = 1;
            std::vector<uint8_t> pixels = RenderAndRead(engine, view, params, width, height);
            Assert::AreEqual<int>(TT_OK, tt_mesh_destroy(mesh));
            const size_t index = (static_cast<size_t>(height / 2) * width + width / 2) * 4;
            return std::vector<uint8_t>(pixels.begin() + index, pixels.begin() + index + 4);
        };

        std::vector<uint8_t> multiplied = centerWith(red, TT_SPHERE_MULTIPLY);
        Assert::IsTrue(IsNear(multiplied[2], 77, 3) && IsNear(multiplied[1], 0, 3) && IsNear(multiplied[0], 0, 3), L"掛ける: 灰色 × 赤");
        std::vector<uint8_t> added = centerWith(green, TT_SPHERE_ADD);
        Assert::IsTrue(IsNear(added[2], 77, 3) && IsNear(added[1], 179, 3) && IsNear(added[0], 77, 3), L"足す: 灰色 + 緑");
        std::vector<uint8_t> none = centerWith(red, TT_SPHERE_NONE);
        Assert::IsTrue(IsNear(none[2], 77, 3) && IsNear(none[1], 77, 3), L"使い方が無しなら、スフィアを使わない");

        TtMeshMaterial bad = QuadMaterial(nullptr);
        bad.sphere_texture = red;
        bad.sphere_mode = 3;
        const TtMeshDesc badDesc = QuadDesc(kClockwise, &bad);
        TtMesh* mesh = nullptr;
        Assert::AreEqual<int>(TT_E_INVALID_ARG, tt_mesh_create(engine, &badDesc, &mesh), L"知らないスフィアの使い方");

        Assert::AreEqual<int>(TT_OK, tt_texture_destroy(red));
        Assert::AreEqual<int>(TT_OK, tt_texture_destroy(green));
        Assert::AreEqual<int>(TT_OK, tt_view_destroy(view));
        Assert::AreEqual<int>(TT_OK, tt_engine_destroy(engine));
    }

    TEST_METHOD(EdgeOutlinesTheSilhouette)
    {
        // 1 辺 1 の立方体（法線は中心から外向き）。輪郭線は面の外へ決まったピクセルだけはみ出す
        TtEngine* engine = CreateWarpEngine();
        constexpr int width = 128;
        constexpr int height = 96;
        TtView* view = nullptr;
        Assert::AreEqual<int>(TT_OK, tt_view_create_offscreen(engine, width, height, 0, &view));

        std::vector<TtMeshVertex> vertices;
        for (int i = 0; i < 8; ++i) {
            const float x = (i & 1) ? 0.5f : -0.5f;
            const float y = (i & 2) ? 0.5f : -0.5f;
            const float z = (i & 4) ? 0.5f : -0.5f;
            const float n = 1.0f / std::sqrt(0.75f);
            vertices.push_back(TtMeshVertex{{x, y, z}, {x * n, y * n, z * n}, {0.0f, 0.0f}});
        }
        std::vector<uint32_t> indices;
        auto addQuad = [&](uint32_t a, uint32_t b, uint32_t c, uint32_t d) {
            // 外から見て時計回り（外向きの法線 = (b - a) × (c - a)）になるよう、向きをそろえる
            using namespace DirectX;
            auto p = [&](uint32_t i) { return XMLoadFloat3(reinterpret_cast<const XMFLOAT3*>(vertices[i].position)); };
            const XMVECTOR normal = XMVector3Cross(p(b) - p(a), p(c) - p(a));
            const XMVECTOR center = (p(a) + p(b) + p(c) + p(d)) * 0.25f;
            if (XMVectorGetX(XMVector3Dot(normal, center)) < 0) {
                std::swap(b, d);
            }
            indices.insert(indices.end(), {a, b, c, a, c, d});
        };
        addQuad(0, 2, 3, 1); // -Z
        addQuad(4, 5, 7, 6); // +Z
        addQuad(0, 1, 5, 4); // -Y
        addQuad(2, 6, 7, 3); // +Y
        addQuad(0, 4, 6, 2); // -X
        addQuad(1, 3, 7, 5); // +X

        TtMeshMaterial material = QuadMaterial(nullptr, TT_MATERIAL_EDGE);
        material.index_count = static_cast<uint32_t>(indices.size());
        material.edge_size = 4.0f;
        material.edge_color[1] = 1.0f; // 緑
        material.edge_color[3] = 1.0f;
        TtMeshDesc desc{};
        desc.struct_size = sizeof(desc);
        desc.vertex_count = static_cast<uint32_t>(vertices.size());
        desc.vertices = vertices.data();
        desc.index_count = static_cast<uint32_t>(indices.size());
        desc.indices = indices.data();
        desc.material_count = 1;
        desc.materials = &material;
        TtMesh* mesh = nullptr;
        Assert::AreEqual<int>(TT_OK, tt_mesh_create(engine, &desc, &mesh));

        auto render = [&](float edgePixels) {
            TtFrameParams params = MakeFrontParams();
            params.edge_pixel_scale = edgePixels;
            const TtDrawItem item = DrawItem(mesh);
            params.items = &item;
            params.item_count = 1;
            return RenderAndRead(engine, view, params, width, height);
        };
        auto pixel = [&](const std::vector<uint8_t>& pixels, int x, int y) { return &pixels[(static_cast<size_t>(y) * width + x) * 4]; };
        auto isGreen = [](const uint8_t* p) { return p[1] > 200 && p[0] < 40 && p[2] < 40; };

        // 正面の面の右端は中央から約 12.9 ピクセル右（距離 4.5、視野角 45°、幅 128）。角の法線は斜め 45° なので、横へは約 2.8 ピクセル
        std::vector<uint8_t> withEdge = render(1.0f);
        Assert::IsTrue(isGreen(pixel(withEdge, 78, 48)), L"面の右のすぐ外は輪郭線の色");
        Assert::IsTrue(IsBackground(pixel(withEdge, 90, 48), 1), L"太さより外は背景");
        Assert::IsFalse(isGreen(pixel(withEdge, 64, 48)), L"面の上は輪郭線で塗らない");

        std::vector<uint8_t> noEdge = render(0.0f);
        Assert::IsTrue(IsBackground(pixel(noEdge, 78, 48), 1), L"輪郭線の太さ 0 なら描かない");

        // 材質モーフで太さを 0 にすると消える
        TtMaterialState state{};
        state.diffuse[0] = state.diffuse[1] = state.diffuse[2] = state.diffuse[3] = 1.0f;
        state.ambient[0] = state.ambient[1] = state.ambient[2] = 1.0f;
        for (float* v : {state.texture_multiply, state.sphere_multiply, state.toon_multiply}) {
            v[0] = v[1] = v[2] = v[3] = 1.0f;
        }
        state.edge_color[1] = state.edge_color[3] = 1.0f;
        state.edge_size = 0.0f;
        Assert::AreEqual<int>(TT_OK, tt_mesh_update_materials(mesh, &state, 1));
        std::vector<uint8_t> morphed = render(1.0f);
        Assert::IsTrue(IsBackground(pixel(morphed, 78, 48), 1), L"太さ 0 の材質は輪郭線を描かない");

        Assert::AreEqual<int>(TT_OK, tt_mesh_destroy(mesh));
        Assert::AreEqual<int>(TT_OK, tt_view_destroy(view));
        Assert::AreEqual<int>(TT_OK, tt_engine_destroy(engine));
    }

    TEST_METHOD(BackgroundCanBeAColorOrTransparent)
    {
        // 書き出し用: 背景を決まった色・透明で消せる。透明な背景に半透明の物を描くと、色は透明度をかけ合わせた値になる
        TtEngine* engine = CreateWarpEngine();
        constexpr int width = 32;
        constexpr int height = 32;
        TtView* view = nullptr;
        Assert::AreEqual<int>(TT_OK, tt_view_create_offscreen(engine, width, height, 0, &view));
        TtMeshMaterial material = QuadMaterial(nullptr);
        material.diffuse[3] = 0.5f; // 白・不透明度 0.5
        const TtMeshDesc desc = QuadDesc(kClockwise, &material);
        TtMesh* mesh = nullptr;
        Assert::AreEqual<int>(TT_OK, tt_mesh_create(engine, &desc, &mesh));

        auto render = [&](float r, float g, float b, float a, bool withQuad) {
            TtFrameParams params = MakeFrontParams();
            params.flags = TT_FRAME_CLEAR_COLOR;
            params.clear_color[0] = r;
            params.clear_color[1] = g;
            params.clear_color[2] = b;
            params.clear_color[3] = a;
            const TtDrawItem item = DrawItem(mesh);
            params.items = withQuad ? &item : nullptr;
            params.item_count = withQuad ? 1 : 0;
            return RenderAndRead(engine, view, params, width, height);
        };
        const size_t center = (static_cast<size_t>(height / 2) * width + width / 2) * 4;

        std::vector<uint8_t> orange = render(1.0f, 0.5f, 0.0f, 1.0f, false);
        Assert::IsTrue(IsNear(orange[0], 0, 1) && IsNear(orange[1], 128, 1) && IsNear(orange[2], 255, 1) && orange[3] == 255, L"決まった色で消す");

        std::vector<uint8_t> transparent = render(0.0f, 0.0f, 0.0f, 0.0f, true);
        Assert::AreEqual<int>(0, transparent[3], L"何も無い所は透明");
        const uint8_t* p = &transparent[center];
        Assert::IsTrue(IsNear(p[0], 128, 2) && IsNear(p[1], 128, 2) && IsNear(p[2], 128, 2) && IsNear(p[3], 128, 2),
            L"半透明の白は、透明度をかけ合わせた灰色・透明度 0.5 で残る");

        Assert::AreEqual<int>(TT_OK, tt_mesh_destroy(mesh));
        Assert::AreEqual<int>(TT_OK, tt_view_destroy(view));
        Assert::AreEqual<int>(TT_OK, tt_engine_destroy(engine));
    }

    TEST_METHOD(MultisamplingSmoothsEdges)
    {
        // 斜めに置いた白い四角形を黒い背景に描く。1 点のときは縁の画素も白か黒のどちらかだが、
        // なめらかに描く（MSAA）と、縁に中間の灰色ができる
        TtEngine* engine = CreateWarpEngine();
        constexpr int width = 64;
        constexpr int height = 48;
        TtView* view = nullptr;
        Assert::AreEqual<int>(TT_OK, tt_view_create_offscreen(engine, width, height, 0, &view));
        const TtMeshMaterial material = QuadMaterial(nullptr);
        const TtMeshDesc desc = QuadDesc(kClockwise, &material);
        TtMesh* mesh = nullptr;
        Assert::AreEqual<int>(TT_OK, tt_mesh_create(engine, &desc, &mesh));

        auto render = [&](int w, int h) {
            TtFrameParams params = MakeFrontParams();
            DirectX::XMStoreFloat4x4(reinterpret_cast<DirectX::XMFLOAT4X4*>(params.projection),
                DirectX::XMMatrixPerspectiveFovLH(DirectX::XMConvertToRadians(45.0f), static_cast<float>(w) / h, 0.1f, 1000.0f));
            params.flags = TT_FRAME_CLEAR_COLOR;
            params.clear_color[3] = 1.0f; // 黒
            TtDrawItem item = DrawItem(mesh);
            DirectX::XMStoreFloat4x4(reinterpret_cast<DirectX::XMFLOAT4X4*>(item.world),
                DirectX::XMMatrixScaling(0.8f, 0.8f, 1.0f) * DirectX::XMMatrixRotationZ(DirectX::XMConvertToRadians(30.0f)));
            params.items = &item;
            params.item_count = 1;
            return RenderAndRead(engine, view, params, w, h);
        };
        // 白でも黒でもない画素の数と、白い画素の数
        auto count = [](const std::vector<uint8_t>& pixels, int& white) {
            int partial = 0;
            white = 0;
            for (size_t i = 0; i < pixels.size(); i += 4) {
                const int g = pixels[i + 1];
                partial += (g > 40 && g < 200) ? 1 : 0;
                white += g >= 200 ? 1 : 0;
            }
            return partial;
        };

        int white1 = 0;
        Assert::AreEqual(0, count(render(width, height), white1), L"1 点のときは中間の色が無い");
        Assert::IsTrue(white1 > 200, L"四角形が描かれている");

        uint32_t actual = 0;
        Assert::AreEqual<int>(TT_OK, tt_view_set_sample_count(view, 4, &actual));
        Assert::AreEqual<uint32_t>(4, actual, L"WARP は 4 点を使える");
        int white4 = 0;
        const int partial4 = count(render(width, height), white4);
        Assert::IsTrue(partial4 > 20, L"縁に中間の色ができる");
        Assert::IsTrue(std::abs(white4 - white1) < partial4, L"形は同じ（縁がなめらかになるだけ）");

        Assert::AreEqual<int>(TT_OK, tt_view_set_sample_count(view, 8, &actual));
        Assert::IsTrue(actual >= 4 && actual <= 8, L"使えなければ少ない数にする");
        Assert::AreEqual<int>(TT_E_INVALID_ARG, tt_view_set_sample_count(view, 3, nullptr));
        Assert::AreEqual<int>(TT_E_INVALID_ARG, tt_view_set_sample_count(view, 0, nullptr));

        // 大きさを変えても、なめらかなまま描ける。0 の大きさの間に変えてもよい
        Assert::AreEqual<int>(TT_OK, tt_view_resize(view, 32, 24));
        int white = 0;
        Assert::IsTrue(count(render(32, 24), white) > 0);
        Assert::AreEqual<int>(TT_OK, tt_view_resize(view, 0, 0));
        Assert::AreEqual<int>(TT_OK, tt_view_set_sample_count(view, 2, &actual));
        Assert::AreEqual<int>(TT_OK, tt_view_resize(view, width, height));
        Assert::IsTrue(count(render(width, height), white) > 0);

        Assert::AreEqual<int>(TT_OK, tt_view_set_sample_count(view, 1, &actual));
        Assert::AreEqual<uint32_t>(1, actual);
        Assert::AreEqual(0, count(render(width, height), white), L"1 点に戻すと中間の色は無くなる");

        // GPU を作り直しても、なめらかさは保たれる
        Assert::AreEqual<int>(TT_OK, tt_view_set_sample_count(view, 4, nullptr));
        Assert::AreEqual<int>(TT_OK, tt_engine_simulate_device_lost(engine));
        Assert::AreEqual<int>(TT_OK, tt_engine_recover(engine));
        Assert::IsTrue(count(render(width, height), white) > 20);

        Assert::AreEqual<int>(TT_OK, tt_mesh_destroy(mesh));
        Assert::AreEqual<int>(TT_OK, tt_view_destroy(view));
        Assert::AreEqual<int>(TT_OK, tt_engine_destroy(engine));
    }

    TEST_METHOD(ColorGradingAndVignetteChangeTheScene)
    {
        // 画面いっぱいの四角形（橙色のテクスチャ）を、効果なしの色を基準にして比べる
        TtEngine* engine = CreateWarpEngine();
        constexpr int width = 64;
        constexpr int height = 48;
        TtView* view = nullptr;
        Assert::AreEqual<int>(TT_OK, tt_view_create_offscreen(engine, width, height, 0, &view));
        TtTexture* texture = CreateSolidTexture(engine, 60, 120, 200);
        const TtMeshMaterial material = QuadMaterial(texture);
        const TtMeshDesc desc = QuadDesc(kClockwise, &material);
        TtMesh* mesh = nullptr;
        Assert::AreEqual<int>(TT_OK, tt_mesh_create(engine, &desc, &mesh));
        Assert::AreEqual<int>(TT_OK, tt_texture_destroy(texture));

        auto render = [&](auto&& setup) {
            TtFrameParams params = MakeFrontParams();
            TtDrawItem item = DrawItem(mesh);
            DirectX::XMStoreFloat4x4(reinterpret_cast<DirectX::XMFLOAT4X4*>(item.world), DirectX::XMMatrixScaling(4.0f, 4.0f, 1.0f));
            params.items = &item;
            params.item_count = 1;
            params.exposure = 0.0f;
            params.contrast = 1.0f;
            params.saturation = 1.0f;
            setup(params);
            return RenderAndRead(engine, view, params, width, height);
        };
        const size_t center = (static_cast<size_t>(height / 2) * width + width / 2) * 4;
        const std::vector<uint8_t> base = render([](TtFrameParams&) {});
        const int b = base[center], g = base[center + 1], r = base[center + 2];
        Assert::IsTrue(r > g && g > b && b > 20, L"基準の色（橙色）");

        std::vector<uint8_t> brighter = render([](TtFrameParams& p) { p.post_flags = TT_POST_COLOR; p.exposure = 1.0f; });
        Assert::IsTrue(IsNear(brighter[center + 1], std::min(255, g * 2), 3) && IsNear(brighter[center], std::min(255, b * 2), 3),
            L"明るさ +1 で 2 倍");
        std::vector<uint8_t> gray = render([](TtFrameParams& p) { p.post_flags = TT_POST_COLOR; p.saturation = 0.0f; });
        const int luminance = static_cast<int>(std::lround(0.2126 * r + 0.7152 * g + 0.0722 * b));
        Assert::IsTrue(IsNear(gray[center], luminance, 2) && IsNear(gray[center + 1], luminance, 2) && IsNear(gray[center + 2], luminance, 2),
            L"彩度 0 で白黒（明るさは保つ）");
        std::vector<uint8_t> warm = render([](TtFrameParams& p) { p.post_flags = TT_POST_COLOR; p.temperature = 1.0f; });
        Assert::IsTrue(warm[center + 2] > r && warm[center] < b && IsNear(warm[center + 1], g, 2), L"色温度 +1 で赤が増え、青が減る");
        std::vector<uint8_t> flat = render([](TtFrameParams& p) { p.post_flags = TT_POST_COLOR; p.contrast = 0.0f; });
        Assert::IsTrue(IsNear(flat[center], 128, 2) && IsNear(flat[center + 2], 128, 2), L"コントラスト 0 で全部が中間の灰色");

        std::vector<uint8_t> vignette = render([](TtFrameParams& p) { p.post_flags = TT_POST_VIGNETTE; p.vignette = 1.0f; });
        Assert::IsTrue(IsNear(vignette[center + 2], r, 2), L"周辺減光は中央を変えない");
        Assert::IsTrue(vignette[2] < r / 3, L"四隅は暗くなる");

        std::vector<uint8_t> unknown = render([](TtFrameParams& p) {
            p.post_flags = TT_POST_COLOR;
            p.exposure = std::numeric_limits<float>::quiet_NaN();
        });
        Assert::IsTrue(IsNear(unknown[center + 2], r, 2) && IsNear(unknown[center], b, 2), L"数でない値は「変えない」");

        // なめらかに描くとき（MSAA）も同じ
        Assert::AreEqual<int>(TT_OK, tt_view_set_sample_count(view, 4, nullptr));
        std::vector<uint8_t> msaa = render([](TtFrameParams& p) { p.post_flags = TT_POST_COLOR; p.exposure = 1.0f; });
        Assert::IsTrue(IsNear(msaa[center + 1], std::min(255, g * 2), 3), L"MSAA でも色調補正がかかる");

        Assert::AreEqual<int>(TT_OK, tt_mesh_destroy(mesh));
        Assert::AreEqual<int>(TT_OK, tt_view_destroy(view));
        Assert::AreEqual<int>(TT_OK, tt_engine_destroy(engine));
    }

    TEST_METHOD(BloomGlowsAroundBrightAreas)
    {
        // 黒い背景の真ん中に小さな白い四角形。ブルームを使うと、四角形の外の近くが明るくなる
        TtEngine* engine = CreateWarpEngine();
        constexpr int width = 64;
        constexpr int height = 48;
        TtView* view = nullptr;
        Assert::AreEqual<int>(TT_OK, tt_view_create_offscreen(engine, width, height, 0, &view));
        TtTexture* texture = CreateSolidTexture(engine, 255, 255, 255);
        TtMeshMaterial material = QuadMaterial(texture);
        material.specular_power = 0.0f;
        const TtMeshDesc desc = QuadDesc(kClockwise, &material);
        TtMesh* mesh = nullptr;
        Assert::AreEqual<int>(TT_OK, tt_mesh_create(engine, &desc, &mesh));
        Assert::AreEqual<int>(TT_OK, tt_texture_destroy(texture));

        auto render = [&](uint32_t flags, float threshold, float alpha) {
            TtFrameParams params = MakeFrontParams();
            params.flags = TT_FRAME_CLEAR_COLOR;
            params.clear_color[3] = alpha; // 黒（alpha = 0 なら透明）
            TtDrawItem item = DrawItem(mesh);
            DirectX::XMStoreFloat4x4(reinterpret_cast<DirectX::XMFLOAT4X4*>(item.world), DirectX::XMMatrixScaling(0.3f, 0.3f, 1.0f));
            params.items = &item;
            params.item_count = 1;
            params.post_flags = flags;
            params.contrast = 1.0f;
            params.saturation = 1.0f;
            params.bloom_intensity = 1.0f;
            params.bloom_threshold = threshold;
            params.bloom_spread = 0.7f;
            return RenderAndRead(engine, view, params, width, height);
        };
        const size_t center = (static_cast<size_t>(height / 2) * width + width / 2) * 4;
        // 四角形（半幅 0.3、約 3 画素）の外、中央から 5 画素右
        const size_t nearby = (static_cast<size_t>(height / 2) * width + width / 2 + 5) * 4;

        std::vector<uint8_t> plain = render(0, 0.5f, 1.0f);
        Assert::IsTrue(plain[center + 1] > 200, L"四角形は白");
        Assert::AreEqual<int>(0, plain[nearby + 1], L"効果なしなら、外は黒のまま");

        std::vector<uint8_t> glow = render(TT_POST_BLOOM, 0.5f, 1.0f);
        Assert::IsTrue(glow[nearby + 1] > 2, L"ブルームで、外の近くが明るくなる");
        Assert::IsTrue(glow[center + 1] >= plain[center + 1], L"明るい所は暗くならない");
        Assert::AreEqual<int>(0, glow[0], L"遠くの隅までは届かない");

        std::vector<uint8_t> highThreshold = render(TT_POST_BLOOM, 1.0f, 1.0f);
        Assert::IsTrue(highThreshold[nearby + 1] < glow[nearby + 1], L"光り始める明るさを上げると、にじみは減る");

        std::vector<uint8_t> transparent = render(TT_POST_BLOOM, 0.5f, 0.0f);
        Assert::IsTrue(transparent[nearby + 3] > 0 && transparent[nearby + 1] > 0, L"透明な背景の上では、にじんだ光の分だけ不透明になる");
        Assert::AreEqual<int>(0, transparent[3], L"何も無い隅は透明のまま");

        Assert::AreEqual<int>(TT_OK, tt_mesh_destroy(mesh));
        Assert::AreEqual<int>(TT_OK, tt_view_destroy(view));
        Assert::AreEqual<int>(TT_OK, tt_engine_destroy(engine));
    }

    TEST_METHOD(BackgroundAndOverlaysAreNotGraded)
    {
        // 色調補正（暗く・白黒）をかけても、背景の色、重ね描きの線、いちばん奥の格子はそのまま。モデルだけが変わる
        TtEngine* engine = CreateWarpEngine();
        constexpr int width = 64;
        constexpr int height = 48;
        TtView* view = nullptr;
        Assert::AreEqual<int>(TT_OK, tt_view_create_offscreen(engine, width, height, 0, &view));
        TtTexture* red = CreateSolidTexture(engine, 0, 0, 255);
        const TtMeshMaterial material = QuadMaterial(red);
        const TtMeshDesc desc = QuadDesc(kClockwise, &material);
        TtMesh* mesh = nullptr;
        Assert::AreEqual<int>(TT_OK, tt_mesh_create(engine, &desc, &mesh));
        Assert::AreEqual<int>(TT_OK, tt_texture_destroy(red));
        // 四角形より奥を横切る白い線（奥行きを無視して手前に描く）
        const TtLineVertex lines[] = {
            {{-3.0f, 0.0f, 2.0f}, {1.0f, 1.0f, 1.0f, 1.0f}},
            {{3.0f, 0.0f, 2.0f}, {1.0f, 1.0f, 1.0f, 1.0f}},
        };

        for (uint32_t samples : {1u, 4u}) {
            Assert::AreEqual<int>(TT_OK, tt_view_set_sample_count(view, samples, nullptr));
            TtFrameParams params = MakeFrontParams();
            params.flags = TT_FRAME_CLEAR_COLOR | TT_FRAME_SHOW_GRID | TT_FRAME_GRID_BEHIND;
            params.clear_color[0] = 0.0f;
            params.clear_color[1] = 0.0f;
            params.clear_color[2] = 1.0f; // 青い背景
            params.clear_color[3] = 1.0f;
            DirectX::XMStoreFloat4x4(reinterpret_cast<DirectX::XMFLOAT4X4*>(params.grid_world),
                DirectX::XMMatrixScaling(0.25f, 0.25f, 0.25f) * DirectX::XMMatrixRotationX(-DirectX::XM_PIDIV2));
            TtDrawItem item = DrawItem(mesh);
            DirectX::XMStoreFloat4x4(reinterpret_cast<DirectX::XMFLOAT4X4*>(item.world), DirectX::XMMatrixTranslation(0.0f, 0.0f, 0.5f));
            params.items = &item;
            params.item_count = 1;
            params.overlay_lines = lines;
            params.overlay_vertex_count = 2;
            params.post_flags = TT_POST_COLOR;
            params.exposure = -1.0f;
            params.contrast = 1.0f;
            params.saturation = 0.0f;
            std::vector<uint8_t> pixels = RenderAndRead(engine, view, params, width, height);

            const uint8_t* model = &pixels[(static_cast<size_t>(height / 2 - 6) * width + width / 2) * 4];
            Assert::IsTrue(IsNear(model[0], model[2], 2) && model[2] < 60, L"モデルは暗く白黒になる");
            bool foundWhite = false;
            for (int y = height / 2 - 1; y <= height / 2; ++y) {
                const uint8_t* p = &pixels[(static_cast<size_t>(y) * width + width / 2) * 4];
                // MSAA では線が 2 行にまたがって少し混ざるが、色調補正（暗く）がかかっていれば 128 以下になる
                foundWhite |= p[0] > 150 && p[1] > 150 && p[2] > 150;
            }
            Assert::IsTrue(foundWhite, L"重ね描きの線は暗くならずに、モデルの上に出る");
            // 背景の所（上の 8 行）には、そのままの青と、格子の線の両方がある
            int pureBlue = 0;
            int gridOutside = 0;
            for (int y = 0; y < 8; ++y) {
                for (int x = 0; x < width; ++x) {
                    const uint8_t* p = &pixels[(static_cast<size_t>(y) * width + x) * 4];
                    const bool blue = p[0] > 250 && p[1] < 5 && p[2] < 5;
                    pureBlue += blue ? 1 : 0;
                    gridOutside += blue ? 0 : 1;
                }
            }
            Assert::IsTrue(pureBlue > 0, L"背景の青はそのまま");
            Assert::IsTrue(gridOutside > 0, L"背景の所には格子が出る");
            // 四角形の内側（重ね描きの線の近くは除く）は、モデルの色だけ（いちばん奥の格子はモデルに隠れる）
            int gridInside = 0;
            for (int y = 17; y <= 31; ++y) {
                if (y >= height / 2 - 2 && y <= height / 2 + 1) {
                    continue;
                }
                for (int x = 25; x <= 39; ++x) {
                    const uint8_t* p = &pixels[(static_cast<size_t>(y) * width + x) * 4];
                    gridInside += (std::abs(p[0] - model[0]) > 6 || std::abs(p[2] - model[2]) > 6) ? 1 : 0;
                }
            }
            Assert::AreEqual(0, gridInside, L"いちばん奥の格子は、モデルに隠れる");
        }

        Assert::AreEqual<int>(TT_OK, tt_mesh_destroy(mesh));
        Assert::AreEqual<int>(TT_OK, tt_view_destroy(view));
        Assert::AreEqual<int>(TT_OK, tt_engine_destroy(engine));
    }

    TEST_METHOD(LinesOutlineSilhouettesAndSteps)
    {
        // 黒い背景に白い大きな四角形、その手前に小さな白い四角形。線画（赤）は、外側の輪郭と、手前の四角形の縁に引かれる
        TtEngine* engine = CreateWarpEngine();
        constexpr int width = 64;
        constexpr int height = 48;
        TtView* view = nullptr;
        Assert::AreEqual<int>(TT_OK, tt_view_create_offscreen(engine, width, height, 0, &view));
        TtTexture* texture = CreateSolidTexture(engine, 255, 255, 255);
        const TtMeshMaterial material = QuadMaterial(texture);
        const TtMeshDesc desc = QuadDesc(kClockwise, &material);
        TtMesh* mesh = nullptr;
        Assert::AreEqual<int>(TT_OK, tt_mesh_create(engine, &desc, &mesh));
        Assert::AreEqual<int>(TT_OK, tt_texture_destroy(texture));

        auto render = [&](uint32_t flags, float frontZ, float detail, bool orthographic) {
            TtFrameParams params = MakeFrontParams();
            if (orthographic) {
                DirectX::XMStoreFloat4x4(reinterpret_cast<DirectX::XMFLOAT4X4*>(params.projection),
                    DirectX::XMMatrixOrthographicLH(5.5f, 4.14f, 0.1f, 100.0f));
            }
            params.flags = TT_FRAME_CLEAR_COLOR;
            params.clear_color[3] = 1.0f; // 黒
            TtDrawItem items[2] = {DrawItem(mesh), DrawItem(mesh)};
            DirectX::XMStoreFloat4x4(reinterpret_cast<DirectX::XMFLOAT4X4*>(items[0].world), DirectX::XMMatrixScaling(0.6f, 0.6f, 1.0f));
            DirectX::XMStoreFloat4x4(reinterpret_cast<DirectX::XMFLOAT4X4*>(items[1].world),
                DirectX::XMMatrixScaling(0.25f, 0.25f, 1.0f) * DirectX::XMMatrixTranslation(0.0f, 0.0f, frontZ));
            params.items = items;
            params.item_count = 2;
            params.post_flags = flags;
            params.contrast = 1.0f;
            params.saturation = 1.0f;
            params.line_color[0] = 1.0f; // 赤
            params.line_width = 1.0f;
            params.line_strength = 1.0f;
            params.line_detail = detail;
            return RenderAndRead(engine, view, params, width, height);
        };
        auto isRed = [](const uint8_t* p) { return p[2] > 180 && p[1] < 110 && p[0] < 110; };
        // 真ん中の行で、赤い画素の x（中央からの距離）を集める
        auto redColumns = [&](const std::vector<uint8_t>& pixels) {
            std::vector<int> found;
            for (int x = 0; x < width; ++x) {
                if (isRed(&pixels[(static_cast<size_t>(height / 2) * width + x) * 4])) {
                    found.push_back(x - width / 2);
                }
            }
            return found;
        };
        auto hasNear = [](const std::vector<int>& xs, int from, int to) {
            return std::any_of(xs.begin(), xs.end(), [&](int x) { return std::abs(x) >= from && std::abs(x) <= to; });
        };

        Assert::IsTrue(redColumns(render(0, -0.5f, 0.5f, false)).empty(), L"線画なしなら赤い線は無い");
        for (uint32_t samples : {1u, 4u}) {
            Assert::AreEqual<int>(TT_OK, tt_view_set_sample_count(view, samples, nullptr));
            std::vector<uint8_t> pixels = render(TT_POST_LINES, -0.5f, 0.5f, false);
            std::vector<int> xs = redColumns(pixels);
            Assert::IsTrue(hasNear(xs, 5, 8), L"外側の輪郭に線を引く");
            Assert::IsTrue(hasNear(xs, 2, 4), L"手前の四角形の縁にも線を引く");
            Assert::AreEqual<int>(0, pixels[1], L"背景には線を引かない");
            const uint8_t* center = &pixels[(static_cast<size_t>(height / 2) * width + width / 2) * 4];
            Assert::IsTrue(center[1] > 200, L"面の真ん中は白のまま");
        }
        Assert::AreEqual<int>(TT_OK, tt_view_set_sample_count(view, 1, nullptr));

        // 小さな段差（奥行きの差が 2% ほど）は、細かさを上げたときだけ線になる
        Assert::IsFalse(hasNear(redColumns(render(TT_POST_LINES, -0.1f, 0.0f, false)), 2, 4), L"細かさ 0 では小さな段差に線を引かない");
        Assert::IsTrue(hasNear(redColumns(render(TT_POST_LINES, -0.1f, 1.0f, false)), 2, 4), L"細かさ 1 なら引く");

        // 平行投影（固定視点）でも引ける
        std::vector<int> ortho = redColumns(render(TT_POST_LINES, -0.5f, 1.0f, true));
        Assert::IsFalse(ortho.empty(), L"平行投影でも線を引く");

        Assert::AreEqual<int>(TT_OK, tt_mesh_destroy(mesh));
        Assert::AreEqual<int>(TT_OK, tt_view_destroy(view));
        Assert::AreEqual<int>(TT_OK, tt_engine_destroy(engine));
    }

    TEST_METHOD(PaintShadowTintAndPaperChangeTheLook)
    {
        TtEngine* engine = CreateWarpEngine();
        constexpr int width = 64;
        constexpr int height = 48;
        TtView* view = nullptr;
        Assert::AreEqual<int>(TT_OK, tt_view_create_offscreen(engine, width, height, 0, &view));
        auto makeQuad = [&](uint8_t gray) {
            TtTexture* texture = CreateSolidTexture(engine, gray, gray, gray);
            const TtMeshMaterial material = QuadMaterial(texture);
            const TtMeshDesc desc = QuadDesc(kClockwise, &material);
            TtMesh* mesh = nullptr;
            Assert::AreEqual<int>(TT_OK, tt_mesh_create(engine, &desc, &mesh));
            Assert::AreEqual<int>(TT_OK, tt_texture_destroy(texture));
            return mesh;
        };
        TtMesh* dark = makeQuad(70);
        TtMesh* bright = makeQuad(235);
        // 画面いっぱいの四角形を描いて、真ん中の行を読む
        auto render = [&](TtMesh* mesh, auto&& setup) {
            TtFrameParams params = MakeFrontParams();
            params.flags = TT_FRAME_CLEAR_COLOR;
            params.clear_color[3] = 1.0f;
            TtDrawItem item = DrawItem(mesh);
            DirectX::XMStoreFloat4x4(reinterpret_cast<DirectX::XMFLOAT4X4*>(item.world), DirectX::XMMatrixScaling(4.0f, 4.0f, 1.0f));
            params.items = &item;
            params.item_count = 1;
            params.contrast = 1.0f;
            params.saturation = 1.0f;
            setup(params);
            return RenderAndRead(engine, view, params, width, height);
        };
        const size_t center = (static_cast<size_t>(height / 2) * width + width / 2) * 4;

        // 塗り: 3 段（黒・中間・白）のどれかになる
        for (TtMesh* mesh : {dark, bright}) {
            std::vector<uint8_t> painted = render(mesh, [](TtFrameParams& p) { p.post_flags = TT_POST_POSTERIZE; p.posterize_levels = 3; });
            const int g = painted[center + 1];
            Assert::IsTrue(g <= 2 || std::abs(g - 128) <= 3 || g >= 252, L"塗りは決まった段のどれか");
        }
        std::vector<uint8_t> darkPainted = render(dark, [](TtFrameParams& p) { p.post_flags = TT_POST_POSTERIZE; p.posterize_levels = 2; });
        std::vector<uint8_t> brightPainted = render(bright, [](TtFrameParams& p) { p.post_flags = TT_POST_POSTERIZE; p.posterize_levels = 2; });
        Assert::IsTrue(darkPainted[center + 1] <= 2 && brightPainted[center + 1] >= 252, L"2 段なら、暗い色は黒・明るい色は白");

        // 影の色（青）: 暗い所は青みがかり、明るい所はほとんど変わらない
        auto blueTint = [](TtFrameParams& p) {
            p.post_flags = TT_POST_SHADOW_TINT;
            p.shadow_tint[2] = 1.0f;
            p.shadow_tint_strength = 1.0f;
        };
        std::vector<uint8_t> darkTinted = render(dark, blueTint);
        Assert::IsTrue(darkTinted[center] > darkTinted[center + 2] + 20, L"暗い所は青みがかる");
        std::vector<uint8_t> brightTinted = render(bright, blueTint);
        Assert::IsTrue(std::abs(brightTinted[center] - brightTinted[center + 2]) <= 3, L"明るい所はほとんど変わらない");

        // 紙の質感: 一様な面にむらができる
        auto spread = [&](const std::vector<uint8_t>& pixels) {
            int lo = 255;
            int hi = 0;
            for (int x = 0; x < width; ++x) {
                const int g = pixels[(static_cast<size_t>(height / 2) * width + x) * 4 + 1];
                lo = std::min(lo, g);
                hi = std::max(hi, g);
            }
            return hi - lo;
        };
        Assert::IsTrue(spread(render(bright, [](TtFrameParams&) {})) <= 2, L"効果なしなら一様");
        Assert::IsTrue(spread(render(bright, [](TtFrameParams& p) {
            p.post_flags = TT_POST_PAPER;
            p.paper_strength = 1.0f;
            p.paper_grain = 2.0f;
        })) > 15, L"紙の質感でむらができる");

        Assert::AreEqual<int>(TT_OK, tt_mesh_destroy(dark));
        Assert::AreEqual<int>(TT_OK, tt_mesh_destroy(bright));
        Assert::AreEqual<int>(TT_OK, tt_view_destroy(view));
        Assert::AreEqual<int>(TT_OK, tt_engine_destroy(engine));
    }

    TEST_METHOD(PbrMaterialsReflectLightAndEmissiveGlows)
    {
        // 正面を向いた灰色の四角形を、カメラの後ろから照らす。拡張材質（PBR）と発光を、材質の今の値で切り替える
        TtEngine* engine = CreateWarpEngine();
        constexpr int width = 64;
        constexpr int height = 48;
        TtView* view = nullptr;
        Assert::AreEqual<int>(TT_OK, tt_view_create_offscreen(engine, width, height, 0, &view));
        TtTexture* texture = CreateSolidTexture(engine, 128, 128, 128);
        const TtMeshMaterial material = QuadMaterial(texture);
        const TtMeshDesc desc = QuadDesc(kClockwise, &material);
        TtMesh* mesh = nullptr;
        Assert::AreEqual<int>(TT_OK, tt_mesh_create(engine, &desc, &mesh));
        Assert::AreEqual<int>(TT_OK, tt_texture_destroy(texture));
        Assert::AreEqual<size_t>(208, sizeof(TtMaterialState), L"C# 側と同じ大きさ（API 0.13）");

        auto state = [](uint32_t shading, float metallic, float roughness) {
            TtMaterialState s{};
            s.diffuse[0] = s.diffuse[1] = s.diffuse[2] = s.diffuse[3] = 1.0f;
            s.ambient[0] = s.ambient[1] = s.ambient[2] = 1.0f;
            for (float* v : {s.texture_multiply, s.sphere_multiply, s.toon_multiply, s.base_tint}) {
                v[0] = v[1] = v[2] = v[3] = 1.0f;
            }
            s.edge_color[3] = 1.0f;
            s.shading = shading;
            s.metallic = metallic;
            s.roughness = roughness;
            return s;
        };
        auto render = [&](const TtMaterialState& s, float light, float scale = 1.0f) {
            Assert::AreEqual<int>(TT_OK, tt_mesh_update_materials(mesh, &s, 1));
            TtFrameParams params = MakeFrontParams();
            params.flags = TT_FRAME_USE_LIGHT;
            params.light_direction[2] = 1.0f; // カメラの後ろから、四角形へ向かう光
            params.light_color[0] = params.light_color[1] = params.light_color[2] = light;
            TtDrawItem item = DrawItem(mesh);
            DirectX::XMStoreFloat4x4(reinterpret_cast<DirectX::XMFLOAT4X4*>(item.world), DirectX::XMMatrixScaling(scale, scale, 1.0f));
            params.items = &item;
            params.item_count = 1;
            return RenderAndRead(engine, view, params, width, height);
        };
        const size_t center = (static_cast<size_t>(height / 2) * width + width / 2) * 4;

        // 照明を消すと、トゥーンの材質は環境色で明るいままだが、PBR の材質は暗くなる（光を返すだけなので）
        std::vector<uint8_t> toonDark = render(state(TT_SHADING_TOON, 0.0f, 0.5f), 0.0f);
        std::vector<uint8_t> pbrDark = render(state(TT_SHADING_PBR, 0.0f, 0.5f), 0.0f);
        Assert::IsTrue(toonDark[center + 1] > 100, L"トゥーンは照明なしでも環境色で見える");
        Assert::IsTrue(pbrDark[center + 1] < 5, L"PBR は照明なしなら暗い");
        std::vector<uint8_t> pbrLit = render(state(TT_SHADING_PBR, 0.0f, 0.5f), 0.6f);
        Assert::IsTrue(pbrLit[center + 1] > 60 && pbrLit[center + 1] < 250, L"PBR は照らすと、ベースカラーに応じた明るさになる");

        // 知らない色の計算の値はトゥーンとして描く
        std::vector<uint8_t> unknown = render(state(7, 0.0f, 0.5f), 0.0f);
        Assert::IsTrue(IsNear(unknown[center + 1], toonDark[center + 1], 2), L"知らない値はトゥーン");

        // 金属で滑らか（粗さ 0.2）なほど、正面の照り返しが強い
        std::vector<uint8_t> smooth = render(state(TT_SHADING_PBR, 1.0f, 0.2f), 0.6f);
        std::vector<uint8_t> rough = render(state(TT_SHADING_PBR, 1.0f, 0.9f), 0.6f);
        Assert::IsTrue(smooth[center + 1] > rough[center + 1] + 20, L"滑らかな金属は、正面の照り返しが強い");
        // 画面いっぱいに広げると、見る向きが光から大きくずれる画面の端では、照り返しがほとんど無い
        std::vector<uint8_t> wide = render(state(TT_SHADING_PBR, 1.0f, 0.2f), 0.6f, 4.0f);
        const size_t side = (static_cast<size_t>(height / 2) * width + 2) * 4;
        Assert::IsTrue(wide[center + 1] > 250 && wide[side + 1] < 150, L"照り返しは正面がいちばん強い");

        // 発光は照明に関係なく足す（どちらの色の計算にも）
        TtMaterialState glowing = state(TT_SHADING_PBR, 0.0f, 0.5f);
        glowing.emissive[0] = 0.5f;
        std::vector<uint8_t> emissive = render(glowing, 0.0f);
        Assert::IsTrue(IsNear(emissive[center + 2], 128, 3) && emissive[center + 1] < 5, L"照明なしでも、発光の赤が出る");
        TtMaterialState toonGlow = state(TT_SHADING_TOON, 0.0f, 0.5f);
        toonGlow.emissive[1] = 1.0f;
        toonGlow.ambient[0] = toonGlow.ambient[1] = toonGlow.ambient[2] = 0.0f;
        std::vector<uint8_t> toonEmissive = render(toonGlow, 0.0f);
        Assert::IsTrue(toonEmissive[center + 1] > 250 && toonEmissive[center + 2] < 5, L"トゥーンの材質にも発光を足す");

        Assert::AreEqual<int>(TT_OK, tt_mesh_destroy(mesh));
        Assert::AreEqual<int>(TT_OK, tt_view_destroy(view));
        Assert::AreEqual<int>(TT_OK, tt_engine_destroy(engine));
    }

    TEST_METHOD(ExtraLightsBrightenModels)
    {
        // メインの照明を消し、環境色 0 の白い四角形（画面いっぱい）を、足す照明だけで照らす
        TtEngine* engine = CreateWarpEngine();
        constexpr int width = 64;
        constexpr int height = 48;
        TtView* view = nullptr;
        Assert::AreEqual<int>(TT_OK, tt_view_create_offscreen(engine, width, height, 0, &view));
        TtTexture* texture = CreateSolidTexture(engine, 255, 255, 255);
        TtMeshMaterial material = QuadMaterial(texture);
        material.ambient[0] = material.ambient[1] = material.ambient[2] = 0.0f;
        const TtMeshDesc desc = QuadDesc(kClockwise, &material);
        TtMesh* mesh = nullptr;
        Assert::AreEqual<int>(TT_OK, tt_mesh_create(engine, &desc, &mesh));
        Assert::AreEqual<int>(TT_OK, tt_texture_destroy(texture));

        auto render = [&](const TtLight* lights, uint32_t count) {
            TtFrameParams params = MakeFrontParams();
            params.flags = TT_FRAME_USE_LIGHT;
            params.light_direction[2] = 1.0f;
            TtDrawItem item = DrawItem(mesh);
            DirectX::XMStoreFloat4x4(reinterpret_cast<DirectX::XMFLOAT4X4*>(item.world), DirectX::XMMatrixScaling(4.0f, 4.0f, 1.0f));
            params.items = &item;
            params.item_count = 1;
            params.lights = lights;
            params.light_count = count;
            return RenderAndRead(engine, view, params, width, height);
        };
        auto light = [](uint32_t type, float x, float y, float z, float r, float g, float b, float range) {
            TtLight l{};
            l.type = type;
            l.position[0] = x;
            l.position[1] = y;
            l.position[2] = z;
            l.direction[2] = 1.0f; // 四角形へ向かう
            l.range = range;
            l.color[0] = r;
            l.color[1] = g;
            l.color[2] = b;
            l.spot_angle = 20.0f;
            l.spot_softness = 0.0f;
            return l;
        };
        const size_t center = (static_cast<size_t>(height / 2) * width + width / 2) * 4;
        const size_t corner = 0;
        const size_t side = (static_cast<size_t>(height / 2) * width + width / 2 + 12) * 4;

        std::vector<uint8_t> dark = render(nullptr, 0);
        Assert::IsTrue(dark[center + 2] <= 2, L"照明なし・環境色 0 なら黒");

        // 赤い点光源（手前 1、届く距離 3）: 近い真ん中は赤く、遠い隅は届かない
        const TtLight red = light(TT_LIGHT_POINT, 0.0f, 0.0f, -1.0f, 1.0f, 0.0f, 0.0f, 3.0f);
        std::vector<uint8_t> point = render(&red, 1);
        Assert::IsTrue(point[center + 2] > 80 && point[center + 1] < 5, L"点光源の色で明るくなる");
        Assert::IsTrue(point[corner + 2] < 5, L"届く距離の外は暗いまま");

        // 緑の平行光: どこも同じように明るい
        const TtLight green = light(TT_LIGHT_DIRECTIONAL, 0.0f, 0.0f, 0.0f, 0.0f, 1.0f, 0.0f, 1.0f);
        std::vector<uint8_t> directional = render(&green, 1);
        Assert::IsTrue(directional[center + 1] > 250 && directional[corner + 1] > 250, L"平行光は距離に関係なく届く");

        // スポットライト（手前 2、全体 20°）: 円すいの中だけ明るい
        const TtLight spot = light(TT_LIGHT_SPOT, 0.0f, 0.0f, -2.0f, 1.0f, 1.0f, 1.0f, 10.0f);
        std::vector<uint8_t> spotted = render(&spot, 1);
        Assert::IsTrue(spotted[center + 1] > 100, L"円すいの中は明るい");
        Assert::IsTrue(spotted[side + 1] < 5, L"円すいの外は暗い");

        // 数でない値の照明は飛ばし、ほかの照明は使う。一覧が NULL なのに数があるのは誤り
        TtLight broken = red;
        broken.position[0] = std::numeric_limits<float>::quiet_NaN();
        const TtLight both[] = {broken, green};
        std::vector<uint8_t> mixed = render(both, 2);
        Assert::IsTrue(mixed[center + 1] > 250 && mixed[center + 2] < 5, L"使えない照明だけを飛ばす");
        TtFrameParams bad = MakeFrontParams();
        bad.light_count = 1;
        Assert::AreEqual<int>(TT_E_INVALID_ARG, tt_view_render(view, &bad));

        // PBR の材質も照らす
        TtMaterialState pbr{};
        pbr.diffuse[0] = pbr.diffuse[1] = pbr.diffuse[2] = pbr.diffuse[3] = 1.0f;
        for (float* v : {pbr.texture_multiply, pbr.sphere_multiply, pbr.toon_multiply, pbr.base_tint}) {
            v[0] = v[1] = v[2] = v[3] = 1.0f;
        }
        pbr.edge_color[3] = 1.0f;
        pbr.shading = TT_SHADING_PBR;
        pbr.roughness = 0.6f;
        Assert::AreEqual<int>(TT_OK, tt_mesh_update_materials(mesh, &pbr, 1));
        std::vector<uint8_t> pbrPoint = render(&red, 1);
        Assert::IsTrue(pbrPoint[center + 2] > 60 && pbrPoint[center + 1] < 10, L"PBR の材質も点光源の色で明るくなる");

        Assert::AreEqual<int>(TT_OK, tt_mesh_destroy(mesh));
        Assert::AreEqual<int>(TT_OK, tt_view_destroy(view));
        Assert::AreEqual<int>(TT_OK, tt_engine_destroy(engine));
    }

    TEST_METHOD(FloorReflectionMirrorsModelsBelowTheFloor)
    {
        // 高さ 0.5〜1.5 に立てた赤い四角形を、斜め上から見る。床の映り込みを使うと、床の下（高さ -0.5〜-1.5）に赤く映る
        TtEngine* engine = CreateWarpEngine();
        constexpr int width = 64;
        constexpr int height = 48;
        TtView* view = nullptr;
        Assert::AreEqual<int>(TT_OK, tt_view_create_offscreen(engine, width, height, 0, &view));
        TtTexture* red = CreateSolidTexture(engine, 0, 0, 255);
        TtMeshMaterial material = QuadMaterial(red, TT_MATERIAL_DOUBLE_SIDED);
        const TtMeshDesc desc = QuadDesc(kClockwise, &material);
        TtMesh* mesh = nullptr;
        Assert::AreEqual<int>(TT_OK, tt_mesh_create(engine, &desc, &mesh));
        Assert::AreEqual<int>(TT_OK, tt_texture_destroy(red));

        using namespace DirectX;
        auto render = [&](uint32_t flags, float strength, float fade, float eyeY) {
            TtFrameParams params = MakeParams(flags);
            XMStoreFloat4x4(reinterpret_cast<XMFLOAT4X4*>(params.view),
                XMMatrixLookAtLH(XMVectorSet(0.0f, eyeY, -6.0f, 1.0f), XMVectorSet(0.0f, 0.0f, 0.0f, 1.0f), XMVectorSet(0.0f, 1.0f, 0.0f, 0.0f)));
            TtDrawItem item = DrawItem(mesh);
            XMStoreFloat4x4(reinterpret_cast<XMFLOAT4X4*>(item.world), XMMatrixScaling(0.5f, 0.5f, 1.0f) * XMMatrixTranslation(0.0f, 1.0f, 0.0f));
            params.items = &item;
            params.item_count = 1;
            params.reflection_strength = strength;
            params.reflection_fade = fade;
            return std::make_pair(RenderAndRead(engine, view, params, width, height), params);
        };
        // ワールドの点が映る画素
        auto pixelOf = [&](const TtFrameParams& params, float x, float y, float z) {
            const XMMATRIX viewProj = XMLoadFloat4x4(reinterpret_cast<const XMFLOAT4X4*>(params.view))
                * XMLoadFloat4x4(reinterpret_cast<const XMFLOAT4X4*>(params.projection));
            XMVECTOR p = XMVector3TransformCoord(XMVectorSet(x, y, z, 1.0f), viewProj);
            const int px = static_cast<int>((XMVectorGetX(p) * 0.5f + 0.5f) * width);
            const int py = static_cast<int>((-XMVectorGetY(p) * 0.5f + 0.5f) * height);
            return (static_cast<size_t>(py) * width + px) * 4;
        };

        auto [plain, plainParams] = render(0, 1.0f, 10.0f, 2.0f);
        const size_t above = pixelOf(plainParams, 0.0f, 1.0f, 0.0f);
        const size_t below = pixelOf(plainParams, 0.0f, -0.8f, 0.0f);
        Assert::IsTrue(plain[above + 2] > 150, L"四角形は赤");
        Assert::IsTrue(IsBackground(&plain[below], 1), L"映り込みなしなら、床の下は背景");

        auto [mirrored, mirroredParams] = render(TT_FRAME_FLOOR_REFLECTION, 0.8f, 10.0f, 2.0f);
        Assert::IsTrue(mirrored[below + 2] > 120 && mirrored[below + 1] < 60, L"床の下に赤く映る");
        Assert::IsTrue(IsNear(mirrored[above + 2], plain[above + 2], 2), L"もとの四角形は変わらない");

        auto [faded, fadedParams] = render(TT_FRAME_FLOOR_REFLECTION, 0.8f, 0.3f, 2.0f);
        Assert::IsTrue(IsBackground(&faded[below], 2), L"消えるまでの距離より深い所は映らない");
        auto [none, noneParams] = render(TT_FRAME_FLOOR_REFLECTION, 0.0f, 10.0f, 2.0f);
        Assert::IsTrue(IsBackground(&none[below], 1), L"濃さ 0 なら映らない");

        // 床の下から見るときは映さない（床の裏）
        auto [under, underParams] = render(0, 1.0f, 10.0f, -2.0f);
        auto [underMirrored, underMirroredParams] = render(TT_FRAME_FLOOR_REFLECTION, 1.0f, 10.0f, -2.0f);
        Assert::IsTrue(under == underMirrored, L"床の下から見ると、映り込みは描かない");

        Assert::AreEqual<int>(TT_OK, tt_mesh_destroy(mesh));
        Assert::AreEqual<int>(TT_OK, tt_view_destroy(view));
        Assert::AreEqual<int>(TT_OK, tt_engine_destroy(engine));
    }

    TEST_METHOD(FloorReflectionBlurGrowsWithDepthBelowTheFloor)
    {
        // 高さ 0.5〜1.5 に立てた赤い四角形の映り込み（床の下の -0.5〜-1.5）をぼかす。床から深い所ほど、縁の外へ広がる
        TtEngine* engine = CreateWarpEngine();
        constexpr int width = 128;
        constexpr int height = 96;
        TtView* view = nullptr;
        Assert::AreEqual<int>(TT_OK, tt_view_create_offscreen(engine, width, height, 0, &view));
        TtTexture* red = CreateSolidTexture(engine, 0, 0, 255);
        TtMeshMaterial material = QuadMaterial(red, TT_MATERIAL_DOUBLE_SIDED);
        const TtMeshDesc desc = QuadDesc(kClockwise, &material);
        TtMesh* mesh = nullptr;
        Assert::AreEqual<int>(TT_OK, tt_mesh_create(engine, &desc, &mesh));
        Assert::AreEqual<int>(TT_OK, tt_texture_destroy(red));

        using namespace DirectX;
        TtFrameParams params = MakeParams(TT_FRAME_FLOOR_REFLECTION);
        XMStoreFloat4x4(reinterpret_cast<XMFLOAT4X4*>(params.view),
            XMMatrixLookAtLH(XMVectorSet(0.0f, 2.0f, -6.0f, 1.0f), XMVectorSet(0.0f, 0.0f, 0.0f, 1.0f), XMVectorSet(0.0f, 1.0f, 0.0f, 0.0f)));
        TtDrawItem item = DrawItem(mesh);
        XMStoreFloat4x4(reinterpret_cast<XMFLOAT4X4*>(item.world), XMMatrixScaling(0.5f, 0.5f, 1.0f) * XMMatrixTranslation(0.0f, 1.0f, 0.0f));
        params.items = &item;
        params.item_count = 1;
        params.reflection_strength = 0.8f;
        params.reflection_fade = 10.0f;
        auto render = [&](float blur) {
            params.reflection_blur = blur;
            return RenderAndRead(engine, view, params, width, height);
        };
        // ワールドの点が映る画素
        const XMMATRIX viewProj = XMLoadFloat4x4(reinterpret_cast<const XMFLOAT4X4*>(params.view))
            * XMLoadFloat4x4(reinterpret_cast<const XMFLOAT4X4*>(params.projection));
        auto pixelOf = [&](float x, float y) {
            XMVECTOR p = XMVector3TransformCoord(XMVectorSet(x, y, 0.0f, 1.0f), viewProj);
            const int px = static_cast<int>((XMVectorGetX(p) * 0.5f + 0.5f) * width);
            const int py = static_cast<int>((-XMVectorGetY(p) * 0.5f + 0.5f) * height);
            return (static_cast<size_t>(py) * width + px) * 4;
        };

        const std::vector<uint8_t> sharp = render(0.0f);
        // ごく小さいぼかし: 別の画像に描いて重ねる道を通っても、くっきりの映り込みとほとんど同じ（位置がずれない・濃さが変わらない）
        const std::vector<uint8_t> tiny = render(0.001f);
        int worst = 0;
        for (size_t i = 0; i < sharp.size(); ++i) {
            worst = std::max(worst, std::abs(static_cast<int>(sharp[i]) - static_cast<int>(tiny[i])));
        }
        Assert::IsTrue(worst <= 3, L"ごく小さいぼかしは、くっきりの映り込みと同じ");

        const std::vector<uint8_t> blurred = render(1.0f);
        const size_t above = pixelOf(0.0f, 1.0f);
        Assert::IsTrue(IsNear(blurred[above + 2], sharp[above + 2], 2), L"もとの四角形はぼかさない");
        const size_t inside = pixelOf(0.0f, -1.0f);
        Assert::IsTrue(blurred[inside + 2] > kBackgroundR + 60, L"映り込みの真ん中は赤いまま");
        // 映り込みの右の縁（x = 0.5）から 4 画素外の、背景より赤い分
        auto outside = [&](const std::vector<uint8_t>& image, float depth) {
            const size_t edge = pixelOf(0.5f, -depth);
            return static_cast<int>(image[edge + 4 * 4 + 2]) - static_cast<int>(kBackgroundR);
        };
        Assert::IsTrue(std::abs(outside(sharp, 0.6f)) <= 1 && std::abs(outside(sharp, 1.4f)) <= 1, L"くっきりなら、縁の外は背景");
        const int shallow = outside(blurred, 0.6f);
        const int deep = outside(blurred, 1.4f);
        Assert::IsTrue(deep > 10, L"床から深い所は、縁の外へぼけて広がる");
        Assert::IsTrue(deep > shallow + 5, L"床に近い所ほどくっきり");

        // 映像効果を使うとき（背景なしで描いてから仕上げる道）も同じようにぼかす
        params.post_flags = TT_POST_VIGNETTE;
        params.vignette = 0.0f;
        const std::vector<uint8_t> post = render(1.0f);
        Assert::IsTrue(IsNear(post[pixelOf(0.5f, -1.4f) + 4 * 4 + 2], blurred[pixelOf(0.5f, -1.4f) + 4 * 4 + 2], 3),
            L"映像効果を使っても、ぼかした映り込みは同じ");

        Assert::AreEqual<int>(TT_OK, tt_mesh_destroy(mesh));
        Assert::AreEqual<int>(TT_OK, tt_view_destroy(view));
        Assert::AreEqual<int>(TT_OK, tt_engine_destroy(engine));
    }

    TEST_METHOD(ScreenReflectionsMirrorObjectsOnGlossyPbrFloors)
    {
        // 滑らかな金属（PBR）の床の奥に、赤い四角形を立てる。画面の中の反射を使うと、床の手前に赤い四角形が映る。
        // 探す距離が短いとき・トゥーンの床では、今までどおり環境の映り込み（分けて書いた環境の映り込みを、そのまま戻す）
        TtEngine* engine = CreateWarpEngine();
        constexpr int width = 96;
        constexpr int height = 72;
        TtView* view = nullptr;
        Assert::AreEqual<int>(TT_OK, tt_view_create_offscreen(engine, width, height, 0, &view));
        auto makeQuad = [&](uint8_t b, uint8_t g, uint8_t r) {
            TtTexture* texture = CreateSolidTexture(engine, b, g, r);
            const TtMeshMaterial material = QuadMaterial(texture, TT_MATERIAL_DOUBLE_SIDED);
            const TtMeshDesc desc = QuadDesc(kClockwise, &material);
            TtMesh* mesh = nullptr;
            Assert::AreEqual<int>(TT_OK, tt_mesh_create(engine, &desc, &mesh));
            Assert::AreEqual<int>(TT_OK, tt_texture_destroy(texture));
            return mesh;
        };
        TtMesh* floor = makeQuad(255, 255, 255);
        TtMesh* wall = makeQuad(0, 0, 255);
        auto state = [](uint32_t shading) {
            TtMaterialState s{};
            s.diffuse[0] = s.diffuse[1] = s.diffuse[2] = s.diffuse[3] = 1.0f;
            s.ambient[0] = s.ambient[1] = s.ambient[2] = 0.5f;
            for (float* v : {s.texture_multiply, s.sphere_multiply, s.toon_multiply, s.base_tint}) {
                v[0] = v[1] = v[2] = v[3] = 1.0f;
            }
            s.edge_color[3] = 1.0f;
            s.shading = shading;
            s.metallic = 1.0f;
            s.roughness = 0.05f;
            return s;
        };

        using namespace DirectX;
        TtDrawItem items[2] = {DrawItem(floor), DrawItem(wall)};
        // 床: 2 x 2 の四角形を寝かせる（高さ 0、奥行き -2〜2）。壁: 高さ 0.1〜1.3・奥行き 0.8 に立てる
        XMStoreFloat4x4(reinterpret_cast<XMFLOAT4X4*>(items[0].world), XMMatrixScaling(2.0f, 2.0f, 1.0f) * XMMatrixRotationX(XM_PIDIV2));
        XMStoreFloat4x4(reinterpret_cast<XMFLOAT4X4*>(items[1].world), XMMatrixScaling(0.6f, 0.6f, 1.0f) * XMMatrixTranslation(0.0f, 0.7f, 0.8f));
        TtFrameParams params = MakeParams(TT_FRAME_USE_LIGHT);
        XMStoreFloat4x4(reinterpret_cast<XMFLOAT4X4*>(params.view),
            XMMatrixLookAtLH(XMVectorSet(0.0f, 1.2f, -4.0f, 1.0f), XMVectorSet(0.0f, 0.4f, 0.0f, 1.0f), XMVectorSet(0.0f, 1.0f, 0.0f, 0.0f)));
        params.light_direction[0] = 0.5f;
        params.light_direction[1] = -1.0f;
        params.light_direction[2] = 0.5f;
        params.light_color[0] = params.light_color[1] = params.light_color[2] = 0.6f;
        params.items = items;
        params.item_count = 2;
        params.vignette = 0.0f;
        auto render = [&](uint32_t postFlags, float strength, float distance) {
            params.post_flags = postFlags;
            params.ssr_strength = strength;
            params.ssr_max_distance = distance;
            return RenderAndRead(engine, view, params, width, height);
        };
        const XMMATRIX viewProj = XMLoadFloat4x4(reinterpret_cast<const XMFLOAT4X4*>(params.view))
            * XMLoadFloat4x4(reinterpret_cast<const XMFLOAT4X4*>(params.projection));
        auto pixelOf = [&](float x, float y, float z) {
            XMVECTOR p = XMVector3TransformCoord(XMVectorSet(x, y, z, 1.0f), viewProj);
            const int px = static_cast<int>((XMVectorGetX(p) * 0.5f + 0.5f) * width);
            const int py = static_cast<int>((-XMVectorGetY(p) * 0.5f + 0.5f) * height);
            return (static_cast<size_t>(py) * width + px) * 4;
        };
        auto worstDifference = [](const std::vector<uint8_t>& a, const std::vector<uint8_t>& b) {
            int worst = 0;
            for (size_t i = 0; i < a.size(); ++i) {
                worst = std::max(worst, std::abs(static_cast<int>(a[i]) - static_cast<int>(b[i])));
            }
            return worst;
        };

        const TtMaterialState pbr = state(TT_SHADING_PBR);
        Assert::AreEqual<int>(TT_OK, tt_mesh_update_materials(floor, &pbr, 1));
        const std::vector<uint8_t> plain = render(TT_POST_VIGNETTE, 0.0f, 30.0f); // 映像効果を通すが、反射は使わない
        const std::vector<uint8_t> mirrored = render(TT_POST_VIGNETTE | TT_POST_SSR, 1.0f, 30.0f);
        // 床の、壁の真ん中（高さ 0.7）が映る所（カメラから映した点へ向かう線が床と交わる所）
        const size_t onFloor = pixelOf(0.0f, 0.0f, -0.97f);
        Assert::IsTrue(plain[onFloor + 2] > 60 && IsNear(plain[onFloor + 1], plain[onFloor + 2], 30), L"反射なしでは、床は周りの色（灰色）を映す");
        Assert::IsTrue(mirrored[onFloor + 2] > mirrored[onFloor + 1] + 60, L"画面の中の反射で、床に赤い壁が映る");
        Assert::IsTrue(plain[onFloor + 1] > mirrored[onFloor + 1] + 30, L"周りの色の映り込みは、壁の色に置き換わる");
        // 壁から離れた床（映り込む向きには何も無い）は、反射なしと同じ（自分の面に当たったことにしない）
        for (float z : {-1.5f, -0.5f, 0.5f, 1.2f, 1.8f}) {
            for (float x : {-1.7f, 1.7f}) {
                const size_t p = pixelOf(x, 0.0f, z);
                Assert::IsTrue(IsNear(mirrored[p], plain[p], 3) && IsNear(mirrored[p + 1], plain[p + 1], 3) && IsNear(mirrored[p + 2], plain[p + 2], 3),
                    L"何も映らない所は、反射なしと同じ");
            }
        }
        const size_t onWall = pixelOf(0.0f, 0.7f, 0.8f);
        Assert::IsTrue(IsNear(mirrored[onWall + 2], plain[onWall + 2], 2) && IsNear(mirrored[onWall + 1], plain[onWall + 1], 2),
            L"トゥーンの壁そのものは変わらない");

        // 探す距離が短く、何にも当たらないときは、分けて書いた環境の映り込みを戻すだけ（反射なしと同じ）
        const std::vector<uint8_t> shortReach = render(TT_POST_VIGNETTE | TT_POST_SSR, 1.0f, 0.1f);
        Assert::IsTrue(worstDifference(plain, shortReach) <= 3, L"当たらなければ、反射なしと同じ");

        // MSAA でも同じように映る
        uint32_t actual = 0;
        Assert::AreEqual<int>(TT_OK, tt_view_set_sample_count(view, 4, &actual));
        const std::vector<uint8_t> smooth = render(TT_POST_VIGNETTE | TT_POST_SSR, 1.0f, 30.0f);
        Assert::IsTrue(smooth[onFloor + 2] > smooth[onFloor + 1] + 60, L"MSAA でも床に赤い壁が映る");
        Assert::AreEqual<int>(TT_OK, tt_view_set_sample_count(view, 1, &actual));

        // トゥーンの床には映さない
        const TtMaterialState toon = state(TT_SHADING_TOON);
        Assert::AreEqual<int>(TT_OK, tt_mesh_update_materials(floor, &toon, 1));
        const std::vector<uint8_t> toonPlain = render(TT_POST_VIGNETTE, 0.0f, 30.0f);
        const std::vector<uint8_t> toonMirrored = render(TT_POST_VIGNETTE | TT_POST_SSR, 1.0f, 30.0f);
        Assert::IsTrue(worstDifference(toonPlain, toonMirrored) <= 1, L"トゥーンの材質は映さない");

        Assert::AreEqual<int>(TT_OK, tt_mesh_destroy(floor));
        Assert::AreEqual<int>(TT_OK, tt_mesh_destroy(wall));
        Assert::AreEqual<int>(TT_OK, tt_view_destroy(view));
        Assert::AreEqual<int>(TT_OK, tt_engine_destroy(engine));
    }

    TEST_METHOD(RayTracedReflectionsShowObjectsOutsideTheScreen)
    {
        // カメラの正面に、滑らかな金属（PBR）の鏡の板を立て、カメラの後ろ（画面の外）に四角形を置く。
        // 画面の中の反射だけでは、カメラの方へ戻る光線は映せない（環境の映り込みのまま）。レイトレーシングを使うと、後ろの四角形が映る
        TtEngine* engine = CreateWarpEngine();
        constexpr int width = 96;
        constexpr int height = 72;
        TtView* view = nullptr;
        Assert::AreEqual<int>(TT_OK, tt_view_create_offscreen(engine, width, height, 0, &view));
        auto makeQuad = [&](TtTexture* texture, uint32_t flags) {
            const TtMeshMaterial material = QuadMaterial(texture, flags);
            const TtMeshDesc desc = QuadDesc(kClockwise, &material);
            TtMesh* mesh = nullptr;
            Assert::AreEqual<int>(TT_OK, tt_mesh_create(engine, &desc, &mesh));
            return mesh;
        };
        TtTexture* white = CreateSolidTexture(engine, 255, 255, 255);
        TtTexture* red = CreateSolidTexture(engine, 0, 0, 255);
        // 左半分（u < 0.5）が青、右半分が緑
        const uint8_t halves[] = {255, 0, 0, 255, 0, 255, 0, 255};
        TtTexture* twoColors = nullptr;
        Assert::AreEqual<int>(TT_OK, tt_texture_create(engine, 2, 1, halves, 8, &twoColors));
        TtMesh* mirror = makeQuad(white, TT_MATERIAL_DOUBLE_SIDED);
        TtMesh* behind = makeQuad(red, 0);
        TtMesh* doubleSided = makeQuad(red, TT_MATERIAL_DOUBLE_SIDED);
        TtMesh* textured = makeQuad(twoColors, 0);
        for (TtTexture* texture : {white, red, twoColors}) {
            Assert::AreEqual<int>(TT_OK, tt_texture_destroy(texture));
        }
        TtMaterialState metal{};
        metal.diffuse[0] = metal.diffuse[1] = metal.diffuse[2] = metal.diffuse[3] = 1.0f;
        for (float* v : {metal.texture_multiply, metal.sphere_multiply, metal.toon_multiply, metal.base_tint}) {
            v[0] = v[1] = v[2] = v[3] = 1.0f;
        }
        metal.edge_color[3] = 1.0f;
        metal.shading = TT_SHADING_PBR;
        metal.metallic = 1.0f;
        metal.roughness = 0.05f;
        Assert::AreEqual<int>(TT_OK, tt_mesh_update_materials(mirror, &metal, 1));

        using namespace DirectX;
        auto place = [](TtDrawItem& item, FXMMATRIX world) { XMStoreFloat4x4(reinterpret_cast<XMFLOAT4X4*>(item.world), world); };
        // 鏡: 原点に 5 x 5（-Z 側、カメラの方を向く）。後ろの四角形: z = -8 に 5 x 5 で、鏡の方（+Z）を向ける
        const XMMATRIX mirrorWorld = XMMatrixScaling(2.5f, 2.5f, 1.0f);
        const XMMATRIX facingMirror = XMMatrixScaling(2.5f, 2.5f, 1.0f) * XMMatrixRotationY(XM_PI) * XMMatrixTranslation(0.0f, 0.0f, -8.0f);
        const XMMATRIX facingAway = XMMatrixScaling(2.5f, 2.5f, 1.0f) * XMMatrixTranslation(0.0f, 0.0f, -8.0f);
        TtDrawItem items[2] = {DrawItem(mirror), DrawItem(behind)};
        place(items[0], mirrorWorld);
        place(items[1], facingMirror);
        TtFrameParams params = MakeParams(TT_FRAME_USE_LIGHT);
        XMStoreFloat4x4(reinterpret_cast<XMFLOAT4X4*>(params.view),
            XMMatrixLookAtLH(XMVectorSet(0.0f, 0.0f, -4.0f, 1.0f), XMVectorZero(), XMVectorSet(0.0f, 1.0f, 0.0f, 0.0f)));
        params.light_direction[0] = 0.5f;
        params.light_direction[1] = -1.0f;
        params.light_direction[2] = 0.5f;
        params.light_color[0] = params.light_color[1] = params.light_color[2] = 0.6f;
        params.items = items;
        params.item_count = 2;
        params.vignette = 0.0f;
        params.ssr_strength = 1.0f;
        params.ssr_max_distance = 30.0f;
        auto render = [&](uint32_t postFlags) {
            params.post_flags = postFlags;
            return RenderAndRead(engine, view, params, width, height);
        };
        const XMMATRIX viewProj = XMLoadFloat4x4(reinterpret_cast<const XMFLOAT4X4*>(params.view))
            * XMLoadFloat4x4(reinterpret_cast<const XMFLOAT4X4*>(params.projection));
        auto pixelOf = [&](float x, float y, float z) {
            XMVECTOR p = XMVector3TransformCoord(XMVectorSet(x, y, z, 1.0f), viewProj);
            const int px = static_cast<int>((XMVectorGetX(p) * 0.5f + 0.5f) * width);
            const int py = static_cast<int>((-XMVectorGetY(p) * 0.5f + 0.5f) * height);
            return (static_cast<size_t>(py) * width + px) * 4;
        };
        auto worstDifference = [](const std::vector<uint8_t>& a, const std::vector<uint8_t>& b) {
            int worst = 0;
            for (size_t i = 0; i < a.size(); ++i) {
                worst = std::max(worst, std::abs(static_cast<int>(a[i]) - static_cast<int>(b[i])));
            }
            return worst;
        };
        constexpr uint32_t kScreen = TT_POST_VIGNETTE | TT_POST_SSR;
        constexpr uint32_t kTraced = kScreen | TT_POST_RAYTRACED_REFLECTIONS;
        const size_t center = pixelOf(0.0f, 0.0f, 0.0f);
        auto isGray = [](const std::vector<uint8_t>& image, size_t p) {
            return image[p + 2] > 60 && IsNear(image[p + 1], image[p + 2], 30) && IsNear(image[p], image[p + 2], 30);
        };
        auto isRed = [](const std::vector<uint8_t>& image, size_t p) { return image[p + 2] > image[p + 1] + 60 && image[p + 2] > image[p] + 60; };

        const std::vector<uint8_t> screenOnly = render(kScreen);
        Assert::IsTrue(isGray(screenOnly, center), L"画面の中の反射だけでは、カメラの後ろの物は映らない（環境の映り込み）");
        const std::vector<uint8_t> traced = render(kTraced);
        Assert::IsTrue(isRed(traced, center), L"レイトレーシングで、画面の外（カメラの後ろ）の赤い四角形が鏡に映る");
        Assert::IsTrue(worstDifference(render(TT_POST_VIGNETTE | TT_POST_RAYTRACED_REFLECTIONS), render(TT_POST_VIGNETTE)) == 0,
            L"画面の中の反射を使わなければ、レイトレーシングだけでは何もしない");

        // 当たる物が無ければ、画面の中の反射だけのときと同じ
        params.item_count = 1;
        Assert::IsTrue(worstDifference(render(kTraced), render(kScreen)) <= 1, L"当たらなければ、環境の映り込みのまま");
        params.item_count = 2;

        // 両面でない材質の裏の面は通り抜ける（両面の材質なら映る）
        place(items[1], facingAway);
        Assert::IsTrue(isGray(render(kTraced), center), L"裏を向けた片面の四角形は映らない");
        items[1].mesh = doubleSided;
        place(items[1], facingAway);
        Assert::IsTrue(isRed(render(kTraced), center), L"両面の材質なら、裏を向けても映る");
        items[1].mesh = behind;
        place(items[1], facingMirror);

        // 不透明度 0 の材質（材質モーフで消した物など）は通り抜ける
        TtMaterialState hidden{};
        hidden.diffuse[0] = hidden.diffuse[1] = hidden.diffuse[2] = 1.0f;
        hidden.diffuse[3] = 0.0f;
        hidden.ambient[0] = hidden.ambient[1] = hidden.ambient[2] = 1.0f;
        for (float* v : {hidden.texture_multiply, hidden.sphere_multiply, hidden.toon_multiply, hidden.base_tint}) {
            v[0] = v[1] = v[2] = v[3] = 1.0f;
        }
        Assert::AreEqual<int>(TT_OK, tt_mesh_update_materials(behind, &hidden, 1));
        Assert::IsTrue(isGray(render(kTraced), center), L"見えない材質は映らない");
        hidden.diffuse[3] = 1.0f;
        Assert::AreEqual<int>(TT_OK, tt_mesh_update_materials(behind, &hidden, 1));
        Assert::IsTrue(isRed(render(kTraced), center), L"材質を戻すと、また映る");

        // 頂点を動かす（モーフ・ボーンで変形したときと同じ）と、映る所もついてくる
        std::vector<TtMeshVertex> moved(std::begin(kQuad), std::end(kQuad));
        for (TtMeshVertex& v : moved) {
            v.position[0] += 100.0f;
        }
        Assert::AreEqual<int>(TT_OK, tt_mesh_update_vertices(behind, moved.data(), static_cast<uint32_t>(moved.size()), nullptr, nullptr));
        Assert::IsTrue(isGray(render(kTraced), center), L"遠くへ動かした四角形は映らない");
        Assert::AreEqual<int>(TT_OK, tt_mesh_update_vertices(behind, kQuad, 4, nullptr, nullptr));
        Assert::IsTrue(isRed(render(kTraced), center), L"戻すと、また映る");

        // テクスチャの色（当たった所の UV で読む）。鏡の右の点の光線は、後ろの四角形のテクスチャの左半分（青）に当たる
        items[1].mesh = textured;
        const std::vector<uint8_t> colors = render(kTraced);
        const size_t right = pixelOf(0.4f, 0.0f, 0.0f);
        const size_t left = pixelOf(-0.4f, 0.0f, 0.0f);
        Assert::IsTrue(colors[right] > colors[right + 1] + 60 && colors[right] > colors[right + 2] + 60, L"右には青が映る");
        Assert::IsTrue(colors[left + 1] > colors[left] + 60 && colors[left + 1] > colors[left + 2] + 60, L"左には緑が映る");

        // MSAA で、浅い角度で見る鏡だけ: 奥行きは画素の中心からずれた点のものなので、求めた位置が鏡の下になりやすい。
        // それでも光線を自分の鏡（両面）に当てない（当たる物が無いので、画面の中の反射だけのときと同じ）
        params.item_count = 1;
        place(items[0], XMMatrixScaling(2.5f, 2.5f, 1.0f) * XMMatrixRotationX(1.3f));
        uint32_t actual = 0;
        Assert::AreEqual<int>(TT_OK, tt_view_set_sample_count(view, 4, &actual));
        Assert::IsTrue(worstDifference(render(kTraced), render(kScreen)) <= 1, L"MSAA で浅い角度から見ても、自分の鏡に当てない");

        for (TtMesh* mesh : {mirror, behind, doubleSided, textured}) {
            Assert::AreEqual<int>(TT_OK, tt_mesh_destroy(mesh));
        }
        Assert::AreEqual<int>(TT_OK, tt_view_destroy(view));
        Assert::AreEqual<int>(TT_OK, tt_engine_destroy(engine));
    }

    TEST_METHOD(RayTracingFindsTheNearestOfManyTriangles)
    {
        // 細かく分けた板（三角形が数千枚。箱の入れ子が何段にもなる）を、カメラの後ろに 2 枚重ねて置く。
        // 手前（鏡に近い方）の緑の板は上半分だけ、奥の赤い板は右半分（鏡に映ると右）だけにある。鏡には、いちばん近くで当たる板の色が映る。
        // 頂点を大きく崩して（箱を作り直すほど）から戻しても、同じ絵になる
        TtEngine* engine = CreateWarpEngine();
        constexpr int width = 96;
        constexpr int height = 72;
        TtView* view = nullptr;
        Assert::AreEqual<int>(TT_OK, tt_view_create_offscreen(engine, width, height, 0, &view));
        constexpr int cells = 40;
        auto gridVertices = [] {
            std::vector<TtMeshVertex> vertices;
            for (int j = 0; j <= cells; ++j) {
                for (int i = 0; i <= cells; ++i) {
                    const float x = -1.0f + 2.0f * static_cast<float>(i) / cells;
                    const float y = 1.0f - 2.0f * static_cast<float>(j) / cells;
                    vertices.push_back(QuadVertex(x, y));
                }
            }
            return vertices;
        };
        // keep(x, y) が true のます目だけを作る（モデルの座標の、ます目の真ん中で決める）
        auto makeGrid = [&](uint8_t b, uint8_t g, uint8_t r, auto keep) {
            const std::vector<TtMeshVertex> vertices = gridVertices();
            std::vector<uint32_t> indices;
            for (int j = 0; j < cells; ++j) {
                for (int i = 0; i < cells; ++i) {
                    const float x = -1.0f + 2.0f * (static_cast<float>(i) + 0.5f) / cells;
                    const float y = 1.0f - 2.0f * (static_cast<float>(j) + 0.5f) / cells;
                    if (!keep(x, y)) {
                        continue;
                    }
                    const uint32_t topLeft = static_cast<uint32_t>(j * (cells + 1) + i);
                    const uint32_t bottomLeft = topLeft + cells + 1;
                    for (uint32_t index : {topLeft, topLeft + 1, bottomLeft + 1, topLeft, bottomLeft + 1, bottomLeft}) {
                        indices.push_back(index);
                    }
                }
            }
            TtTexture* texture = CreateSolidTexture(engine, b, g, r);
            TtMeshMaterial material = QuadMaterial(texture);
            material.index_count = static_cast<uint32_t>(indices.size());
            TtMeshDesc desc{};
            desc.struct_size = sizeof(desc);
            desc.vertex_count = static_cast<uint32_t>(vertices.size());
            desc.vertices = vertices.data();
            desc.index_count = static_cast<uint32_t>(indices.size());
            desc.indices = indices.data();
            desc.material_count = 1;
            desc.materials = &material;
            TtMesh* mesh = nullptr;
            Assert::AreEqual<int>(TT_OK, tt_mesh_create(engine, &desc, &mesh));
            Assert::AreEqual<int>(TT_OK, tt_texture_destroy(texture));
            return mesh;
        };
        TtTexture* white = CreateSolidTexture(engine, 255, 255, 255);
        const TtMeshMaterial mirrorMaterial = QuadMaterial(white, TT_MATERIAL_DOUBLE_SIDED);
        const TtMeshDesc mirrorDesc = QuadDesc(kClockwise, &mirrorMaterial);
        TtMesh* mirror = nullptr;
        Assert::AreEqual<int>(TT_OK, tt_mesh_create(engine, &mirrorDesc, &mirror));
        Assert::AreEqual<int>(TT_OK, tt_texture_destroy(white));
        // 鏡の方を向けると、モデルの座標の x は左右が入れ替わる（鏡に映った右 = モデルの座標の x < 0）
        TtMesh* green = makeGrid(0, 255, 0, [](float, float y) { return y > 0.0f; });
        TtMesh* red = makeGrid(0, 0, 255, [](float x, float) { return x < 0.0f; });
        TtMaterialState metal{};
        metal.diffuse[0] = metal.diffuse[1] = metal.diffuse[2] = metal.diffuse[3] = 1.0f;
        for (float* v : {metal.texture_multiply, metal.sphere_multiply, metal.toon_multiply, metal.base_tint}) {
            v[0] = v[1] = v[2] = v[3] = 1.0f;
        }
        metal.edge_color[3] = 1.0f;
        metal.shading = TT_SHADING_PBR;
        metal.metallic = 1.0f;
        metal.roughness = 0.05f;
        Assert::AreEqual<int>(TT_OK, tt_mesh_update_materials(mirror, &metal, 1));

        using namespace DirectX;
        auto place = [](TtDrawItem& item, FXMMATRIX world) { XMStoreFloat4x4(reinterpret_cast<XMFLOAT4X4*>(item.world), world); };
        TtDrawItem items[3] = {DrawItem(mirror), DrawItem(green), DrawItem(red)};
        place(items[0], XMMatrixScaling(2.5f, 2.5f, 1.0f));
        place(items[1], XMMatrixScaling(2.5f, 2.5f, 1.0f) * XMMatrixRotationY(XM_PI) * XMMatrixTranslation(0.0f, 0.0f, -6.0f));
        place(items[2], XMMatrixScaling(2.5f, 2.5f, 1.0f) * XMMatrixRotationY(XM_PI) * XMMatrixTranslation(0.0f, 0.0f, -8.0f));
        TtFrameParams params = MakeParams(TT_FRAME_USE_LIGHT);
        XMStoreFloat4x4(reinterpret_cast<XMFLOAT4X4*>(params.view),
            XMMatrixLookAtLH(XMVectorSet(0.0f, 0.0f, -4.0f, 1.0f), XMVectorZero(), XMVectorSet(0.0f, 1.0f, 0.0f, 0.0f)));
        params.light_color[0] = params.light_color[1] = params.light_color[2] = 0.6f;
        params.light_direction[1] = -1.0f;
        params.items = items;
        params.item_count = 3;
        params.post_flags = TT_POST_VIGNETTE | TT_POST_SSR | TT_POST_RAYTRACED_REFLECTIONS;
        params.vignette = 0.0f;
        params.ssr_strength = 1.0f;
        params.ssr_max_distance = 30.0f;
        const XMMATRIX viewProj = XMLoadFloat4x4(reinterpret_cast<const XMFLOAT4X4*>(params.view))
            * XMLoadFloat4x4(reinterpret_cast<const XMFLOAT4X4*>(params.projection));
        auto pixelOf = [&](float x, float y) {
            XMVECTOR p = XMVector3TransformCoord(XMVectorSet(x, y, 0.0f, 1.0f), viewProj);
            const int px = static_cast<int>((XMVectorGetX(p) * 0.5f + 0.5f) * width);
            const int py = static_cast<int>((-XMVectorGetY(p) * 0.5f + 0.5f) * height);
            return (static_cast<size_t>(py) * width + px) * 4;
        };
        auto isRed = [](const std::vector<uint8_t>& image, size_t p) { return image[p + 2] > image[p + 1] + 60 && image[p + 2] > image[p] + 60; };
        auto isGreen = [](const std::vector<uint8_t>& image, size_t p) { return image[p + 1] > image[p + 2] + 60 && image[p + 1] > image[p] + 60; };
        auto isGray = [](const std::vector<uint8_t>& image, size_t p) {
            return image[p + 2] > 60 && IsNear(image[p + 1], image[p + 2], 30) && IsNear(image[p], image[p + 2], 30);
        };
        auto check = [&](const std::vector<uint8_t>& image) {
            // 鏡の点 (x, y) の光線は、z = -6 では (2.5x, 2.5y)、z = -8 では (3x, 3y) を通る
            Assert::IsTrue(isGreen(image, pixelOf(0.4f, 0.4f)), L"右上: 手前の緑の板に当たる（奥の赤より近い）");
            Assert::IsTrue(isGreen(image, pixelOf(-0.4f, 0.4f)), L"左上: 緑の板に当たる");
            Assert::IsTrue(isRed(image, pixelOf(0.4f, -0.4f)), L"右下: 緑の板の無い所を抜けて、奥の赤い板に当たる");
            Assert::IsTrue(isGray(image, pixelOf(-0.4f, -0.4f)), L"左下: どちらの板も無い（環境の映り込み）");
        };
        const std::vector<uint8_t> first = RenderAndRead(engine, view, params, width, height);
        check(first);

        // 頂点を大きく崩す（1 行おきに上へ 50 ずらし、細長い三角形にする。箱がふくらみすぎるので作り直す）
        std::vector<TtMeshVertex> scrambled = gridVertices();
        for (size_t i = 0; i < scrambled.size(); ++i) {
            if ((i / (cells + 1)) % 2 == 1) {
                scrambled[i].position[1] += 50.0f;
            }
        }
        Assert::AreEqual<int>(TT_OK, tt_mesh_update_vertices(green, scrambled.data(), static_cast<uint32_t>(scrambled.size()), nullptr, nullptr));
        RenderAndRead(engine, view, params, width, height);
        // 戻すと、同じ絵になる
        const std::vector<TtMeshVertex> original = gridVertices();
        Assert::AreEqual<int>(TT_OK, tt_mesh_update_vertices(green, original.data(), static_cast<uint32_t>(original.size()), nullptr, nullptr));
        const std::vector<uint8_t> restored = RenderAndRead(engine, view, params, width, height);
        check(restored);
        int worst = 0;
        for (size_t i = 0; i < first.size(); ++i) {
            worst = std::max(worst, std::abs(static_cast<int>(first[i]) - static_cast<int>(restored[i])));
        }
        Assert::IsTrue(worst <= 2, L"戻すと、崩す前と同じ絵になる");

        for (TtMesh* mesh : {mirror, green, red}) {
            Assert::AreEqual<int>(TT_OK, tt_mesh_destroy(mesh));
        }
        Assert::AreEqual<int>(TT_OK, tt_view_destroy(view));
        Assert::AreEqual<int>(TT_OK, tt_engine_destroy(engine));
    }

    TEST_METHOD(RayTracedAmbientOcclusionSeesObjectsOutsideTheScreen)
    {
        // 正面に白い大きな板、その左の画面の外に、板に接する壁を立てる。画面の奥行きで求める環境光の遮りは、画面に映っていない壁を
        // 知らないので暗くならない。レイトレーシングで求めると、壁の近くが暗くなる。何も無い所（板の真ん中）は暗くならない
        TtEngine* engine = CreateWarpEngine();
        constexpr int width = 96;
        constexpr int height = 72;
        TtView* view = nullptr;
        Assert::AreEqual<int>(TT_OK, tt_view_create_offscreen(engine, width, height, 0, &view));
        TtTexture* white = CreateSolidTexture(engine, 255, 255, 255);
        const TtMeshMaterial material = QuadMaterial(white, TT_MATERIAL_DOUBLE_SIDED);
        const TtMeshDesc desc = QuadDesc(kClockwise, &material);
        TtMesh* board = nullptr;
        TtMesh* wall = nullptr;
        Assert::AreEqual<int>(TT_OK, tt_mesh_create(engine, &desc, &board));
        Assert::AreEqual<int>(TT_OK, tt_mesh_create(engine, &desc, &wall));
        Assert::AreEqual<int>(TT_OK, tt_texture_destroy(white));

        using namespace DirectX;
        TtDrawItem items[2] = {DrawItem(board), DrawItem(wall)};
        // 板: z = 0 に 10 x 10。壁: x = -3（画面の外）に、高さ 6・奥行き -1.5〜0 で立てる（板に接する）
        XMStoreFloat4x4(reinterpret_cast<XMFLOAT4X4*>(items[0].world), XMMatrixScaling(5.0f, 5.0f, 1.0f));
        XMStoreFloat4x4(reinterpret_cast<XMFLOAT4X4*>(items[1].world),
            XMMatrixScaling(0.75f, 3.0f, 1.0f) * XMMatrixRotationY(XM_PIDIV2) * XMMatrixTranslation(-3.0f, 0.0f, -0.75f));
        TtFrameParams params = MakeFrontParams();
        params.items = items;
        params.item_count = 2;
        params.vignette = 0.0f;
        params.ao_strength = 1.0f;
        params.ao_radius = 1.0f;
        auto render = [&](uint32_t postFlags) {
            params.post_flags = postFlags;
            return RenderAndRead(engine, view, params, width, height);
        };
        const XMMATRIX viewProj = XMLoadFloat4x4(reinterpret_cast<const XMFLOAT4X4*>(params.view))
            * XMLoadFloat4x4(reinterpret_cast<const XMFLOAT4X4*>(params.projection));
        auto pixelOf = [&](float x, float y) {
            XMVECTOR p = XMVector3TransformCoord(XMVectorSet(x, y, 0.0f, 1.0f), viewProj);
            const int px = static_cast<int>((XMVectorGetX(p) * 0.5f + 0.5f) * width);
            const int py = static_cast<int>((-XMVectorGetY(p) * 0.5f + 0.5f) * height);
            return (static_cast<size_t>(py) * width + px) * 4;
        };
        auto worstDifference = [](const std::vector<uint8_t>& a, const std::vector<uint8_t>& b) {
            int worst = 0;
            for (size_t i = 0; i < a.size(); ++i) {
                worst = std::max(worst, std::abs(static_cast<int>(a[i]) - static_cast<int>(b[i])));
            }
            return worst;
        };

        const std::vector<uint8_t> plain = render(TT_POST_VIGNETTE);
        const std::vector<uint8_t> screen = render(TT_POST_VIGNETTE | TT_POST_AO);
        const std::vector<uint8_t> traced = render(TT_POST_VIGNETTE | TT_POST_AO | TT_POST_RAYTRACED_AO);
        const size_t nearWall = pixelOf(-2.6f, 0.0f); // 壁から 0.4
        const size_t middle = pixelOf(0.0f, 0.0f);
        Assert::IsTrue(plain[nearWall + 1] > 240, L"遮りが無ければ、板は白い");
        Assert::IsTrue(IsNear(screen[nearWall + 1], plain[nearWall + 1], 3), L"画面の奥行きの遮りは、画面の外の壁では暗くならない");
        Assert::IsTrue(traced[nearWall + 1] + 15 < plain[nearWall + 1], L"レイトレーシングの遮りは、画面の外の壁の近くを暗くする");
        Assert::IsTrue(IsNear(traced[middle + 1], plain[middle + 1], 2), L"何も無い所（自分の面）は暗くならない");
        Assert::IsTrue(worstDifference(render(TT_POST_VIGNETTE | TT_POST_RAYTRACED_AO), plain) == 0,
            L"環境光の遮りを使わなければ、レイトレーシングだけでは何もしない");

        // 壁を板から離す（広がりより遠くへ）と、暗くならない
        XMStoreFloat4x4(reinterpret_cast<XMFLOAT4X4*>(items[1].world),
            XMMatrixScaling(0.75f, 3.0f, 1.0f) * XMMatrixRotationY(XM_PIDIV2) * XMMatrixTranslation(-5.0f, 0.0f, -0.75f));
        const std::vector<uint8_t> distant = render(TT_POST_VIGNETTE | TT_POST_AO | TT_POST_RAYTRACED_AO);
        Assert::IsTrue(IsNear(distant[nearWall + 1], plain[nearWall + 1], 2), L"広がりより遠い物は遮らない");

        // 傾けた板だけ（奥行きが画素ごとに変わる）: 画像の端まで、どこも暗くならない（自分の面に当てない。端の画素の面の向きも正しい）
        XMStoreFloat4x4(reinterpret_cast<XMFLOAT4X4*>(items[0].world), XMMatrixScaling(5.0f, 5.0f, 1.0f) * XMMatrixRotationX(0.6f));
        params.item_count = 1;
        const std::vector<uint8_t> tiltedPlain = render(TT_POST_VIGNETTE);
        const std::vector<uint8_t> tiltedTraced = render(TT_POST_VIGNETTE | TT_POST_AO | TT_POST_RAYTRACED_AO);
        Assert::IsTrue(worstDifference(tiltedTraced, tiltedPlain) <= 2, L"遮る物が無ければ、傾けた板のどこも暗くならない");

        // MSAA で、浅い角度で見る板（画素の中で奥行きが大きく変わる）: 奥行きは画素の中心からずれた点のものなので、
        // 求めた位置が板の下になりやすい。それでも自分の板に当てて暗くしない
        XMStoreFloat4x4(reinterpret_cast<XMFLOAT4X4*>(items[0].world), XMMatrixScaling(5.0f, 5.0f, 1.0f) * XMMatrixRotationX(1.3f));
        uint32_t actual = 0;
        Assert::AreEqual<int>(TT_OK, tt_view_set_sample_count(view, 4, &actual));
        const std::vector<uint8_t> grazingPlain = render(TT_POST_VIGNETTE);
        const std::vector<uint8_t> grazingTraced = render(TT_POST_VIGNETTE | TT_POST_AO | TT_POST_RAYTRACED_AO);
        Assert::IsTrue(worstDifference(grazingTraced, grazingPlain) <= 2, L"MSAA で浅い角度から見ても、自分の板で暗くならない");

        Assert::AreEqual<int>(TT_OK, tt_mesh_destroy(board));
        Assert::AreEqual<int>(TT_OK, tt_mesh_destroy(wall));
        Assert::AreEqual<int>(TT_OK, tt_view_destroy(view));
        Assert::AreEqual<int>(TT_OK, tt_engine_destroy(engine));
    }

    TEST_METHOD(DeferredPublishShowsOnlyPublishedFrames)
    {
        // 見える面を分けたビュー: 描いても、写す（tt_view_publish）までは見える面（読み出し）は変わらない。
        // 表示側が前の絵を出している間に、GPU の完了を待たずに次の絵を描けるようにするため
        TtEngine* engine = CreateWarpEngine();
        constexpr int width = 32;
        constexpr int height = 24;
        TtView* view = nullptr;
        Assert::AreEqual<int>(TT_OK, tt_view_create_offscreen(engine, width, height, TT_VIEW_FLAG_DEFERRED_PUBLISH, &view));
        auto read = [&] {
            std::vector<uint8_t> pixels(static_cast<size_t>(width) * height * 4);
            Assert::AreEqual<int>(TT_OK, tt_engine_wait_for_gpu(engine));
            Assert::AreEqual<int>(TT_OK, tt_view_read_pixels(view, pixels.data(), width * 4, static_cast<int>(pixels.size())));
            return pixels;
        };
        auto render = [&](float r, float g, float b) {
            TtFrameParams params = MakeParams(TT_FRAME_CLEAR_COLOR);
            params.clear_color[0] = r;
            params.clear_color[1] = g;
            params.clear_color[2] = b;
            params.clear_color[3] = 1.0f;
            Assert::AreEqual<int>(TT_OK, tt_view_render(view, &params));
        };
        auto isColor = [](const std::vector<uint8_t>& pixels, int b, int g, int r, int a) {
            for (size_t i = 0; i < pixels.size(); i += 4) {
                if (!IsNear(pixels[i], b, 1) || !IsNear(pixels[i + 1], g, 1) || !IsNear(pixels[i + 2], r, 1) || !IsNear(pixels[i + 3], a, 1)) {
                    return false;
                }
            }
            return true;
        };

        Assert::IsTrue(isColor(read(), 0, 0, 0, 0), L"作ったときの見える面は透明な黒");
        Assert::AreEqual<int>(TT_OK, tt_view_publish(view));
        Assert::IsTrue(isColor(read(), 0, 0, 0, 0), L"まだ描いていなければ、写しても変わらない");
        render(1.0f, 0.0f, 0.0f);
        Assert::IsTrue(isColor(read(), 0, 0, 0, 0), L"描いただけでは、見える面は変わらない");
        Assert::AreEqual<int>(TT_OK, tt_view_publish(view));
        Assert::IsTrue(isColor(read(), 0, 0, 255, 255), L"写すと、描いた絵が見える");
        render(0.0f, 1.0f, 0.0f);
        Assert::IsTrue(isColor(read(), 0, 0, 255, 255), L"次の絵を描いている間も、前に写した絵が見えている");
        Assert::AreEqual<int>(TT_OK, tt_view_publish(view));
        Assert::IsTrue(isColor(read(), 0, 255, 0, 255), L"写すと、次の絵に変わる");

        // MSAA でも同じ（まとめた後の絵を写す）
        uint32_t actual = 0;
        Assert::AreEqual<int>(TT_OK, tt_view_set_sample_count(view, 4, &actual));
        render(0.0f, 0.0f, 1.0f);
        Assert::IsTrue(isColor(read(), 0, 255, 0, 255), L"MSAA でも、写すまでは前の絵");
        Assert::AreEqual<int>(TT_OK, tt_view_publish(view));
        Assert::IsTrue(isColor(read(), 255, 0, 0, 255), L"MSAA でも、写すと描いた絵が見える");

        // 大きさを変えると、見える面は透明な黒に戻り、描くまでは写しても変わらない
        Assert::AreEqual<int>(TT_OK, tt_view_resize(view, width, height + 1));
        Assert::AreEqual<int>(TT_OK, tt_view_resize(view, width, height));
        Assert::AreEqual<int>(TT_OK, tt_view_publish(view));
        Assert::IsTrue(isColor(read(), 0, 0, 0, 0), L"大きさを変えた後、描くまでは透明な黒");

        // GPU を作り直しても、同じように使える
        render(1.0f, 1.0f, 1.0f);
        Assert::AreEqual<int>(TT_OK, tt_engine_flush(engine), L"頼んだ描画をすぐ GPU へ送る");
        Assert::AreEqual<int>(TT_OK, tt_engine_simulate_device_lost(engine));
        Assert::AreEqual<int>(TT_E_DEVICE_LOST, tt_view_publish(view));
        Assert::AreEqual<int>(TT_E_DEVICE_LOST, tt_engine_flush(engine));
        Assert::AreEqual<int>(TT_OK, tt_engine_recover(engine));
        Assert::IsTrue(isColor(read(), 0, 0, 0, 0), L"作り直した後は、描くまで透明な黒");
        render(1.0f, 1.0f, 1.0f);
        Assert::AreEqual<int>(TT_OK, tt_view_publish(view));
        Assert::IsTrue(isColor(read(), 255, 255, 255, 255), L"作り直した後も、写すと見える");
        Assert::AreEqual<int>(TT_OK, tt_view_destroy(view));

        // 見える面を分けないビューでは、写すのは何もしない（描いた絵がすぐ読める）
        Assert::AreEqual<int>(TT_OK, tt_view_create_offscreen(engine, width, height, 0, &view));
        render(1.0f, 0.0f, 0.0f);
        Assert::AreEqual<int>(TT_OK, tt_view_publish(view));
        Assert::IsTrue(isColor(read(), 0, 0, 255, 255), L"分けないビューは、描いた絵がそのまま見える");
        Assert::AreEqual<int>(TT_OK, tt_view_destroy(view));

        TtView* bad = nullptr;
        Assert::AreEqual<int>(TT_E_INVALID_ARG, tt_view_create_offscreen(engine, width, height, 1u << 2, &bad), L"知らない印は受け付けない");
        Assert::AreEqual<int>(TT_E_INVALID_ARG, tt_view_publish(nullptr));
        Assert::AreEqual<int>(TT_E_INVALID_ARG, tt_engine_flush(nullptr));
        Assert::AreEqual<int>(TT_OK, tt_engine_destroy(engine));
    }

    TEST_METHOD(DepthOfFieldBlursOutsideTheFocus)
    {
        // 手前（距離 3）に小さな赤い四角形、奥（距離 8）に大きな緑の四角形。ピントの合っていないほうの縁がぼける
        TtEngine* engine = CreateWarpEngine();
        constexpr int width = 64;
        constexpr int height = 48;
        TtView* view = nullptr;
        Assert::AreEqual<int>(TT_OK, tt_view_create_offscreen(engine, width, height, 0, &view));
        auto makeQuad = [&](uint8_t b, uint8_t g, uint8_t r) {
            TtTexture* texture = CreateSolidTexture(engine, b, g, r);
            const TtMeshMaterial material = QuadMaterial(texture);
            const TtMeshDesc desc = QuadDesc(kClockwise, &material);
            TtMesh* mesh = nullptr;
            Assert::AreEqual<int>(TT_OK, tt_mesh_create(engine, &desc, &mesh));
            Assert::AreEqual<int>(TT_OK, tt_texture_destroy(texture));
            return mesh;
        };
        TtMesh* nearMesh = makeQuad(0, 0, 255);
        TtMesh* farMesh = makeQuad(0, 255, 0);

        auto render = [&](uint32_t flags, float focus, float blur) {
            TtFrameParams params = MakeFrontParams();
            params.flags = TT_FRAME_CLEAR_COLOR;
            params.clear_color[3] = 1.0f;
            TtDrawItem items[2] = {DrawItem(farMesh), DrawItem(nearMesh)};
            DirectX::XMStoreFloat4x4(reinterpret_cast<DirectX::XMFLOAT4X4*>(items[0].world),
                DirectX::XMMatrixScaling(1.2f, 1.2f, 1.0f) * DirectX::XMMatrixTranslation(0.0f, 0.0f, 3.0f));
            DirectX::XMStoreFloat4x4(reinterpret_cast<DirectX::XMFLOAT4X4*>(items[1].world),
                DirectX::XMMatrixScaling(0.3f, 0.3f, 1.0f) * DirectX::XMMatrixTranslation(0.0f, 0.0f, -2.0f));
            params.items = items;
            params.item_count = 2;
            params.post_flags = flags;
            params.contrast = 1.0f;
            params.saturation = 1.0f;
            params.dof_focus_distance = focus;
            params.dof_focus_range = 0.5f;
            params.dof_max_blur = blur;
            params.dof_transition = 1.0f;
            return RenderAndRead(engine, view, params, width, height);
        };
        // 奥の四角形の右の縁（中央から 6〜14 画素。手前の四角形より外）で、緑が中間の明るさの画素の数
        auto farEdgeBlur = [&](const std::vector<uint8_t>& pixels) {
            int count = 0;
            for (int y = height / 2 - 3; y <= height / 2 + 3; ++y) {
                for (int x = width / 2 + 6; x <= width / 2 + 14; ++x) {
                    const int g = pixels[(static_cast<size_t>(y) * width + x) * 4 + 1];
                    count += (g > 30 && g < 200) ? 1 : 0;
                }
            }
            return count;
        };
        // 手前の四角形の右の縁（中央から 3〜8 画素）で、赤が中間の明るさの画素の数
        auto nearEdgeBlur = [&](const std::vector<uint8_t>& pixels) {
            int count = 0;
            for (int y = height / 2 - 2; y <= height / 2 + 2; ++y) {
                for (int x = width / 2 + 3; x <= width / 2 + 8; ++x) {
                    const int r = pixels[(static_cast<size_t>(y) * width + x) * 4 + 2];
                    count += (r > 30 && r < 200) ? 1 : 0;
                }
            }
            return count;
        };

        std::vector<uint8_t> sharp = render(0, 3.0f, 6.0f);
        Assert::AreEqual(0, farEdgeBlur(sharp), L"被写界深度なしなら、縁はくっきり");
        Assert::AreEqual(0, nearEdgeBlur(sharp));

        std::vector<uint8_t> focusNear = render(TT_POST_DOF, 3.0f, 6.0f);
        Assert::IsTrue(farEdgeBlur(focusNear) > 3, L"手前にピントを合わせると、奥の縁がぼける");
        Assert::AreEqual(0, nearEdgeBlur(focusNear), L"ピントの合った手前の縁は、くっきりのまま");

        std::vector<uint8_t> focusFar = render(TT_POST_DOF, 8.0f, 6.0f);
        Assert::AreEqual(0, farEdgeBlur(focusFar), L"奥にピントを合わせると、奥の縁はくっきり");
        Assert::IsTrue(nearEdgeBlur(focusFar) > 3, L"手前の縁がぼける");

        std::vector<uint8_t> noBlur = render(TT_POST_DOF, 3.0f, 0.0f);
        Assert::AreEqual(0, farEdgeBlur(noBlur), L"ぼけの大きさ 0 なら何もしない");

        Assert::AreEqual<int>(TT_OK, tt_mesh_destroy(nearMesh));
        Assert::AreEqual<int>(TT_OK, tt_mesh_destroy(farMesh));
        Assert::AreEqual<int>(TT_OK, tt_view_destroy(view));
        Assert::AreEqual<int>(TT_OK, tt_engine_destroy(engine));
    }

    TEST_METHOD(AmbientOcclusionDarkensCreases)
    {
        // 奥の壁（z = 2）に、60 度傾けた板が x = 0 で接する。接する所の近くの壁と板だけが暗くなる
        TtEngine* engine = CreateWarpEngine();
        constexpr int width = 256;
        constexpr int height = 192;
        TtView* view = nullptr;
        Assert::AreEqual<int>(TT_OK, tt_view_create_offscreen(engine, width, height, 0, &view));
        TtTexture* texture = CreateSolidTexture(engine, 160, 160, 160);
        const TtMeshMaterial material = QuadMaterial(texture);
        const TtMeshDesc desc = QuadDesc(kClockwise, &material);
        TtMesh* mesh = nullptr;
        Assert::AreEqual<int>(TT_OK, tt_mesh_create(engine, &desc, &mesh));
        Assert::AreEqual<int>(TT_OK, tt_texture_destroy(texture));

        auto render = [&](uint32_t flags, float strength, float radius) {
            TtFrameParams params = MakeFrontParams();
            params.flags = TT_FRAME_CLEAR_COLOR;
            params.clear_color[3] = 1.0f;
            TtDrawItem items[2] = {DrawItem(mesh), DrawItem(mesh)};
            DirectX::XMStoreFloat4x4(reinterpret_cast<DirectX::XMFLOAT4X4*>(items[0].world),
                DirectX::XMMatrixScaling(2.0f, 2.0f, 1.0f) * DirectX::XMMatrixTranslation(0.0f, 0.0f, 2.0f));
            DirectX::XMStoreFloat4x4(reinterpret_cast<DirectX::XMFLOAT4X4*>(items[1].world),
                DirectX::XMMatrixScaling(1.0f, 2.0f, 1.0f) * DirectX::XMMatrixRotationY(DirectX::XM_PI / 3.0f)
                    * DirectX::XMMatrixTranslation(0.5f, 0.0f, 2.0f - 0.8660f));
            params.items = items;
            params.item_count = 2;
            params.post_flags = flags;
            params.contrast = 1.0f;
            params.saturation = 1.0f;
            params.ao_strength = strength;
            params.ao_radius = radius;
            return RenderAndRead(engine, view, params, width, height);
        };
        auto green = [&](const std::vector<uint8_t>& pixels, int x, int y) {
            return static_cast<int>(pixels[(static_cast<size_t>(y) * width + x) * 4 + 1]);
        };

        const std::vector<uint8_t> plain = render(0, 0.0f, 0.4f);
        const std::vector<uint8_t> occluded = render(TT_POST_AO, 1.0f, 0.4f);
        const int middle = height / 2;
        const int crease = green(plain, width / 2 - 2, middle);
        Assert::IsTrue(crease > 40, L"接する所の近くの壁が描かれている");
        Assert::IsTrue(green(occluded, width / 2 - 2, middle) < crease * 9 / 10, L"接する所の近くの壁は暗くなる");
        for (int x : {72, 80}) {
            Assert::IsTrue(std::abs(green(plain, x, middle) - green(occluded, x, middle)) <= 2, L"接する所から離れた平らな壁は変わらない");
        }
        Assert::IsTrue(green(occluded, width / 2 + 1, middle) < green(plain, width / 2 + 1, middle) * 9 / 10, L"接する所の近くの板も暗くなる");
        Assert::IsTrue(std::abs(green(plain, 164, middle) - green(occluded, 164, middle)) <= 2, L"板の、接する所から離れた所は変わらない");
        Assert::AreEqual(green(plain, 2, 2), green(occluded, 2, 2), L"背景は変わらない");

        const std::vector<uint8_t> none = render(TT_POST_AO, 0.0f, 0.4f);
        Assert::IsTrue(std::abs(crease - green(none, width / 2 - 2, middle)) <= 1, L"強さ 0 なら何もしない");
        const std::vector<uint8_t> narrow = render(TT_POST_AO, 1.0f, 0.05f);
        Assert::IsTrue(green(narrow, width / 2 - 2, middle) > green(occluded, width / 2 - 2, middle), L"広がりを小さくすると、接する所から少し離れた点は明るくなる");

        Assert::AreEqual<int>(TT_OK, tt_mesh_destroy(mesh));
        Assert::AreEqual<int>(TT_OK, tt_view_destroy(view));
        Assert::AreEqual<int>(TT_OK, tt_engine_destroy(engine));
    }

    TEST_METHOD(FogBlendsDistantObjectsIntoTheFogColor)
    {
        // 赤い板を 2 枚。左は近く（カメラから 3）、右は遠く（カメラから 10）。霧の色は青
        TtEngine* engine = CreateWarpEngine();
        constexpr int width = 64;
        constexpr int height = 48;
        TtView* view = nullptr;
        Assert::AreEqual<int>(TT_OK, tt_view_create_offscreen(engine, width, height, 0, &view));
        TtTexture* texture = CreateSolidTexture(engine, 0, 0, 255);
        const TtMeshMaterial material = QuadMaterial(texture);
        const TtMeshDesc desc = QuadDesc(kClockwise, &material);
        TtMesh* mesh = nullptr;
        Assert::AreEqual<int>(TT_OK, tt_mesh_create(engine, &desc, &mesh));
        Assert::AreEqual<int>(TT_OK, tt_texture_destroy(texture));

        auto render = [&](uint32_t flags, float start, float density, float fogHeight, float backgroundAlpha, bool orthographic = false) {
            TtFrameParams params = MakeFrontParams();
            if (orthographic) {
                DirectX::XMStoreFloat4x4(reinterpret_cast<DirectX::XMFLOAT4X4*>(params.projection),
                    DirectX::XMMatrixOrthographicLH(8.0f, 6.0f, 0.1f, 100.0f));
            }
            params.flags = TT_FRAME_CLEAR_COLOR;
            params.clear_color[3] = backgroundAlpha;
            TtDrawItem items[2] = {DrawItem(mesh), DrawItem(mesh)};
            DirectX::XMStoreFloat4x4(reinterpret_cast<DirectX::XMFLOAT4X4*>(items[0].world),
                DirectX::XMMatrixScaling(0.5f, 0.5f, 1.0f) * DirectX::XMMatrixTranslation(-1.0f, 0.0f, -2.0f));
            DirectX::XMStoreFloat4x4(reinterpret_cast<DirectX::XMFLOAT4X4*>(items[1].world),
                DirectX::XMMatrixScaling(1.5f, 1.5f, 1.0f) * DirectX::XMMatrixTranslation(3.0f, 0.0f, 5.0f));
            params.items = items;
            params.item_count = 2;
            params.post_flags = flags;
            params.contrast = 1.0f;
            params.saturation = 1.0f;
            params.fog_color[2] = 1.0f;
            params.fog_start = start;
            params.fog_density = density;
            params.fog_height = fogHeight;
            return RenderAndRead(engine, view, params, width, height);
        };
        auto at = [&](const std::vector<uint8_t>& pixels, int x, int y, int channel) {
            return static_cast<int>(pixels[(static_cast<size_t>(y) * width + x) * 4 + channel]);
        };
        constexpr int kBlue = 0;
        constexpr int kRed = 2;
        constexpr int kAlpha = 3;
        const int nearX = 13;
        const int farX = 49;
        const int middle = height / 2;

        const std::vector<uint8_t> plain = render(0, 0.0f, 0.1f, 0.0f, 1.0f);
        Assert::AreEqual(255, at(plain, nearX, middle, kRed));
        Assert::AreEqual(255, at(plain, farX, middle, kRed));
        Assert::AreEqual(0, at(plain, 2, 2, kBlue));

        // 霧の量 = 1 - exp(-0.1 × 距離): 近く 0.26、遠く 0.63、背景 1
        const std::vector<uint8_t> foggy = render(TT_POST_FOG, 0.0f, 0.1f, 0.0f, 1.0f);
        const int nearBlue = at(foggy, nearX, middle, kBlue);
        const int farBlue = at(foggy, farX, middle, kBlue);
        Assert::IsTrue(nearBlue > 50 && nearBlue < 85, L"近くの板は少しだけ霧の色になる");
        Assert::IsTrue(farBlue > 145 && farBlue < 180, L"遠くの板はより霧の色になる");
        Assert::IsTrue(at(foggy, farX, middle, kRed) < at(foggy, nearX, middle, kRed), L"遠くほど元の色が薄い");
        Assert::IsTrue(at(foggy, 2, 2, kBlue) > 250, L"不透明な背景は霧の色になる");

        const std::vector<uint8_t> transparent = render(TT_POST_FOG, 0.0f, 0.1f, 0.0f, 0.0f);
        Assert::AreEqual(0, at(transparent, 2, 2, kAlpha), L"透明な背景は透明のまま");
        Assert::AreEqual(255, at(transparent, farX, middle, kAlpha), L"物は不透明のまま");
        Assert::IsTrue(std::abs(farBlue - at(transparent, farX, middle, kBlue)) <= 2, L"物の霧は背景によらない");

        const std::vector<uint8_t> late = render(TT_POST_FOG, 20.0f, 0.1f, 0.0f, 0.0f);
        Assert::AreEqual(255, at(late, farX, middle, kRed), L"霧の始まりより近い物は変わらない");
        Assert::AreEqual(0, at(late, farX, middle, kBlue));

        const std::vector<uint8_t> none = render(TT_POST_FOG, 0.0f, 0.0f, 0.0f, 1.0f);
        Assert::AreEqual(0, at(none, farX, middle, kBlue), L"濃さ 0 なら何もしない");

        // 高さで薄くする: カメラは高さ 0。板の下（地面より下）は濃さそのまま、上は薄い
        const std::vector<uint8_t> ground = render(TT_POST_FOG, 0.0f, 0.1f, 0.5f, 1.0f);
        const int top = at(ground, farX, middle - 7, kBlue);
        const int bottom = at(ground, farX, middle + 7, kBlue);
        Assert::IsTrue(bottom > top + 40, L"地面の近く（下）ほど霧が濃い");
        Assert::IsTrue(std::abs(bottom - at(foggy, farX, middle + 7, kBlue)) <= 3, L"地面より下は、高さによらない霧と同じ");

        const std::vector<uint8_t> fixedView = render(TT_POST_FOG, 0.0f, 0.1f, 0.0f, 1.0f, true);
        Assert::AreEqual(255, at(fixedView, farX, middle, kRed), L"平行投影（固定の視点）には霧をかけない");
        Assert::AreEqual(0, at(fixedView, 2, 2, kBlue));

        Assert::AreEqual<int>(TT_OK, tt_mesh_destroy(mesh));
        Assert::AreEqual<int>(TT_OK, tt_view_destroy(view));
        Assert::AreEqual<int>(TT_OK, tt_engine_destroy(engine));
    }

    TEST_METHOD(GridCanBeDrawnBehindModels)
    {
        // 正面の視点と同じく、格子を画面に平行な面（Z = 0）に置き、その奥（Z = 0.5）に赤い四角形を置く。
        // ふつうは格子が手前なので四角形の上に線が出るが、TT_FRAME_GRID_BEHIND なら格子はいちばん奥になる
        TtEngine* engine = CreateWarpEngine();
        constexpr int width = 64;
        constexpr int height = 48;
        TtView* view = nullptr;
        Assert::AreEqual<int>(TT_OK, tt_view_create_offscreen(engine, width, height, 0, &view));
        TtTexture* red = CreateSolidTexture(engine, 0, 0, 255);
        const TtMeshMaterial material = QuadMaterial(red);
        const TtMeshDesc desc = QuadDesc(kClockwise, &material);
        TtMesh* mesh = nullptr;
        Assert::AreEqual<int>(TT_OK, tt_mesh_create(engine, &desc, &mesh));
        Assert::AreEqual<int>(TT_OK, tt_texture_destroy(red));

        auto render = [&](uint32_t flags) {
            TtFrameParams params = MakeFrontParams();
            params.flags = TT_FRAME_SHOW_GRID | flags;
            // 格子は 0 の線を描かない（座標軸が描く）ので、4 分の 1 に縮めて、四角形の内側に線を通す
            DirectX::XMStoreFloat4x4(reinterpret_cast<DirectX::XMFLOAT4X4*>(params.grid_world),
                DirectX::XMMatrixScaling(0.25f, 0.25f, 0.25f) * DirectX::XMMatrixRotationX(-DirectX::XM_PIDIV2));
            TtDrawItem item = DrawItem(mesh);
            DirectX::XMStoreFloat4x4(reinterpret_cast<DirectX::XMFLOAT4X4*>(item.world), DirectX::XMMatrixTranslation(0.0f, 0.0f, 0.5f));
            params.items = &item;
            params.item_count = 1;
            return RenderAndRead(engine, view, params, width, height);
        };
        // 四角形の内側で、赤でない点（格子の線）を数える
        auto notRed = [&](const std::vector<uint8_t>& pixels) {
            int count = 0;
            for (int y = 17; y <= 31; ++y) {
                for (int x = 25; x <= 39; ++x) {
                    const uint8_t* p = &pixels[(static_cast<size_t>(y) * width + x) * 4];
                    count += (p[2] > 150 && p[1] < 40 && p[0] < 40) ? 0 : 1;
                }
            }
            return count;
        };

        Assert::IsTrue(notRed(render(0)) > 0, L"ふつうは、手前の格子の線が四角形の上に出る");
        Assert::AreEqual(0, notRed(render(TT_FRAME_GRID_BEHIND)), L"いちばん奥に描くと、格子は四角形に隠れる");

        Assert::AreEqual<int>(TT_OK, tt_mesh_destroy(mesh));
        Assert::AreEqual<int>(TT_OK, tt_view_destroy(view));
        Assert::AreEqual<int>(TT_OK, tt_engine_destroy(engine));
    }

    TEST_METHOD(GroundShadowDarkensTheFloorOnce)
    {
        // 高さ 1 と 1.25 の小さな水平の四角形を、斜め上（光は +X・下向き、傾き 2:1）から照らし、真上から見る。
        // 影は高さの 2 倍だけ +X へずれ、2 つの影の重なった所も、1 つの所と同じ濃さになる
        TtEngine* engine = CreateWarpEngine();
        constexpr int width = 64;
        constexpr int height = 48;
        TtView* view = nullptr;
        Assert::AreEqual<int>(TT_OK, tt_view_create_offscreen(engine, width, height, 0, &view));

        std::vector<TtMeshVertex> vertices;
        std::vector<uint32_t> indices;
        auto addFlatQuad = [&](float centerX, float y, float half) {
            const uint32_t base = static_cast<uint32_t>(vertices.size());
            const float xs[] = {-half, half, half, -half};
            const float zs[] = {half, half, -half, -half};
            for (int i = 0; i < 4; ++i) {
                vertices.push_back(TtMeshVertex{{centerX + xs[i], y, zs[i]}, {0.0f, 1.0f, 0.0f}, {0.0f, 0.0f}});
            }
            indices.insert(indices.end(), {base, base + 1, base + 2, base, base + 2, base + 3}); // 上から見て時計回り
        };
        addFlatQuad(0.0f, 1.0f, 0.5f);
        addFlatQuad(0.25f, 1.25f, 0.5f);

        auto render = [&](uint32_t materialFlags, uint32_t frameFlags, float lightY) {
            TtMeshMaterial material = QuadMaterial(nullptr, materialFlags | TT_MATERIAL_DOUBLE_SIDED);
            material.index_count = static_cast<uint32_t>(indices.size());
            TtMeshDesc desc{};
            desc.struct_size = sizeof(desc);
            desc.vertex_count = static_cast<uint32_t>(vertices.size());
            desc.vertices = vertices.data();
            desc.index_count = static_cast<uint32_t>(indices.size());
            desc.indices = indices.data();
            desc.material_count = 1;
            desc.materials = &material;
            TtMesh* mesh = nullptr;
            Assert::AreEqual<int>(TT_OK, tt_mesh_create(engine, &desc, &mesh));
            TtFrameParams params = MakeParams(frameFlags | TT_FRAME_USE_LIGHT);
            DirectX::XMStoreFloat4x4(reinterpret_cast<DirectX::XMFLOAT4X4*>(params.view),
                DirectX::XMMatrixLookAtLH(DirectX::XMVectorSet(0, 8, 0, 1), DirectX::XMVectorZero(), DirectX::XMVectorSet(0, 0, 1, 0)));
            params.light_direction[0] = 2.0f;
            params.light_direction[1] = lightY;
            params.light_color[0] = params.light_color[1] = params.light_color[2] = 0.6f;
            params.ground_shadow_color[3] = 0.5f; // 半透明の黒
            const TtDrawItem item = DrawItem(mesh);
            params.items = &item;
            params.item_count = 1;
            std::vector<uint8_t> pixels = RenderAndRead(engine, view, params, width, height);
            Assert::AreEqual<int>(TT_OK, tt_mesh_destroy(mesh));
            return pixels;
        };
        auto pixel = [&](const std::vector<uint8_t>& pixels, int x, int y) { return &pixels[(static_cast<size_t>(y) * width + x) * 4]; };
        auto isHalfBackground = [](const uint8_t* p) {
            return IsNear(p[0], kBackgroundB / 2, 3) && IsNear(p[1], kBackgroundG / 2, 3) && IsNear(p[2], kBackgroundR / 2, 3);
        };

        // 床（距離 8）の X = 1.86 → 45 ピクセル目、X = 2.42（重なり）→ 49、X = 2.97 → 53
        std::vector<uint8_t> shadowed = render(TT_MATERIAL_GROUND_SHADOW, TT_FRAME_GROUND_SHADOW, -1.0f);
        Assert::IsTrue(isHalfBackground(pixel(shadowed, 45, 24)), L"影は床を半透明の黒で暗くする");
        Assert::IsTrue(isHalfBackground(pixel(shadowed, 49, 24)), L"2 つの影が重なっても二重に暗くしない");
        Assert::IsTrue(isHalfBackground(pixel(shadowed, 53, 24)));
        Assert::IsTrue(IsBackground(pixel(shadowed, 62, 24), 1), L"影の外は床のまま");

        Assert::IsTrue(IsBackground(pixel(render(0, TT_FRAME_GROUND_SHADOW, -1.0f), 45, 24), 1), L"地面影の無い材質は影を落とさない");
        Assert::IsTrue(IsBackground(pixel(render(TT_MATERIAL_GROUND_SHADOW, 0, -1.0f), 45, 24), 1), L"地面の影を使わない描画");
        Assert::IsTrue(IsBackground(pixel(render(TT_MATERIAL_GROUND_SHADOW, TT_FRAME_GROUND_SHADOW, 1.0f), 45, 24), 1),
            L"下から照らすときは床に影を落とさない");

        Assert::AreEqual<int>(TT_OK, tt_view_destroy(view));
        Assert::AreEqual<int>(TT_OK, tt_engine_destroy(engine));
    }

    TEST_METHOD(SelfShadowFallsOnReceivers)
    {
        // 高さ 1 の小さな四角形（影を落とす）が、高さ 0 の大きな四角形（影を受ける・トゥーンの下が赤）に影を落とす。
        // 光は +X・下向き（傾き 2:1）なので、影は X = 1.5〜2.5 に落ちる。影の中はトゥーンのいちばん下の色を掛ける
        TtEngine* engine = CreateWarpEngine();
        constexpr int width = 64;
        constexpr int height = 48;
        TtView* view = nullptr;
        Assert::AreEqual<int>(TT_OK, tt_view_create_offscreen(engine, width, height, 0, &view));
        // 上半分が白、下半分が赤（4 段にして、光の当たる側を読んだときに赤が混ざらないようにする）
        const uint8_t toonPixels[] = {255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255,
            0, 0, 200, 255, 0, 0, 200, 255, 0, 0, 200, 255, 0, 0, 200, 255};
        TtTexture* toon = nullptr;
        Assert::AreEqual<int>(TT_OK, tt_texture_create(engine, 2, 4, toonPixels, 8, &toon));

        auto flatMesh = [&](float y, float half, uint32_t flags) {
            const TtMeshVertex vertices[] = {
                {{-half, y, half}, {0.0f, 1.0f, 0.0f}, {0.0f, 0.0f}},
                {{half, y, half}, {0.0f, 1.0f, 0.0f}, {0.0f, 0.0f}},
                {{half, y, -half}, {0.0f, 1.0f, 0.0f}, {0.0f, 0.0f}},
                {{-half, y, -half}, {0.0f, 1.0f, 0.0f}, {0.0f, 0.0f}},
            };
            TtMeshMaterial material = QuadMaterial(nullptr, flags | TT_MATERIAL_DOUBLE_SIDED);
            material.toon_texture = toon;
            TtMeshDesc desc{};
            desc.struct_size = sizeof(desc);
            desc.vertex_count = 4;
            desc.vertices = vertices;
            desc.index_count = 6;
            desc.indices = kClockwise;
            desc.material_count = 1;
            desc.materials = &material;
            TtMesh* mesh = nullptr;
            Assert::AreEqual<int>(TT_OK, tt_mesh_create(engine, &desc, &mesh));
            return mesh;
        };
        auto render = [&](uint32_t receiverFlags, uint32_t frameFlags) {
            TtMesh* floor = flatMesh(0.0f, 3.0f, receiverFlags);
            TtMesh* occluder = flatMesh(1.0f, 0.5f, TT_MATERIAL_CAST_SHADOW);
            TtFrameParams params = MakeParams(frameFlags | TT_FRAME_USE_LIGHT);
            DirectX::XMStoreFloat4x4(reinterpret_cast<DirectX::XMFLOAT4X4*>(params.view),
                DirectX::XMMatrixLookAtLH(DirectX::XMVectorSet(0, 8, 0, 1), DirectX::XMVectorZero(), DirectX::XMVectorSet(0, 0, 1, 0)));
            params.light_direction[0] = 2.0f;
            params.light_direction[1] = -1.0f;
            params.light_color[0] = params.light_color[1] = params.light_color[2] = 0.6f;
            const TtDrawItem items[] = {DrawItem(floor), DrawItem(occluder)};
            params.items = items;
            params.item_count = 2;
            std::vector<uint8_t> pixels = RenderAndRead(engine, view, params, width, height);
            Assert::AreEqual<int>(TT_OK, tt_mesh_destroy(floor));
            Assert::AreEqual<int>(TT_OK, tt_mesh_destroy(occluder));
            return pixels;
        };
        auto pixel = [&](const std::vector<uint8_t>& pixels, int x, int y) { return &pixels[(static_cast<size_t>(y) * width + x) * 4]; };
        auto isRed = [](const uint8_t* p) { return IsNear(p[2], 200, 6) && p[1] < 10 && p[0] < 10; };
        auto isWhite = [](const uint8_t* p) { return p[0] > 245 && p[1] > 245 && p[2] > 245; };

        // 床の X = 2.0 → 46 ピクセル目（影の中）、X = -2.0 → 17 ピクセル目（光の当たる所）
        std::vector<uint8_t> shadowed = render(TT_MATERIAL_RECEIVE_SHADOW, TT_FRAME_SELF_SHADOW);
        Assert::IsTrue(isRed(pixel(shadowed, 46, 24)), L"影の中はトゥーンのいちばん下の色");
        Assert::IsTrue(isWhite(pixel(shadowed, 17, 24)), L"光の当たる所は元の色");

        Assert::IsTrue(isWhite(pixel(render(0, TT_FRAME_SELF_SHADOW), 46, 24)), L"セルフ影を受けない材質には落ちない");
        Assert::IsTrue(isWhite(pixel(render(TT_MATERIAL_RECEIVE_SHADOW, 0), 46, 24)), L"セルフシャドウを使わない描画");

        Assert::AreEqual<int>(TT_OK, tt_texture_destroy(toon));
        Assert::AreEqual<int>(TT_OK, tt_view_destroy(view));
        Assert::AreEqual<int>(TT_OK, tt_engine_destroy(engine));
    }

    TEST_METHOD(SoftShadowsWidenWithDistanceFromTheCaster)
    {
        // 1 辺 2 の板（影を落とす）を高さ h に浮かせ、床（影を受ける・トゥーンの下が赤）に影を落とす。光は +X・下向き（傾き 2:1）。
        // 柔らかな影では、板が床から離れているほど影の縁が広くぼけ、近いほどくっきりのまま
        TtEngine* engine = CreateWarpEngine();
        constexpr int width = 128;
        constexpr int height = 96;
        TtView* view = nullptr;
        Assert::AreEqual<int>(TT_OK, tt_view_create_offscreen(engine, width, height, 0, &view));
        const uint8_t toonPixels[] = {255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255,
            0, 0, 200, 255, 0, 0, 200, 255, 0, 0, 200, 255, 0, 0, 200, 255};
        TtTexture* toon = nullptr;
        Assert::AreEqual<int>(TT_OK, tt_texture_create(engine, 2, 4, toonPixels, 8, &toon));

        auto flatMesh = [&](float y, float half, uint32_t flags) {
            const TtMeshVertex vertices[] = {
                {{-half, y, half}, {0.0f, 1.0f, 0.0f}, {0.0f, 0.0f}},
                {{half, y, half}, {0.0f, 1.0f, 0.0f}, {0.0f, 0.0f}},
                {{half, y, -half}, {0.0f, 1.0f, 0.0f}, {0.0f, 0.0f}},
                {{-half, y, -half}, {0.0f, 1.0f, 0.0f}, {0.0f, 0.0f}},
            };
            TtMeshMaterial material = QuadMaterial(nullptr, flags | TT_MATERIAL_DOUBLE_SIDED);
            material.toon_texture = toon;
            TtMeshDesc desc{};
            desc.struct_size = sizeof(desc);
            desc.vertex_count = 4;
            desc.vertices = vertices;
            desc.index_count = 6;
            desc.indices = kClockwise;
            desc.material_count = 1;
            desc.materials = &material;
            TtMesh* mesh = nullptr;
            Assert::AreEqual<int>(TT_OK, tt_mesh_create(engine, &desc, &mesh));
            return mesh;
        };
        auto render = [&](float occluderHeight, float softness) {
            TtMesh* floor = flatMesh(0.0f, 4.0f, TT_MATERIAL_RECEIVE_SHADOW);
            TtMesh* occluder = flatMesh(occluderHeight, 1.0f, TT_MATERIAL_CAST_SHADOW);
            TtFrameParams params = MakeParams(TT_FRAME_SELF_SHADOW | TT_FRAME_USE_LIGHT);
            DirectX::XMStoreFloat4x4(reinterpret_cast<DirectX::XMFLOAT4X4*>(params.view),
                DirectX::XMMatrixLookAtLH(DirectX::XMVectorSet(0, 8, 0, 1), DirectX::XMVectorZero(), DirectX::XMVectorSet(0, 0, 1, 0)));
            params.light_direction[0] = 2.0f;
            params.light_direction[1] = -1.0f;
            params.light_color[0] = params.light_color[1] = params.light_color[2] = 0.6f;
            params.shadow_softness = softness;
            const TtDrawItem items[] = {DrawItem(floor), DrawItem(occluder)};
            params.items = items;
            params.item_count = 2;
            std::vector<uint8_t> pixels = RenderAndRead(engine, view, params, width, height);
            Assert::AreEqual<int>(TT_OK, tt_mesh_destroy(floor));
            Assert::AreEqual<int>(TT_OK, tt_mesh_destroy(occluder));
            return pixels;
        };
        // 床の X を画素の列にする（真上から見て、1 あたり 14.5 画素）
        auto column = [](float x) { return static_cast<int>(64.0f + x * 14.48f); };
        // 影の右の縁（X = 1 + 2h）の前後 12 画素で、白でも赤でもない（ぼけた）画素の数
        auto blurred = [&](const std::vector<uint8_t>& pixels, float occluderHeight) {
            const int edge = column(1.0f + 2.0f * occluderHeight);
            int count = 0;
            for (int x = edge - 12; x <= edge + 12; ++x) {
                const int g = pixels[(static_cast<size_t>(height / 2) * width + x) * 4 + 1];
                count += (g > 30 && g < 225) ? 1 : 0;
            }
            return count;
        };
        auto green = [&](const std::vector<uint8_t>& pixels, float x) {
            return static_cast<int>(pixels[(static_cast<size_t>(height / 2) * width + column(x)) * 4 + 1]);
        };

        const std::vector<uint8_t> crisp = render(1.0f, 0.0f);
        Assert::IsTrue(blurred(crisp, 1.0f) <= 3, L"やわらかさ 0 なら、縁はくっきり（今までどおり）");
        Assert::IsTrue(green(crisp, 2.0f) < 10, L"影の真ん中は赤");

        const std::vector<uint8_t> high = render(1.0f, 0.1f);
        const int highBlur = blurred(high, 1.0f);
        Assert::IsTrue(highBlur >= 8, L"床から離れた板の影は、縁が広くぼける");
        Assert::IsTrue(green(high, 2.0f) < 30, L"影の真ん中は、ぼけても影のまま");
        Assert::IsTrue(green(high, -2.5f) > 245, L"影から離れた所は光が当たったまま");

        const std::vector<uint8_t> low = render(0.25f, 0.1f);
        Assert::IsTrue(blurred(low, 0.25f) + 4 <= highBlur, L"床に近い板の影は、縁がくっきりに近い");

        Assert::AreEqual<int>(TT_OK, tt_texture_destroy(toon));
        Assert::AreEqual<int>(TT_OK, tt_view_destroy(view));
        Assert::AreEqual<int>(TT_OK, tt_engine_destroy(engine));
    }

    TEST_METHOD(SoftGroundShadowsBlurAwayFromContact)
    {
        // 1 辺 2 の板（地面影あり）を高さ h に浮かせ、白い背景の上の床（高さ 0）に地面の影を落とす。光は +X・下向き（傾き 2:1）。
        // 柔らかな影では、地面の影も照明から見た奥行きで描き、板が床から離れているほど影の縁がぼける
        TtEngine* engine = CreateWarpEngine();
        constexpr int width = 128;
        constexpr int height = 96;
        TtView* view = nullptr;
        Assert::AreEqual<int>(TT_OK, tt_view_create_offscreen(engine, width, height, 0, &view));

        auto render = [&](float occluderHeight, float softness) {
            const float half = 1.0f;
            const TtMeshVertex vertices[] = {
                {{-half, occluderHeight, half}, {0.0f, 1.0f, 0.0f}, {0.0f, 0.0f}},
                {{half, occluderHeight, half}, {0.0f, 1.0f, 0.0f}, {0.0f, 0.0f}},
                {{half, occluderHeight, -half}, {0.0f, 1.0f, 0.0f}, {0.0f, 0.0f}},
                {{-half, occluderHeight, -half}, {0.0f, 1.0f, 0.0f}, {0.0f, 0.0f}},
            };
            TtMeshMaterial material = QuadMaterial(nullptr, TT_MATERIAL_GROUND_SHADOW | TT_MATERIAL_DOUBLE_SIDED);
            TtMeshDesc desc{};
            desc.struct_size = sizeof(desc);
            desc.vertex_count = 4;
            desc.vertices = vertices;
            desc.index_count = 6;
            desc.indices = kClockwise;
            desc.material_count = 1;
            desc.materials = &material;
            TtMesh* mesh = nullptr;
            Assert::AreEqual<int>(TT_OK, tt_mesh_create(engine, &desc, &mesh));
            TtFrameParams params = MakeParams(TT_FRAME_GROUND_SHADOW | TT_FRAME_USE_LIGHT | TT_FRAME_CLEAR_COLOR);
            DirectX::XMStoreFloat4x4(reinterpret_cast<DirectX::XMFLOAT4X4*>(params.view),
                DirectX::XMMatrixLookAtLH(DirectX::XMVectorSet(0, 8, 0, 1), DirectX::XMVectorZero(), DirectX::XMVectorSet(0, 0, 1, 0)));
            params.light_direction[0] = 2.0f;
            params.light_direction[1] = -1.0f;
            params.light_color[0] = params.light_color[1] = params.light_color[2] = 0.6f;
            params.ground_shadow_color[3] = 0.5f;
            params.clear_color[0] = params.clear_color[1] = params.clear_color[2] = params.clear_color[3] = 1.0f;
            params.shadow_softness = softness;
            const TtDrawItem item = DrawItem(mesh);
            params.items = &item;
            params.item_count = 1;
            std::vector<uint8_t> pixels = RenderAndRead(engine, view, params, width, height);
            Assert::AreEqual<int>(TT_OK, tt_mesh_destroy(mesh));
            return pixels;
        };
        auto column = [](float x) { return static_cast<int>(64.0f + x * 14.48f); };
        auto green = [&](const std::vector<uint8_t>& pixels, float x) {
            return static_cast<int>(pixels[(static_cast<size_t>(height / 2) * width + column(x)) * 4 + 1]);
        };
        // 影の右の縁（X = 1 + 2h）の前後 12 画素で、背景（白）でも影（半分の明るさ）でもない画素の数
        auto blurred = [&](const std::vector<uint8_t>& pixels, float occluderHeight) {
            const int edge = column(1.0f + 2.0f * occluderHeight);
            int count = 0;
            for (int x = edge - 12; x <= edge + 12; ++x) {
                const int g = pixels[(static_cast<size_t>(height / 2) * width + x) * 4 + 1];
                count += (g > 140 && g < 245) ? 1 : 0;
            }
            return count;
        };

        const std::vector<uint8_t> crisp = render(1.0f, 0.0f);
        Assert::IsTrue(blurred(crisp, 1.0f) <= 3, L"やわらかさ 0 なら、今までどおり押しつぶして描く（縁はくっきり）");
        Assert::IsTrue(IsNear(static_cast<uint8_t>(green(crisp, 2.0f)), 128, 4), L"影の真ん中は半透明の黒");

        const std::vector<uint8_t> high = render(1.0f, 0.1f);
        const int highBlur = blurred(high, 1.0f);
        Assert::IsTrue(highBlur >= 8, L"床から離れた板の地面の影は、縁が広くぼける");
        Assert::IsTrue(IsNear(static_cast<uint8_t>(green(high, 2.0f)), 128, 8), L"影の真ん中の濃さは同じ（二重に暗くしない）");
        Assert::IsTrue(green(high, -2.5f) > 250, L"影から離れた床は背景のまま");

        const std::vector<uint8_t> low = render(0.25f, 0.1f);
        Assert::IsTrue(blurred(low, 0.25f) + 4 <= highBlur, L"床に近い板の地面の影は、縁がくっきりに近い");

        Assert::AreEqual<int>(TT_OK, tt_view_destroy(view));
        Assert::AreEqual<int>(TT_OK, tt_engine_destroy(engine));
    }

    TEST_METHOD(EnvironmentLightsPbrMaterialsAndDrawsTheBackground)
    {
        TtEngine* engine = CreateWarpEngine();
        constexpr int width = 64;
        constexpr int height = 48;
        TtView* view = nullptr;
        Assert::AreEqual<int>(TT_OK, tt_view_create_offscreen(engine, width, height, 0, &view));

        // 作るときの確かめ
        TtEnvironment* bad = reinterpret_cast<TtEnvironment*>(1);
        const float one[3] = {1.0f, 1.0f, 1.0f};
        Assert::AreEqual<int>(TT_E_INVALID_ARG, tt_environment_create(engine, 0, 1, one, &bad), L"大きさ 0");
        Assert::IsNull(bad);
        Assert::AreEqual<int>(TT_E_INVALID_ARG, tt_environment_create(engine, 1, 1, nullptr, &bad), L"画素が無い");
        Assert::AreEqual<int>(TT_OK, tt_environment_destroy(nullptr));

        // 一様な赤の景色と、真ん中の半分（+Z の側）が赤・残り（-Z の側）が青の景色
        constexpr int envWidth = 8;
        constexpr int envHeight = 4;
        std::vector<float> red(envWidth * envHeight * 3, 0.0f);
        std::vector<float> halves(envWidth * envHeight * 3, 0.0f);
        for (int y = 0; y < envHeight; ++y) {
            for (int x = 0; x < envWidth; ++x) {
                const size_t i = (static_cast<size_t>(y) * envWidth + x) * 3;
                red[i] = 1.0f;
                halves[i + ((x >= 2 && x < 6) ? 0 : 2)] = 1.0f;
            }
        }
        TtEnvironment* redSky = nullptr;
        TtEnvironment* halfSky = nullptr;
        Assert::AreEqual<int>(TT_OK, tt_environment_create(engine, envWidth, envHeight, red.data(), &redSky));
        Assert::AreEqual<int>(TT_OK, tt_environment_create(engine, envWidth, envHeight, halves.data(), &halfSky));

        TtTexture* texture = CreateSolidTexture(engine, 128, 128, 128);
        const TtMeshMaterial material = QuadMaterial(texture);
        const TtMeshDesc desc = QuadDesc(kClockwise, &material);
        TtMesh* mesh = nullptr;
        Assert::AreEqual<int>(TT_OK, tt_mesh_create(engine, &desc, &mesh));
        Assert::AreEqual<int>(TT_OK, tt_texture_destroy(texture));
        auto setShading = [&](uint32_t shading) {
            TtMaterialState s{};
            s.diffuse[0] = s.diffuse[1] = s.diffuse[2] = s.diffuse[3] = 1.0f;
            s.ambient[0] = s.ambient[1] = s.ambient[2] = 1.0f;
            for (float* v : {s.texture_multiply, s.sphere_multiply, s.toon_multiply, s.base_tint}) {
                v[0] = v[1] = v[2] = v[3] = 1.0f;
            }
            s.edge_color[3] = 1.0f;
            s.shading = shading;
            s.roughness = 0.5f;
            Assert::AreEqual<int>(TT_OK, tt_mesh_update_materials(mesh, &s, 1));
        };
        auto render = [&](TtEnvironment* environment, float intensity, float rotation, uint32_t flags, bool orthographic = false) {
            TtFrameParams params = MakeFrontParams();
            params.flags = TT_FRAME_USE_LIGHT | TT_FRAME_CLEAR_COLOR | flags; // 照明は消す（黒）
            params.clear_color[3] = 1.0f;
            if (orthographic) {
                DirectX::XMStoreFloat4x4(reinterpret_cast<DirectX::XMFLOAT4X4*>(params.projection),
                    DirectX::XMMatrixOrthographicLH(8.0f, 6.0f, 0.1f, 100.0f));
            }
            params.light_direction[2] = 1.0f;
            params.environment = environment;
            params.environment_intensity = intensity;
            params.environment_rotation = rotation;
            TtDrawItem item = DrawItem(mesh);
            params.items = &item;
            params.item_count = 1;
            return RenderAndRead(engine, view, params, width, height);
        };
        const size_t center = (static_cast<size_t>(height / 2) * width + width / 2) * 4;
        const size_t corner = (static_cast<size_t>(2) * width + 2) * 4;

        setShading(TT_SHADING_PBR);
        std::vector<uint8_t> dark = render(nullptr, 1.0f, 0.0f, 0);
        Assert::IsTrue(dark[center + 2] < 5, L"照明も景色も無ければ、PBR は暗い");
        std::vector<uint8_t> lit = render(redSky, 1.0f, 0.0f, 0);
        Assert::IsTrue(lit[center + 2] > 100 && lit[center + 1] < 10 && lit[center] < 10, L"赤い景色に囲まれると、赤く照らされる");
        std::vector<uint8_t> dim = render(redSky, 0.5f, 0.0f, 0);
        Assert::IsTrue(std::abs(dim[center + 2] * 2 - lit[center + 2]) <= 6, L"明るさ 0.5 なら半分");
        Assert::AreEqual(0, static_cast<int>(lit[corner + 2]), L"背景に描かなければ、背景は消した色のまま");

        setShading(TT_SHADING_TOON);
        std::vector<uint8_t> toonPlain = render(nullptr, 1.0f, 0.0f, 0);
        std::vector<uint8_t> toonEnv = render(redSky, 1.0f, 0.0f, 0);
        Assert::IsTrue(std::abs(toonPlain[center + 1] - toonEnv[center + 1]) <= 1, L"トゥーンの材質には景色を使わない（MMD と同じ見え方）");

        // 背景: カメラは +Z を向くので、左上の隅も +Z の側（赤）。180 度回すと青になる
        std::vector<uint8_t> sky = render(halfSky, 1.0f, 0.0f, TT_FRAME_ENVIRONMENT_BACKGROUND);
        Assert::IsTrue(sky[corner + 2] > 245 && sky[corner] < 10, L"背景に、見る向きの景色（赤）を描く");
        std::vector<uint8_t> turned = render(halfSky, 1.0f, DirectX::XM_PI, TT_FRAME_ENVIRONMENT_BACKGROUND);
        Assert::IsTrue(turned[corner] > 245 && turned[corner + 2] < 10, L"景色を 180 度回すと、反対側（青）が見える");
        std::vector<uint8_t> fixedView = render(halfSky, 1.0f, 0.0f, TT_FRAME_ENVIRONMENT_BACKGROUND, true);
        Assert::IsTrue(fixedView[corner] < 5 && fixedView[corner + 2] < 5, L"平行投影（固定の視点）には背景を描かない");

        Assert::AreEqual<int>(TT_OK, tt_mesh_destroy(mesh));
        Assert::AreEqual<int>(TT_OK, tt_environment_destroy(redSky));
        Assert::AreEqual<int>(TT_OK, tt_environment_destroy(halfSky));
        Assert::AreEqual<int>(TT_OK, tt_view_destroy(view));
        Assert::AreEqual<int>(TT_OK, tt_engine_destroy(engine));
    }

    TEST_METHOD(MeshCreationRejectsInvalidData)
    {
        TtEngine* engine = CreateWarpEngine();
        TtMesh* mesh = reinterpret_cast<TtMesh*>(1);

        const uint32_t outOfRange[] = {0, 1, 4, 0, 2, 3};
        TtMeshMaterial material = QuadMaterial(nullptr);
        TtMeshDesc desc = QuadDesc(outOfRange, &material);
        Assert::AreEqual<int>(TT_E_INVALID_ARG, tt_mesh_create(engine, &desc, &mesh), L"頂点の数を超える番号");
        Assert::IsNull(mesh);

        material.index_count = 9;
        desc = QuadDesc(kClockwise, &material);
        Assert::AreEqual<int>(TT_E_INVALID_ARG, tt_mesh_create(engine, &desc, &mesh), L"材質の範囲が面の数を超える");

        material.index_count = 6;
        desc = QuadDesc(kClockwise, &material);
        desc.struct_size = 4;
        Assert::AreEqual<int>(TT_E_INVALID_ARG, tt_mesh_create(engine, &desc, &mesh), L"struct_size の食い違い");

        TtTexture* texture = nullptr;
        const uint8_t pixel[4] = {};
        Assert::AreEqual<int>(TT_E_INVALID_ARG, tt_texture_create(engine, 0, 1, pixel, 4, &texture), L"大きさ 0 のテクスチャ");
        Assert::AreEqual<int>(TT_E_INVALID_ARG, tt_texture_create(engine, 2, 1, pixel, 4, &texture), L"幅に足りない行の長さ");

        Assert::AreEqual<int>(TT_OK, tt_engine_destroy(engine));
    }

    TEST_METHOD(EngineCannotBeDestroyedWhileTexturesOrMeshesAlive)
    {
        TtEngine* engine = CreateWarpEngine();
        TtTexture* texture = CreateSolidTexture(engine, 1, 2, 3);
        const TtMeshMaterial material = QuadMaterial(texture);
        const TtMeshDesc desc = QuadDesc(kClockwise, &material);
        TtMesh* mesh = nullptr;
        Assert::AreEqual<int>(TT_OK, tt_mesh_create(engine, &desc, &mesh));

        Assert::AreEqual<int>(TT_E_INVALID_STATE, tt_engine_destroy(engine));
        Assert::AreEqual<int>(TT_OK, tt_texture_destroy(texture));
        Assert::AreEqual<int>(TT_E_INVALID_STATE, tt_engine_destroy(engine), L"メッシュがまだ残っている");
        Assert::AreEqual<int>(TT_OK, tt_mesh_destroy(mesh));
        Assert::AreEqual<int>(TT_OK, tt_engine_destroy(engine));
    }

    TEST_METHOD(SelectedMeshGetsHighlightBoxAndBadItemsAreRejected)
    {
        TtEngine* engine = CreateWarpEngine();
        constexpr int width = 64;
        constexpr int height = 48;
        TtView* view = nullptr;
        Assert::AreEqual<int>(TT_OK, tt_view_create_offscreen(engine, width, height, 0, &view));
        const TtMeshMaterial material = QuadMaterial(nullptr);
        const TtMeshDesc desc = QuadDesc(kClockwise, &material);
        TtMesh* mesh = nullptr;
        Assert::AreEqual<int>(TT_OK, tt_mesh_create(engine, &desc, &mesh));

        auto countHighlight = [&](uint32_t flags) {
            TtFrameParams params = MakeParams(0); // 斜めから見ると箱の辺が四角形の外にも出る
            const TtDrawItem item = DrawItem(mesh, flags);
            params.items = &item;
            params.item_count = 1;
            std::vector<uint8_t> pixels = RenderAndRead(engine, view, params, width, height);
            int count = 0;
            for (size_t i = 0; i < pixels.size(); i += 4) {
                if (pixels[i + 2] > 230 && pixels[i + 1] > 130 && pixels[i + 1] < 190 && pixels[i] < 80) {
                    ++count;
                }
            }
            return count;
        };
        Assert::IsTrue(countHighlight(TT_DRAW_SELECTED) > countHighlight(0) + 10, L"選択中は強調色の箱が描かれる");

        TtFrameParams bad = MakeParams(0);
        bad.item_count = 1;
        bad.items = nullptr;
        Assert::AreEqual<int>(TT_E_INVALID_ARG, tt_view_render(view, &bad), L"一覧が NULL");
        const TtDrawItem nullMesh = DrawItem(nullptr);
        bad.items = &nullMesh;
        Assert::AreEqual<int>(TT_E_INVALID_ARG, tt_view_render(view, &bad), L"メッシュが NULL");

        Assert::AreEqual<int>(TT_OK, tt_mesh_destroy(mesh));
        Assert::AreEqual<int>(TT_OK, tt_view_destroy(view));
        Assert::AreEqual<int>(TT_OK, tt_engine_destroy(engine));
    }

    TEST_METHOD(MeshVerticesAndMaterialsCanBeReplaced)
    {
        TtEngine* engine = CreateWarpEngine();
        constexpr int width = 32;
        constexpr int height = 32;
        TtView* view = nullptr;
        Assert::AreEqual<int>(TT_OK, tt_view_create_offscreen(engine, width, height, 0, &view));
        TtTexture* red = CreateSolidTexture(engine, 0, 0, 255);
        const TtMeshMaterial material = QuadMaterial(red);
        const TtMeshDesc desc = QuadDesc(kClockwise, &material);
        TtMesh* mesh = nullptr;
        Assert::AreEqual<int>(TT_OK, tt_mesh_create(engine, &desc, &mesh));
        Assert::AreEqual<int>(TT_OK, tt_texture_destroy(red));

        TtFrameParams params = MakeFrontParams();
        const TtDrawItem item = DrawItem(mesh);
        params.items = &item;
        params.item_count = 1;
        const size_t center = (static_cast<size_t>(height / 2) * width + width / 2) * 4;

        // 材質の値: テクスチャに 0 を掛けて緑を足す → 赤が消えて緑になる
        TtMaterialState state{};
        state.diffuse[0] = state.diffuse[1] = state.diffuse[2] = state.diffuse[3] = 1.0f;
        state.ambient[0] = state.ambient[1] = state.ambient[2] = 1.0f;
        state.texture_add[1] = 1.0f;
        state.texture_multiply[3] = 1.0f; // 透明度はそのまま
        Assert::AreEqual<int>(TT_OK, tt_mesh_update_materials(mesh, &state, 1));
        std::vector<uint8_t> pixels = RenderAndRead(engine, view, params, width, height);
        Assert::IsTrue(pixels[center + 2] < 20 && pixels[center + 1] > 150, L"材質の値を変えると色が変わる");

        // 頂点: 四角形を画面の外へ動かす → 中央は背景色になる
        TtMeshVertex moved[4];
        for (int i = 0; i < 4; ++i) {
            moved[i] = kQuad[i];
            moved[i].position[0] += 50.0f;
        }
        const float lo[3] = {49.0f, -1.0f, 0.0f};
        const float hi[3] = {51.0f, 1.0f, 0.0f};
        Assert::AreEqual<int>(TT_OK, tt_mesh_update_vertices(mesh, moved, 4, lo, hi));
        pixels = RenderAndRead(engine, view, params, width, height);
        Assert::IsTrue(IsBackground(&pixels[center], 1), L"頂点を置き換えると形が動く");

        Assert::AreEqual<int>(TT_E_INVALID_ARG, tt_mesh_update_vertices(mesh, moved, 3, nullptr, nullptr), L"頂点の数の食い違い");
        Assert::AreEqual<int>(TT_E_INVALID_ARG, tt_mesh_update_materials(mesh, &state, 2), L"材質の数の食い違い");

        Assert::AreEqual<int>(TT_OK, tt_mesh_destroy(mesh));
        Assert::AreEqual<int>(TT_OK, tt_view_destroy(view));
        Assert::AreEqual<int>(TT_OK, tt_engine_destroy(engine));
    }

    TEST_METHOD(OverlayLinesAreDrawnOnTopOfModels)
    {
        TtEngine* engine = CreateWarpEngine();
        constexpr int width = 32;
        constexpr int height = 32;
        TtView* view = nullptr;
        Assert::AreEqual<int>(TT_OK, tt_view_create_offscreen(engine, width, height, 0, &view));
        const TtMeshMaterial material = QuadMaterial(nullptr);
        const TtMeshDesc desc = QuadDesc(kClockwise, &material);
        TtMesh* mesh = nullptr;
        Assert::AreEqual<int>(TT_OK, tt_mesh_create(engine, &desc, &mesh));

        // 四角形より奥（z = 2）を横切る青い線。奥行きを無視して手前に描かれるはず
        const TtLineVertex lines[] = {
            {{-3.0f, 0.0f, 2.0f}, {0.0f, 0.0f, 1.0f, 1.0f}},
            {{3.0f, 0.0f, 2.0f}, {0.0f, 0.0f, 1.0f, 1.0f}},
        };
        TtFrameParams params = MakeFrontParams();
        const TtDrawItem item = DrawItem(mesh);
        params.items = &item;
        params.item_count = 1;
        params.overlay_lines = lines;
        params.overlay_vertex_count = 2;
        std::vector<uint8_t> pixels = RenderAndRead(engine, view, params, width, height);
        bool foundBlue = false;
        for (int y = height / 2 - 1; y <= height / 2; ++y) {
            const uint8_t* p = &pixels[(static_cast<size_t>(y) * width + width / 2) * 4];
            foundBlue |= p[0] > 200 && p[1] < 60 && p[2] < 60;
        }
        Assert::IsTrue(foundBlue, L"モデルより奥の線も手前に重ねて描く");

        params.overlay_lines = nullptr;
        Assert::AreEqual<int>(TT_E_INVALID_ARG, tt_view_render(view, &params), L"線の数があるのに NULL");

        Assert::AreEqual<int>(TT_OK, tt_mesh_destroy(mesh));
        Assert::AreEqual<int>(TT_OK, tt_view_destroy(view));
        Assert::AreEqual<int>(TT_OK, tt_engine_destroy(engine));
    }

    TEST_METHOD(DeviceLostIsReportedUntilRecoveredAndResourcesComeBack)
    {
        TtEngine* engine = CreateWarpEngine();
        constexpr int width = 32;
        constexpr int height = 32;
        TtView* view = nullptr;
        Assert::AreEqual<int>(TT_OK, tt_view_create_offscreen(engine, width, height, 0, &view));
        TtTexture* red = CreateSolidTexture(engine, 0, 0, 255);
        const TtMeshMaterial material = QuadMaterial(red);
        const TtMeshDesc desc = QuadDesc(kClockwise, &material);
        TtMesh* mesh = nullptr;
        Assert::AreEqual<int>(TT_OK, tt_mesh_create(engine, &desc, &mesh));
        Assert::AreEqual<int>(TT_OK, tt_texture_destroy(red), L"持ち手を破棄したテクスチャも、作り直しで戻ること");

        TtFrameParams params = MakeFrontParams();
        const TtDrawItem item = DrawItem(mesh);
        params.items = &item;
        params.item_count = 1;
        std::vector<uint8_t> pixels = RenderAndRead(engine, view, params, width, height);
        const size_t center = (static_cast<size_t>(height / 2) * width + width / 2) * 4;
        Assert::IsTrue(pixels[center + 2] > 150 && pixels[center + 1] < 20, L"最初は赤い四角形が写る");
        Assert::AreEqual<int>(TT_OK, tt_engine_check_device(engine));

        Assert::AreEqual<int>(TT_OK, tt_engine_simulate_device_lost(engine));
        Assert::AreEqual<int>(TT_E_DEVICE_LOST, tt_engine_check_device(engine));
        Assert::AreEqual<int>(TT_E_DEVICE_LOST, tt_view_render(view, &params), L"使えない間は描けない");
        Assert::AreEqual<int>(TT_E_DEVICE_LOST, tt_engine_wait_for_gpu(engine));
        TtTexture* texture = reinterpret_cast<TtTexture*>(1);
        const uint8_t pixel[4] = {1, 2, 3, 255};
        Assert::AreEqual<int>(TT_E_DEVICE_LOST, tt_texture_create(engine, 1, 1, pixel, 4, &texture));
        Assert::IsNull(texture);

        // 使えない間の変形と大きさの変更は覚えておき、作り直したときに使う
        TtMeshVertex moved[4];
        for (int i = 0; i < 4; ++i) {
            moved[i] = kQuad[i];
            moved[i].position[0] += 50.0f;
        }
        Assert::AreEqual<int>(TT_OK, tt_mesh_update_vertices(mesh, moved, 4, nullptr, nullptr));
        constexpr int newWidth = 40;
        constexpr int newHeight = 30;
        Assert::AreEqual<int>(TT_OK, tt_view_resize(view, newWidth, newHeight));

        Assert::AreEqual<int>(TT_OK, tt_engine_recover(engine));
        Assert::AreEqual<int>(TT_OK, tt_engine_check_device(engine));
        const size_t newCenter = (static_cast<size_t>(newHeight / 2) * newWidth + newWidth / 2) * 4;
        pixels = RenderAndRead(engine, view, params, newWidth, newHeight);
        Assert::IsTrue(IsBackground(&pixels[newCenter], 1), L"使えない間に送った頂点で作り直す");

        Assert::AreEqual<int>(TT_OK, tt_mesh_update_vertices(mesh, kQuad, 4, nullptr, nullptr));
        pixels = RenderAndRead(engine, view, params, newWidth, newHeight);
        Assert::IsTrue(pixels[newCenter + 2] > 150 && pixels[newCenter + 1] < 20, L"テクスチャの赤も戻る");

        Assert::AreEqual<int>(TT_OK, tt_mesh_destroy(mesh));
        Assert::AreEqual<int>(TT_OK, tt_view_destroy(view));
        Assert::AreEqual<int>(TT_OK, tt_engine_destroy(engine));
    }

    TEST_METHOD(RecoverWorksForWindowViewsAndCanBeRepeated)
    {
        HWND parent = CreateWindowExW(0, L"STATIC", L"test", WS_OVERLAPPEDWINDOW, 0, 0, 200, 200, nullptr, nullptr,
            GetModuleHandleW(nullptr), nullptr);
        Assert::IsNotNull(parent);
        TtEngine* engine = CreateWarpEngine();
        TtView* view = nullptr;
        Assert::AreEqual<int>(TT_OK, tt_view_create_for_window(engine, parent, 64, 48, &view));
        const TtFrameParams params = MakeParams();
        Assert::IsTrue(tt_view_render(view, &params) >= 0);

        Assert::AreEqual<int>(TT_OK, tt_engine_simulate_device_lost(engine));
        Assert::AreEqual<int>(TT_OK, tt_engine_recover(engine));
        Assert::IsTrue(tt_view_render(view, &params) >= 0, L"作り直したスワップチェーンに描ける");
        Assert::AreEqual<int>(TT_OK, tt_engine_recover(engine), L"使える状態で呼んでも作り直せる");
        Assert::IsTrue(tt_view_render(view, &params) >= 0);

        Assert::AreEqual<int>(TT_OK, tt_view_destroy(view));
        Assert::AreEqual<int>(TT_OK, tt_engine_destroy(engine));
        DestroyWindow(parent);
    }

    TEST_METHOD(LogCallbackReceivesEngineMessages)
    {
        std::vector<std::wstring> messages;
        tt_set_log_callback(
            [](int32_t, const wchar_t* message, void* userData) {
                static_cast<std::vector<std::wstring>*>(userData)->push_back(message);
            },
            &messages);
        TtEngine* engine = CreateWarpEngine();
        tt_set_log_callback(nullptr, nullptr);
        Assert::AreEqual<int>(TT_OK, tt_engine_destroy(engine));
        Assert::IsFalse(messages.empty(), L"初期化のログが届くこと");
    }
};
