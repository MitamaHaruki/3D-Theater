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
