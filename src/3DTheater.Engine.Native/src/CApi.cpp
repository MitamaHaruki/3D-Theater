// 公開 C API の入口。ここで引数を確かめ、内部の例外を TtResult に変換する。
#include <new>

#include "Engine.h"
#include "Log.h"
#include "Resources.h"
#include "View.h"

namespace {

constexpr int32_t kMaxViewSize = 16384;

template <class F>
TtResult Guard(F&& body) noexcept
{
    try {
        body();
        return TT_OK;
    } catch (const tt::Error& e) {
        tt::SetLastErrorMessage(e.Message());
        // 「この環境では使えない」は呼び出し側が別の方法へ切り替え、「GPU が使えない」は作り直す想定なので、警告にとどめる
        const bool handledByCaller = e.Result() == TT_E_NOT_SUPPORTED || e.Result() == TT_E_DEVICE_LOST;
        tt::Log(handledByCaller ? TT_LOG_WARNING : TT_LOG_ERROR, e.Message());
        return e.Result();
    } catch (const std::bad_alloc&) {
        tt::SetLastErrorMessage(L"メモリが足りません");
        tt::Log(TT_LOG_ERROR, L"メモリが足りません");
        return TT_E_OUT_OF_MEMORY;
    } catch (...) {
        tt::SetLastErrorMessage(L"予期しない例外が発生しました");
        tt::Log(TT_LOG_ERROR, L"予期しない例外が発生しました");
        return TT_E_UNKNOWN;
    }
}

void Require(bool condition, const wchar_t* message)
{
    if (!condition) {
        throw tt::Error(TT_E_INVALID_ARG, message);
    }
}

tt::Engine& ToEngine(TtEngine* handle)
{
    Require(handle != nullptr, L"engine が NULL です");
    auto* engine = reinterpret_cast<tt::Engine*>(handle);
    Require(engine->IsValid(), L"engine が正しくありません（破棄済みの可能性があります）");
    engine->CheckThread();
    return *engine;
}

tt::View& ToView(TtView* handle)
{
    Require(handle != nullptr, L"view が NULL です");
    auto* view = reinterpret_cast<tt::View*>(handle);
    Require(view->IsValid(), L"view が正しくありません（破棄済みの可能性があります）");
    view->OwnerEngine().CheckThread();
    return *view;
}

tt::Texture& ToTexture(TtTexture* handle)
{
    Require(handle != nullptr, L"texture が NULL です");
    auto* texture = reinterpret_cast<tt::Texture*>(handle);
    Require(texture->IsValid(), L"texture が正しくありません（破棄済みの可能性があります）");
    texture->OwnerEngine().CheckThread();
    return *texture;
}

tt::Environment& ToEnvironment(TtEnvironment* handle)
{
    Require(handle != nullptr, L"environment が NULL です");
    auto* environment = reinterpret_cast<tt::Environment*>(handle);
    Require(environment->IsValid(), L"environment が正しくありません（破棄済みの可能性があります）");
    environment->OwnerEngine().CheckThread();
    return *environment;
}

tt::Mesh& ToMesh(TtMesh* handle)
{
    Require(handle != nullptr, L"mesh が NULL です");
    auto* mesh = reinterpret_cast<tt::Mesh*>(handle);
    Require(mesh->IsValid(), L"mesh が正しくありません（破棄済みの可能性があります）");
    mesh->OwnerEngine().CheckThread();
    return *mesh;
}

void RequireSize(int32_t width, int32_t height, int32_t minimum)
{
    Require(width >= minimum && height >= minimum && width <= kMaxViewSize && height <= kMaxViewSize,
        L"ビューの大きさが範囲外です");
}

} // namespace

extern "C" {

TT_API uint32_t TT_CALL tt_get_api_version(void)
{
    return TT_API_VERSION;
}

TT_API void TT_CALL tt_set_log_callback(TtLogCallback callback, void* user_data)
{
    tt::SetLogCallback(callback, user_data);
}

TT_API int32_t TT_CALL tt_get_last_error_message(wchar_t* buffer, int32_t capacity)
{
    const std::wstring& message = tt::LastErrorMessage();
    if (buffer && capacity > 0) {
        wcsncpy_s(buffer, static_cast<size_t>(capacity), message.c_str(), _TRUNCATE);
    }
    return static_cast<int32_t>(message.size());
}

TT_API TtResult TT_CALL tt_engine_create(const TtEngineDesc* desc, TtEngine** out_engine)
{
    return Guard([&] {
        Require(out_engine != nullptr, L"out_engine が NULL です");
        *out_engine = nullptr;
        Require(desc != nullptr, L"desc が NULL です");
        Require(desc->struct_size == sizeof(TtEngineDesc), L"TtEngineDesc の struct_size が合いません");
        auto* engine = new tt::Engine(*desc);
        *out_engine = reinterpret_cast<TtEngine*>(engine);
    });
}

TT_API TtResult TT_CALL tt_engine_destroy(TtEngine* engine)
{
    if (!engine) {
        return TT_OK;
    }
    return Guard([&] {
        tt::Engine& e = ToEngine(engine);
        if (e.LiveObjectCount() > 0) {
            throw tt::Error(TT_E_INVALID_STATE,
                std::format(L"ビュー・テクスチャ・メッシュが合わせて {} 個残っているためエンジンを破棄できません。先にそれらを破棄してください",
                    e.LiveObjectCount()));
        }
        delete &e;
    });
}

TT_API TtResult TT_CALL tt_texture_create(
    TtEngine* engine, int32_t width, int32_t height, const void* bgra_pixels, int32_t stride, TtTexture** out_texture)
{
    return Guard([&] {
        Require(out_texture != nullptr, L"out_texture が NULL です");
        *out_texture = nullptr;
        tt::Engine& e = ToEngine(engine);
        auto* texture = new tt::Texture(e, width, height, bgra_pixels, stride);
        *out_texture = reinterpret_cast<TtTexture*>(texture);
    });
}

TT_API TtResult TT_CALL tt_texture_destroy(TtTexture* texture)
{
    if (!texture) {
        return TT_OK;
    }
    return Guard([&] { delete &ToTexture(texture); });
}

TT_API TtResult TT_CALL tt_environment_create(
    TtEngine* engine, int32_t width, int32_t height, const float* rgb_pixels, TtEnvironment** out_environment)
{
    return Guard([&] {
        Require(out_environment != nullptr, L"out_environment が NULL です");
        *out_environment = nullptr;
        tt::Engine& e = ToEngine(engine);
        auto* environment = new tt::Environment(e, width, height, rgb_pixels);
        *out_environment = reinterpret_cast<TtEnvironment*>(environment);
    });
}

TT_API TtResult TT_CALL tt_environment_destroy(TtEnvironment* environment)
{
    if (!environment) {
        return TT_OK;
    }
    return Guard([&] { delete &ToEnvironment(environment); });
}

TT_API TtResult TT_CALL tt_mesh_create(TtEngine* engine, const TtMeshDesc* desc, TtMesh** out_mesh)
{
    return Guard([&] {
        Require(out_mesh != nullptr, L"out_mesh が NULL です");
        *out_mesh = nullptr;
        tt::Engine& e = ToEngine(engine);
        Require(desc != nullptr, L"desc が NULL です");
        Require(desc->struct_size == sizeof(TtMeshDesc), L"TtMeshDesc の struct_size が合いません");
        auto* mesh = new tt::Mesh(e, *desc);
        *out_mesh = reinterpret_cast<TtMesh*>(mesh);
    });
}

TT_API TtResult TT_CALL tt_mesh_destroy(TtMesh* mesh)
{
    if (!mesh) {
        return TT_OK;
    }
    return Guard([&] { delete &ToMesh(mesh); });
}

TT_API TtResult TT_CALL tt_mesh_update_vertices(
    TtMesh* mesh, const TtMeshVertex* vertices, uint32_t vertex_count, const float* bounds_min, const float* bounds_max)
{
    return Guard([&] { ToMesh(mesh).UpdateVertices(vertices, vertex_count, bounds_min, bounds_max); });
}

TT_API TtResult TT_CALL tt_mesh_update_materials(TtMesh* mesh, const TtMaterialState* states, uint32_t material_count)
{
    return Guard([&] { ToMesh(mesh).UpdateMaterials(states, material_count); });
}

TT_API TtResult TT_CALL tt_engine_get_info(TtEngine* engine, TtEngineInfo* out_info)
{
    return Guard([&] {
        tt::Engine& e = ToEngine(engine);
        Require(out_info != nullptr, L"out_info が NULL です");
        Require(out_info->struct_size == sizeof(TtEngineInfo), L"TtEngineInfo の struct_size が合いません");
        e.FillInfo(*out_info);
    });
}

TT_API TtResult TT_CALL tt_engine_query_video_memory(TtEngine* engine, TtVideoMemoryInfo* out_info)
{
    return Guard([&] {
        tt::Engine& e = ToEngine(engine);
        Require(out_info != nullptr, L"out_info が NULL です");
        Require(out_info->struct_size == sizeof(TtVideoMemoryInfo), L"TtVideoMemoryInfo の struct_size が合いません");
        e.QueryVideoMemory(*out_info);
    });
}

TT_API TtResult TT_CALL tt_engine_wait_for_gpu(TtEngine* engine)
{
    return Guard([&] { ToEngine(engine).WaitForGpu(); });
}

TT_API TtResult TT_CALL tt_engine_flush(TtEngine* engine)
{
    return Guard([&] { ToEngine(engine).Flush(); });
}

TT_API TtResult TT_CALL tt_engine_check_device(TtEngine* engine)
{
    // 使えないのは失敗ではなく状態なので、ログには残さない
    try {
        return ToEngine(engine).IsDeviceLost() ? TT_E_DEVICE_LOST : TT_OK;
    } catch (const tt::Error& e) {
        tt::SetLastErrorMessage(e.Message());
        return e.Result();
    }
}

TT_API TtResult TT_CALL tt_engine_recover(TtEngine* engine)
{
    return Guard([&] { ToEngine(engine).Recover(); });
}

TT_API TtResult TT_CALL tt_engine_simulate_device_lost(TtEngine* engine)
{
    return Guard([&] {
        tt::Engine& e = ToEngine(engine);
        e.SimulateDeviceLost();
        tt::Log(TT_LOG_WARNING, L"GPU が使えなくなったことにしました（試験・診断用）");
    });
}

TT_API TtResult TT_CALL tt_view_create_for_window(
    TtEngine* engine, void* parent_hwnd, int32_t width, int32_t height, TtView** out_view)
{
    return Guard([&] {
        Require(out_view != nullptr, L"out_view が NULL です");
        *out_view = nullptr;
        tt::Engine& e = ToEngine(engine);
        RequireSize(width, height, 0);
        auto view = tt::View::CreateForWindow(e, static_cast<HWND>(parent_hwnd), width, height);
        *out_view = reinterpret_cast<TtView*>(view.release());
    });
}

TT_API TtResult TT_CALL tt_view_create_offscreen(
    TtEngine* engine, int32_t width, int32_t height, uint32_t flags, TtView** out_view)
{
    return Guard([&] {
        Require(out_view != nullptr, L"out_view が NULL です");
        *out_view = nullptr;
        tt::Engine& e = ToEngine(engine);
        RequireSize(width, height, 1);
        Require((flags & ~static_cast<uint32_t>(TT_VIEW_FLAG_SHARE_D3D9 | TT_VIEW_FLAG_DEFERRED_PUBLISH)) == 0, L"flags に未知の値があります");
        auto view = tt::View::CreateOffscreen(e, width, height, flags);
        *out_view = reinterpret_cast<TtView*>(view.release());
    });
}

TT_API TtResult TT_CALL tt_view_destroy(TtView* view)
{
    if (!view) {
        return TT_OK;
    }
    return Guard([&] { delete &ToView(view); });
}

TT_API TtResult TT_CALL tt_view_get_window(TtView* view, void** out_hwnd)
{
    return Guard([&] {
        Require(out_hwnd != nullptr, L"out_hwnd が NULL です");
        *out_hwnd = ToView(view).Window();
    });
}

TT_API TtResult TT_CALL tt_view_get_d3d9_surface(TtView* view, void** out_surface)
{
    return Guard([&] {
        Require(out_surface != nullptr, L"out_surface が NULL です");
        *out_surface = ToView(view).D3D9Surface();
    });
}

TT_API TtResult TT_CALL tt_view_resize(TtView* view, int32_t width, int32_t height)
{
    return Guard([&] {
        tt::View& v = ToView(view);
        RequireSize(width, height, 0);
        v.Resize(width, height);
    });
}

TT_API TtResult TT_CALL tt_view_set_sample_count(TtView* view, uint32_t sample_count, uint32_t* out_actual)
{
    return Guard([&] {
        tt::View& v = ToView(view);
        Require(sample_count == 1 || sample_count == 2 || sample_count == 4 || sample_count == 8,
            L"なめらかさ（sample_count）は 1・2・4・8 のどれかにしてください");
        const uint32_t actual = v.SetSampleCount(sample_count);
        if (out_actual) {
            *out_actual = actual;
        }
    });
}

TT_API TtResult TT_CALL tt_view_render(TtView* view, const TtFrameParams* params)
{
    bool rendered = true;
    const TtResult result = Guard([&] {
        tt::View& v = ToView(view);
        Require(params != nullptr, L"params が NULL です");
        Require(params->struct_size == sizeof(TtFrameParams), L"TtFrameParams の struct_size が合いません");
        Require(params->item_count == 0 || params->items != nullptr, L"描くモデルの一覧が NULL です");
        Require(params->overlay_vertex_count == 0 || params->overlay_lines != nullptr, L"重ね描きの線が NULL です");
        Require(params->light_count == 0 || params->lights != nullptr, L"足す照明の一覧が NULL です");
        for (uint32_t i = 0; i < params->item_count; ++i) {
            tt::Mesh& mesh = ToMesh(params->items[i].mesh);
            Require(&mesh.OwnerEngine() == &v.OwnerEngine(), L"別のエンジンで作ったメッシュは描けません");
        }
        if (params->environment) {
            Require(&ToEnvironment(params->environment).OwnerEngine() == &v.OwnerEngine(), L"別のエンジンで作った環境の画像は使えません");
        }
        rendered = v.Render(*params);
    });
    return (result == TT_OK && !rendered) ? TT_S_NOT_READY : result;
}

TT_API TtResult TT_CALL tt_view_read_pixels(TtView* view, void* buffer, int32_t stride, int32_t buffer_size)
{
    return Guard([&] { ToView(view).ReadPixels(buffer, stride, buffer_size); });
}

TT_API TtResult TT_CALL tt_view_publish(TtView* view)
{
    return Guard([&] { ToView(view).Publish(); });
}

} // extern "C"
