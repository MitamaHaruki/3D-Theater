// 公開 C API の入口。引数を確かめ、内部の例外を TpResult に変換する。
#include <new>
#include <string>

#include "World.h"

namespace {

thread_local std::wstring g_lastError;

template <class F>
TpResult Guard(F&& body) noexcept
{
    try {
        body();
        return TP_OK;
    } catch (const tp::Error& e) {
        g_lastError = e.Message();
        return e.Result();
    } catch (const std::bad_alloc&) {
        g_lastError = L"メモリが足りません";
        return TP_E_OUT_OF_MEMORY;
    } catch (...) {
        g_lastError = L"予期しない例外が発生しました";
        return TP_E_UNKNOWN;
    }
}

tp::World& ToWorld(TpWorld* handle)
{
    auto* world = reinterpret_cast<tp::World*>(handle);
    if (!world || !world->IsValid()) {
        throw tp::Error(TP_E_INVALID_ARG, L"world が正しくありません（NULL か、破棄済みの可能性があります）");
    }
    return *world;
}

} // namespace

extern "C" {

TP_API uint32_t TP_CALL tp_get_api_version(void)
{
    return TP_API_VERSION;
}

TP_API int32_t TP_CALL tp_get_last_error_message(wchar_t* buffer, int32_t capacity)
{
    if (buffer && capacity > 0) {
        wcsncpy_s(buffer, static_cast<size_t>(capacity), g_lastError.c_str(), _TRUNCATE);
    }
    return static_cast<int32_t>(g_lastError.size());
}

TP_API TpResult TP_CALL tp_world_create(const TpWorldDesc* desc, TpWorld** out_world)
{
    return Guard([&] {
        if (!out_world) {
            throw tp::Error(TP_E_INVALID_ARG, L"out_world が NULL です");
        }
        *out_world = nullptr;
        if (!desc || desc->struct_size != sizeof(TpWorldDesc)) {
            throw tp::Error(TP_E_INVALID_ARG, L"TpWorldDesc が NULL か、struct_size が合いません");
        }
        *out_world = reinterpret_cast<TpWorld*>(new tp::World(*desc));
    });
}

TP_API TpResult TP_CALL tp_world_destroy(TpWorld* world)
{
    if (!world) {
        return TP_OK;
    }
    return Guard([&] { delete &ToWorld(world); });
}

TP_API TpResult TP_CALL tp_world_add_bodies(TpWorld* world, const TpBodyDesc* bodies, int32_t count)
{
    return Guard([&] { ToWorld(world).AddBodies(bodies, count); });
}

TP_API TpResult TP_CALL tp_world_add_joints(TpWorld* world, const TpJointDesc* joints, int32_t count)
{
    return Guard([&] { ToWorld(world).AddJoints(joints, count); });
}

TP_API TpResult TP_CALL tp_world_set_gravity(TpWorld* world, const float gravity[3])
{
    return Guard([&] { ToWorld(world).SetGravity(gravity); });
}

TP_API int32_t TP_CALL tp_world_body_count(TpWorld* world)
{
    auto* w = reinterpret_cast<tp::World*>(world);
    return w && w->IsValid() ? w->BodyCount() : 0;
}

TP_API TpResult TP_CALL tp_world_reset(TpWorld* world, const TpTransform* transforms, int32_t count)
{
    return Guard([&] { ToWorld(world).Reset(transforms, count); });
}

TP_API TpResult TP_CALL tp_world_step(
    TpWorld* world, const TpTransform* targets, int32_t count, float time_step, int32_t substeps)
{
    return Guard([&] { ToWorld(world).Step(targets, count, time_step, substeps); });
}

TP_API TpResult TP_CALL tp_world_get_transforms(TpWorld* world, TpTransform* out_transforms, int32_t count)
{
    return Guard([&] { ToWorld(world).GetTransforms(out_transforms, count); });
}

} // extern "C"
