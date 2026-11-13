// 仮想カメラの映像の流れ（1 本）。頼まれた分だけ、1 秒に 30 枚の速さで絵（NV12）を届ける。
#pragma once

#include <condition_variable>
#include <deque>
#include <memory>
#include <mutex>
#include <thread>

#include "Common.h"
#include "FrameClient.h"

namespace tvc {

class MediaStream : public RuntimeClass<RuntimeClassFlags<ClassicCom>,
                        ChainInterfaces<IMFMediaStream2, IMFMediaStream, IMFMediaEventGenerator>, IMFAttributes, IKsControl> {
public:
    MediaStream() = default;
    ~MediaStream() override;

    HRESULT RuntimeClassInitialize(IMFMediaSource* source, uint32_t width, uint32_t height);

    // IMFMediaEventGenerator
    STDMETHODIMP GetEvent(DWORD flags, IMFMediaEvent** event) override;
    STDMETHODIMP BeginGetEvent(IMFAsyncCallback* callback, IUnknown* state) override;
    STDMETHODIMP EndGetEvent(IMFAsyncResult* result, IMFMediaEvent** event) override;
    STDMETHODIMP QueueEvent(MediaEventType type, REFGUID extendedType, HRESULT status, const PROPVARIANT* eventValue) override;

    // IMFMediaStream
    STDMETHODIMP GetMediaSource(IMFMediaSource** source) override;
    STDMETHODIMP GetStreamDescriptor(IMFStreamDescriptor** descriptor) override;
    STDMETHODIMP RequestSample(IUnknown* token) override;

    // IMFMediaStream2
    STDMETHODIMP SetStreamState(MF_STREAM_STATE state) override;
    STDMETHODIMP GetStreamState(MF_STREAM_STATE* state) override;

    TVC_KSCONTROL_METHODS
    TVC_ATTRIBUTES_METHODS(attributes_)

    // メディアソースから呼ぶ
    HRESULT Start(const PROPVARIANT* startTime);
    HRESULT Stop();
    void Shutdown();
    bool IsRunning();
    HRESULT GetCurrentType(IMFMediaType** type);
    HRESULT SetCurrentType(IMFMediaType* type);

private:
    void StartDelivery();  // mutex_ を持って呼ぶ
    void StopDelivery(std::unique_lock<std::mutex>& lock);
    void Run();
    HRESULT Deliver(IUnknown* token);

    std::mutex mutex_;
    std::condition_variable wake_;
    ComPtr<IMFAttributes> attributes_;
    ComPtr<IMFMediaEventQueue> queue_;
    ComPtr<IMFStreamDescriptor> descriptor_;
    ComPtr<IMFMediaSource> source_; // 親（Shutdown で離して、互いに持ち合わないようにする）
    std::unique_ptr<FrameClient> client_;
    std::deque<ComPtr<IUnknown>> requests_; // 頼まれた分（印の無い頼みは nullptr）
    std::thread worker_;
    MF_STREAM_STATE state_ = MF_STREAM_STATE_STOPPED;
    bool stopping_ = false;
    bool shutdown_ = false;
    uint32_t width_ = 0;
    uint32_t height_ = 0;
};

} // namespace tvc
