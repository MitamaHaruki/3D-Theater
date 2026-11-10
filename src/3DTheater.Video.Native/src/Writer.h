// 動画の書き手（Media Foundation の Sink Writer）と、動画のファイルを調べる部分。
#pragma once

#include <windows.h>

#include <mfapi.h>
#include <mfidl.h>
#include <mfreadwrite.h>
#include <wrl/client.h>

#include <string>

#include "tv_video.h"

namespace tv {

using Microsoft::WRL::ComPtr;

// 内部で起きた失敗。C API の入口で受け止めて TvResult とエラー説明に変換する。
class Error {
public:
    Error(TvResult result, std::wstring message) : result_(result), message_(std::move(message)) {}

    TvResult Result() const noexcept { return result_; }
    const std::wstring& Message() const noexcept { return message_; }

private:
    TvResult result_;
    std::wstring message_;
};

void CheckHr(HRESULT hr, const wchar_t* what, TvResult result = TV_E_MEDIA);

// COM（MTA）と Media Foundation の開始・終了。作ったスレッドで壊すこと
class MediaStartup {
public:
    MediaStartup();
    ~MediaStartup();

    MediaStartup(const MediaStartup&) = delete;
    MediaStartup& operator=(const MediaStartup&) = delete;

private:
    bool uninitializeCom_ = false;
    bool shutdownMedia_ = false;
};

class Writer {
public:
    explicit Writer(const TvWriterDesc& desc);
    ~Writer();

    Writer(const Writer&) = delete;
    Writer& operator=(const Writer&) = delete;

    bool IsValid() const noexcept { return magic_ == kMagic; }

    void WriteVideo(const void* bgra, int stride, int64_t frameIndex);
    void WriteAudio(const int16_t* samples, int frameCount);
    void Finish();

private:
    static constexpr uint32_t kMagic = 0x52575654; // "TVWR"

    uint32_t magic_ = kMagic;
    MediaStartup startup_;
    ComPtr<IMFSinkWriter> writer_;
    DWORD videoStream_ = 0;
    DWORD audioStream_ = 0;
    bool hasAudio_ = false;
    UINT32 width_ = 0;
    UINT32 height_ = 0;
    UINT32 frameRate_ = 0;
    UINT32 sampleRate_ = 0;
    UINT32 channels_ = 0;
    int64_t audioFrames_ = 0;
    bool finished_ = false;
};

void Probe(const wchar_t* path, TvProbeInfo& info, void* firstFrame, int stride, int bufferSize);

} // namespace tv
