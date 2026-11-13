// 公開 C API の入口。引数を確かめ、内部の例外を TvResult に変換する。
#include <new>
#include <string>

#include "Capture.h"
#include "Writer.h"

namespace {

thread_local std::wstring g_lastError;

template <class F>
TvResult Guard(F&& body) noexcept
{
    try {
        body();
        return TV_OK;
    } catch (const tv::Error& e) {
        g_lastError = e.Message();
        return e.Result();
    } catch (const std::bad_alloc&) {
        g_lastError = L"メモリが足りません";
        return TV_E_OUT_OF_MEMORY;
    } catch (...) {
        g_lastError = L"予期しない例外が発生しました";
        return TV_E_UNKNOWN;
    }
}

tv::Writer& ToWriter(TvWriter* handle)
{
    auto* writer = reinterpret_cast<tv::Writer*>(handle);
    if (!writer || !writer->IsValid()) {
        throw tv::Error(TV_E_INVALID_ARG, L"writer が正しくありません（NULL か、破棄済みの可能性があります）");
    }
    return *writer;
}

void Require(bool condition, const wchar_t* message)
{
    if (!condition) {
        throw tv::Error(TV_E_INVALID_ARG, message);
    }
}

tv::Capture& ToCapture(TvCapture* handle)
{
    auto* capture = reinterpret_cast<tv::Capture*>(handle);
    if (!capture || !capture->IsValid()) {
        throw tv::Error(TV_E_INVALID_ARG, L"capture が正しくありません（NULL か、破棄済みの可能性があります）");
    }
    return *capture;
}

} // namespace

extern "C" {

TV_API uint32_t TV_CALL tv_get_api_version(void)
{
    return TV_API_VERSION;
}

TV_API int32_t TV_CALL tv_get_last_error_message(wchar_t* buffer, int32_t capacity)
{
    if (buffer && capacity > 0) {
        wcsncpy_s(buffer, static_cast<size_t>(capacity), g_lastError.c_str(), _TRUNCATE);
    }
    return static_cast<int32_t>(g_lastError.size());
}

TV_API TvResult TV_CALL tv_writer_create(const TvWriterDesc* desc, TvWriter** out_writer)
{
    return Guard([&] {
        Require(out_writer != nullptr, L"out_writer が NULL です");
        *out_writer = nullptr;
        Require(desc && desc->struct_size == sizeof(TvWriterDesc), L"TvWriterDesc が NULL か、struct_size が合いません");
        Require(desc->path && desc->path[0] != L'\0', L"書き出し先が空です");
        Require(desc->width >= 16 && desc->height >= 16 && desc->width <= 8192 && desc->height <= 8192,
            L"映像の大きさは 16〜8192 にしてください");
        Require(desc->width % 2 == 0 && desc->height % 2 == 0, L"映像の幅と高さは偶数にしてください（H.264 の決まり）");
        Require(desc->frame_rate >= 1 && desc->frame_rate <= 240, L"フレームの速さは 1〜240 にしてください");
        Require(desc->video_bitrate >= 100'000, L"映像のビット数が少なすぎます");
        if (desc->audio_sample_rate != 0) {
            Require(desc->audio_sample_rate == 44100 || desc->audio_sample_rate == 48000, L"音声のサンプリング周波数は 44100 か 48000 にしてください");
            Require(desc->audio_channels == 1 || desc->audio_channels == 2, L"音声のチャンネルは 1 か 2 にしてください");
            const uint32_t b = desc->audio_bitrate;
            Require(b == 96000 || b == 128000 || b == 160000 || b == 192000, L"音声のビット数は 96000・128000・160000・192000 のどれかにしてください");
        }
        *out_writer = reinterpret_cast<TvWriter*>(new tv::Writer(*desc));
    });
}

TV_API TvResult TV_CALL tv_writer_write_video(TvWriter* writer, const void* bgra, int32_t stride, int64_t frame_index)
{
    return Guard([&] { ToWriter(writer).WriteVideo(bgra, stride, frame_index); });
}

TV_API TvResult TV_CALL tv_writer_write_audio(TvWriter* writer, const int16_t* samples, int32_t frame_count)
{
    return Guard([&] { ToWriter(writer).WriteAudio(samples, frame_count); });
}

TV_API TvResult TV_CALL tv_writer_finish(TvWriter* writer)
{
    return Guard([&] { ToWriter(writer).Finish(); });
}

TV_API TvResult TV_CALL tv_writer_destroy(TvWriter* writer)
{
    if (!writer) {
        return TV_OK;
    }
    return Guard([&] { delete &ToWriter(writer); });
}

TV_API TvResult TV_CALL tv_probe_file(
    const wchar_t* path, TvProbeInfo* out_info, void* first_frame, int32_t stride, int32_t buffer_size)
{
    return Guard([&] {
        Require(path && path[0] != L'\0', L"path が空です");
        Require(out_info && out_info->struct_size == sizeof(TvProbeInfo), L"TvProbeInfo が NULL か、struct_size が合いません");
        TvProbeInfo info{};
        info.struct_size = sizeof(TvProbeInfo);
        tv::Probe(path, info, first_frame, stride, buffer_size);
        *out_info = info;
    });
}

TV_API int32_t TV_CALL tv_camera_list(TvCameraInfo* items, int32_t capacity)
{
    int32_t count = 0;
    const TvResult result = Guard([&] {
        Require(capacity >= 0 && (capacity == 0 || items != nullptr), L"items が NULL です");
        count = tv::ListCameras(items, capacity);
    });
    return result == TV_OK ? count : result;
}

TV_API TvResult TV_CALL tv_capture_open_camera(const wchar_t* id, int32_t width, int32_t height, int32_t frame_rate, TvCapture** out_capture)
{
    return Guard([&] {
        Require(out_capture != nullptr, L"out_capture が NULL です");
        *out_capture = nullptr;
        Require(id && id[0] != L'\0', L"カメラの目印が空です");
        Require(width >= 16 && height >= 16 && width <= 8192 && height <= 8192, L"大きさは 16〜8192 にしてください");
        Require(frame_rate >= 1 && frame_rate <= 240, L"フレームの速さは 1〜240 にしてください");
        *out_capture = reinterpret_cast<TvCapture*>(tv::Capture::OpenCamera(id, width, height, frame_rate));
    });
}

TV_API TvResult TV_CALL tv_capture_open_file(const wchar_t* path, int32_t loop, TvCapture** out_capture)
{
    return Guard([&] {
        Require(out_capture != nullptr, L"out_capture が NULL です");
        *out_capture = nullptr;
        Require(path && path[0] != L'\0', L"path が空です");
        *out_capture = reinterpret_cast<TvCapture*>(tv::Capture::OpenFile(path, loop != 0));
    });
}

TV_API TvResult TV_CALL tv_capture_get_info(TvCapture* capture, TvCaptureInfo* out_info)
{
    return Guard([&] {
        Require(out_info && out_info->struct_size == sizeof(TvCaptureInfo), L"TvCaptureInfo が NULL か、struct_size が合いません");
        TvCaptureInfo info{};
        info.struct_size = sizeof(TvCaptureInfo);
        ToCapture(capture).GetInfo(info);
        *out_info = info;
    });
}

TV_API TvResult TV_CALL tv_capture_read(TvCapture* capture, void* bgra, int32_t stride, int32_t buffer_size, int64_t* out_timestamp)
{
    bool read = false;
    const TvResult result = Guard([&] {
        int64_t timestamp = 0;
        read = ToCapture(capture).Read(bgra, stride, buffer_size, timestamp);
        if (out_timestamp) {
            *out_timestamp = timestamp;
        }
    });
    return result == TV_OK && !read ? TV_E_END_OF_STREAM : result;
}

TV_API TvResult TV_CALL tv_capture_cancel(TvCapture* capture)
{
    return Guard([&] { ToCapture(capture).Cancel(); });
}

TV_API TvResult TV_CALL tv_capture_destroy(TvCapture* capture)
{
    if (!capture) {
        return TV_OK;
    }
    return Guard([&] { delete &ToCapture(capture); });
}

} // extern "C"
