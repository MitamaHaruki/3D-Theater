// 3D Theater のアプリから、名前付きパイプで絵を受け取る（tvc_protocol.h の決まり）。
#pragma once

#include <atomic>
#include <mutex>
#include <thread>
#include <vector>

#include "Common.h"

namespace tvc {

class FrameClient {
public:
    FrameClient(uint32_t width, uint32_t height);
    ~FrameClient();

    FrameClient(const FrameClient&) = delete;
    FrameClient& operator=(const FrameClient&) = delete;

    // 受け取りを始める（別のスレッドで、アプリにつながるまで試し続ける）
    void Start();
    // 受け取りをやめる（スレッドの終わりを待つ）
    void Stop();

    // 最新の絵（NV12）を写す。まだ届いていなければ黒で塗って false
    bool CopyLatest(BYTE* destination, size_t size);

    // アプリの今の絵の大きさを聞く（アプリが動いていなければ false）
    static bool QuerySize(uint32_t& width, uint32_t& height, DWORD timeoutMs);

private:
    void Run();

    const uint32_t width_;
    const uint32_t height_;
    const size_t frameBytes_;
    HANDLE stop_ = nullptr; // 止めるときに立てる（待っている読み書きをほどく）
    std::thread thread_;
    std::mutex mutex_;
    std::vector<BYTE> latest_;
    bool hasFrame_ = false;
};

} // namespace tvc
