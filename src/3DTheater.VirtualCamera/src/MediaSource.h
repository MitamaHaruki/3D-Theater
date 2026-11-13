// 仮想カメラの元（Media Foundation のメディアソース）。映像の流れを 1 本だけ持つ。
// 作るときにアプリへ今の絵の大きさを聞き、その大きさの NV12 を出す（アプリが動いていなければ 1920 × 1080）。
#pragma once

#include <mutex>

#include "Common.h"
#include "MediaStream.h"

namespace tvc {

class MediaSource : public RuntimeClass<RuntimeClassFlags<ClassicCom>,
                        ChainInterfaces<IMFMediaSource2, IMFMediaSourceEx, IMFMediaSource, IMFMediaEventGenerator>,
                        IMFAttributes, IMFGetService, IKsControl> {
public:
    MediaSource() = default;
    ~MediaSource() override;

    HRESULT RuntimeClassInitialize(IMFAttributes* activateAttributes);

    // IMFMediaEventGenerator
    STDMETHODIMP GetEvent(DWORD flags, IMFMediaEvent** event) override;
    STDMETHODIMP BeginGetEvent(IMFAsyncCallback* callback, IUnknown* state) override;
    STDMETHODIMP EndGetEvent(IMFAsyncResult* result, IMFMediaEvent** event) override;
    STDMETHODIMP QueueEvent(MediaEventType type, REFGUID extendedType, HRESULT status, const PROPVARIANT* eventValue) override;

    // IMFMediaSource
    STDMETHODIMP GetCharacteristics(DWORD* characteristics) override;
    STDMETHODIMP CreatePresentationDescriptor(IMFPresentationDescriptor** descriptor) override;
    STDMETHODIMP Start(IMFPresentationDescriptor* descriptor, const GUID* timeFormat, const PROPVARIANT* startPosition) override;
    STDMETHODIMP Stop() override;
    STDMETHODIMP Pause() override;
    STDMETHODIMP Shutdown() override;

    // IMFMediaSourceEx
    STDMETHODIMP GetSourceAttributes(IMFAttributes** attributes) override;
    STDMETHODIMP GetStreamAttributes(DWORD streamIdentifier, IMFAttributes** attributes) override;
    STDMETHODIMP SetD3DManager(IUnknown* manager) override;

    // IMFMediaSource2
    STDMETHODIMP SetMediaType(DWORD streamId, IMFMediaType* mediaType) override;

    // IMFGetService
    STDMETHODIMP GetService(REFGUID service, REFIID riid, LPVOID* object) override;

    TVC_KSCONTROL_METHODS
    TVC_ATTRIBUTES_METHODS(attributes_)

private:
    std::mutex mutex_;
    ComPtr<IMFAttributes> attributes_;
    ComPtr<IMFMediaEventQueue> queue_;
    ComPtr<IMFPresentationDescriptor> presentation_;
    ComPtr<MediaStream> stream_;
    bool shutdown_ = false;
};

} // namespace tvc
