// カメラと動画のファイルから映像を読む部分（Media Foundation の Source Reader）。
#pragma once

#include <atomic>

#include "Writer.h"

namespace tv {

// カメラの一覧（名前と、開くときに使う目印）。capacity を超えた分は書かない。全部の数を返す
int ListCameras(TvCameraInfo* items, int capacity);

class Capture {
public:
    // カメラを開く。大きさとフレームの速さは、カメラが出せる形のうち、いちばん近いものを選ぶ
    static Capture* OpenCamera(const wchar_t* id, int width, int height, int frameRate);
    // 動画のファイルを開く（確かめ用。loop なら終わりで先頭に戻る）
    static Capture* OpenFile(const wchar_t* path, bool loop);
    // COM の部品の DLL から、カメラの元（IMFActivate）を直接作って開く（確かめ用。登録は要らない）
    static Capture* OpenActivate(const wchar_t* dllPath, const wchar_t* clsid);

    ~Capture();

    Capture(const Capture&) = delete;
    Capture& operator=(const Capture&) = delete;

    bool IsValid() const noexcept { return magic_ == kMagic; }

    void GetInfo(TvCaptureInfo& info) const;

    // 次のフレームを BGRA（上の行から）で写す。ファイルの終わり（繰り返さないとき）なら false
    bool Read(void* bgra, int stride, int bufferSize, int64_t& timestamp);

    // 待っている読み出しをやめさせる（ほかのスレッドから呼んでよい）
    void Cancel();

private:
    Capture() = default;
    void UseRgbOutput();
    void CreateReader();

    static constexpr uint32_t kMagic = 0x43415054; // "CAPT"
    uint32_t magic_ = kMagic;
    MediaStartup startup_;
    ComPtr<IMFActivate> activate_;
    ComPtr<IMFMediaSource> source_;
    ComPtr<IMFSourceReader> reader_;
    UINT32 width_ = 0;
    UINT32 height_ = 0;
    double frameRate_ = 0;
    LONG defaultStride_ = 0;
    bool loop_ = false;
    bool isFile_ = false;
    std::atomic<bool> cancelled_ = false;
};

} // namespace tv
