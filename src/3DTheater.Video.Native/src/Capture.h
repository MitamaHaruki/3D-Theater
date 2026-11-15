// カメラと動画のファイルから映像を読む部分（Media Foundation の Source Reader）。
#pragma once

#include <atomic>
#include <mutex>

#include "Writer.h"

namespace tv {

// カメラの一覧（名前と、開くときに使う目印）。capacity を超えた分は書かない。全部の数を返す
int ListCameras(TvCameraInfo* items, int capacity);

// Source Reader の読み出しの結果を受け取る（Source Reader を非同期で使う）。
// 同期の ReadSample は、待っている間にカメラの元を閉じても戻らなかった（ノートパソコンのカメラ・この PC の仮想カメラで確かめた）。
// 非同期にして、結果が届くのと、やめさせる合図のどちらか早い方を待つ。
class ReadCallback final : public IMFSourceReaderCallback {
public:
    struct Result {
        HRESULT status = S_OK;
        DWORD flags = 0;
        LONGLONG time = 0;
        ComPtr<IMFSample> sample;
    };

    ReadCallback();
    ~ReadCallback();

    ReadCallback(const ReadCallback&) = delete;
    ReadCallback& operator=(const ReadCallback&) = delete;

    STDMETHODIMP QueryInterface(REFIID riid, void** object) override;
    STDMETHODIMP_(ULONG) AddRef() override;
    STDMETHODIMP_(ULONG) Release() override;

    STDMETHODIMP OnReadSample(HRESULT status, DWORD streamIndex, DWORD flags, LONGLONG timestamp, IMFSample* sample) override;
    STDMETHODIMP OnFlush(DWORD streamIndex) override;
    STDMETHODIMP OnEvent(DWORD streamIndex, IMFMediaEvent* event) override;

    // 結果が届いたら合図になる（自動で戻る）
    HANDLE Ready() const noexcept { return ready_; }
    // 頼んだ読み出しを捨て終えたら合図になる（Flush の後）
    HANDLE Flushed() const noexcept { return flushed_; }
    // 届いた結果を受け取る
    Result Take();

private:
    std::atomic<ULONG> references_ = 1;
    HANDLE ready_ = nullptr;
    HANDLE flushed_ = nullptr;
    std::mutex lock_;
    Result result_;
};

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

    // 待っている読み出しをやめさせる（ほかのスレッドから呼んでよい。この後の読み出しは失敗する）
    void Cancel();

private:
    Capture();
    void UseRgbOutput();
    void CreateReader();
    // 読み手の設定を作る（BGRA への変換と、結果の受け取り先）
    ComPtr<IMFAttributes> ReaderAttributes();

    static constexpr uint32_t kMagic = 0x43415054; // "CAPT"
    uint32_t magic_ = kMagic;
    MediaStartup startup_;
    ComPtr<ReadCallback> callback_;
    HANDLE cancel_ = nullptr; // やめさせる合図（ずっと立ったまま）
    bool pending_ = false;    // 頼んだ読み出しの結果を、まだ受け取っていない
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
