// 公開 C API を通したネイティブエンジンのテスト。
// GPU の無い環境でも動くよう、ソフトウェア描画（WARP）を使う。
#include <windows.h>

#include <CppUnitTest.h>
#include <DirectXMath.h>

#include <cstdlib>
#include <string>
#include <thread>
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
