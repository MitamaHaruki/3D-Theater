// 仮想カメラの元（メディアソース）。Frame Server が Start・Stop・Shutdown を呼び、流れ（MediaStream）に絵を頼む。
#include "MediaSource.h"

#include <propvarutil.h>

namespace tvc {

namespace {

constexpr DWORD kSizeQueryTimeout = 500; // アプリへ大きさを聞くのを待つ長さ

// 「いつもの形（Legacy）」のカメラの使い方だけを持つ（Windows 11 の仮想カメラの決まり）
HRESULT AddSensorProfiles(IMFAttributes* attributes)
{
    ComPtr<IMFSensorProfileCollection> collection;
    TVC_RETURN_IF_FAILED(MFCreateSensorProfileCollection(&collection));
    ComPtr<IMFSensorProfile> legacy;
    TVC_RETURN_IF_FAILED(MFCreateSensorProfile(KSCAMERAPROFILE_Legacy, 0, nullptr, &legacy));
    TVC_RETURN_IF_FAILED(legacy->AddProfileFilter(0, L"((RES==;FRT<=30,1;SUT==))"));
    TVC_RETURN_IF_FAILED(collection->AddProfile(legacy.Get()));
    return attributes->SetUnknown(MF_DEVICEMFT_SENSORPROFILE_COLLECTION, collection.Get());
}

} // namespace

MediaSource::~MediaSource()
{
    Shutdown();
}

HRESULT MediaSource::RuntimeClassInitialize(IMFAttributes* activateAttributes)
{
    TVC_RETURN_IF_FAILED(MFCreateAttributes(&attributes_, 4));
    if (activateAttributes) {
        TVC_RETURN_IF_FAILED(activateAttributes->CopyAllItems(attributes_.Get()));
    }
    TVC_RETURN_IF_FAILED(AddSensorProfiles(attributes_.Get()));

    uint32_t width = TVC_DEFAULT_WIDTH;
    uint32_t height = TVC_DEFAULT_HEIGHT;
    if (FrameClient::QuerySize(width, height, kSizeQueryTimeout)) {
        Trace(L"アプリの絵の大きさ: %u × %u", width, height);
    } else {
        width = TVC_DEFAULT_WIDTH;
        height = TVC_DEFAULT_HEIGHT;
        Trace(L"アプリに大きさを聞けなかったため、%u × %u にします", width, height);
    }

    TVC_RETURN_IF_FAILED(Microsoft::WRL::MakeAndInitialize<MediaStream>(&stream_, static_cast<IMFMediaSource*>(this), width, height));
    ComPtr<IMFStreamDescriptor> streamDescriptor;
    TVC_RETURN_IF_FAILED(stream_->GetStreamDescriptor(&streamDescriptor));
    IMFStreamDescriptor* descriptors[] = { streamDescriptor.Get() };
    TVC_RETURN_IF_FAILED(MFCreatePresentationDescriptor(1, descriptors, &presentation_));
    TVC_RETURN_IF_FAILED(presentation_->SelectStream(0));
    return MFCreateEventQueue(&queue_);
}

STDMETHODIMP MediaSource::GetEvent(DWORD flags, IMFMediaEvent** event)
{
    ComPtr<IMFMediaEventQueue> queue;
    {
        std::lock_guard lock(mutex_);
        if (shutdown_) {
            return MF_E_SHUTDOWN;
        }
        queue = queue_;
    }
    return queue->GetEvent(flags, event);
}

STDMETHODIMP MediaSource::BeginGetEvent(IMFAsyncCallback* callback, IUnknown* state)
{
    std::lock_guard lock(mutex_);
    return shutdown_ ? MF_E_SHUTDOWN : queue_->BeginGetEvent(callback, state);
}

STDMETHODIMP MediaSource::EndGetEvent(IMFAsyncResult* result, IMFMediaEvent** event)
{
    std::lock_guard lock(mutex_);
    return shutdown_ ? MF_E_SHUTDOWN : queue_->EndGetEvent(result, event);
}

STDMETHODIMP MediaSource::QueueEvent(MediaEventType type, REFGUID extendedType, HRESULT status, const PROPVARIANT* eventValue)
{
    std::lock_guard lock(mutex_);
    return shutdown_ ? MF_E_SHUTDOWN : queue_->QueueEventParamVar(type, extendedType, status, eventValue);
}

STDMETHODIMP MediaSource::GetCharacteristics(DWORD* characteristics)
{
    if (!characteristics) {
        return E_POINTER;
    }
    std::lock_guard lock(mutex_);
    if (shutdown_) {
        return MF_E_SHUTDOWN;
    }
    *characteristics = MFMEDIASOURCE_IS_LIVE;
    return S_OK;
}

STDMETHODIMP MediaSource::CreatePresentationDescriptor(IMFPresentationDescriptor** descriptor)
{
    if (!descriptor) {
        return E_POINTER;
    }
    std::lock_guard lock(mutex_);
    if (shutdown_) {
        return MF_E_SHUTDOWN;
    }
    return presentation_->Clone(descriptor);
}

STDMETHODIMP MediaSource::Start(IMFPresentationDescriptor* descriptor, const GUID* timeFormat, const PROPVARIANT* startPosition)
{
    if (!descriptor) {
        return E_POINTER;
    }
    if (timeFormat && *timeFormat != GUID_NULL) {
        return MF_E_UNSUPPORTED_TIME_FORMAT;
    }
    ComPtr<MediaStream> stream;
    ComPtr<IMFMediaEventQueue> queue;
    {
        std::lock_guard lock(mutex_);
        if (shutdown_) {
            return MF_E_SHUTDOWN;
        }
        stream = stream_;
        queue = queue_;
    }
    DWORD count = 0;
    TVC_RETURN_IF_FAILED(descriptor->GetStreamDescriptorCount(&count));
    if (count != 1) {
        return E_INVALIDARG;
    }
    BOOL selected = FALSE;
    ComPtr<IMFStreamDescriptor> streamDescriptor;
    TVC_RETURN_IF_FAILED(descriptor->GetStreamDescriptorByIndex(0, &selected, &streamDescriptor));

    // 始める時刻（渡されなければ今）
    PROPVARIANT time;
    PropVariantInit(&time);
    if (startPosition && startPosition->vt != VT_EMPTY) {
        TVC_RETURN_IF_FAILED(PropVariantCopy(&time, startPosition));
    } else {
        time.vt = VT_I8;
        time.hVal.QuadPart = MFGetSystemTime();
    }

    HRESULT hr = S_OK;
    if (selected) {
        const bool wasRunning = stream->IsRunning();
        hr = queue->QueueEventParamUnk(wasRunning ? MEUpdatedStream : MENewStream, GUID_NULL, S_OK,
            static_cast<IMFMediaStream*>(stream.Get()));
        if (SUCCEEDED(hr)) {
            hr = stream->Start(&time);
        }
    } else {
        stream->Stop();
    }
    if (SUCCEEDED(hr)) {
        hr = queue->QueueEventParamVar(MESourceStarted, GUID_NULL, S_OK, &time);
    }
    PropVariantClear(&time);
    return hr;
}

STDMETHODIMP MediaSource::Stop()
{
    ComPtr<MediaStream> stream;
    ComPtr<IMFMediaEventQueue> queue;
    {
        std::lock_guard lock(mutex_);
        if (shutdown_) {
            return MF_E_SHUTDOWN;
        }
        stream = stream_;
        queue = queue_;
    }
    stream->Stop();
    return queue->QueueEventParamVar(MESourceStopped, GUID_NULL, S_OK, nullptr);
}

STDMETHODIMP MediaSource::Pause()
{
    return MF_E_INVALID_STATE_TRANSITION; // 生の映像なので止めるだけ
}

STDMETHODIMP MediaSource::Shutdown()
{
    ComPtr<MediaStream> stream;
    ComPtr<IMFMediaEventQueue> queue;
    {
        std::lock_guard lock(mutex_);
        if (shutdown_) {
            return MF_E_SHUTDOWN;
        }
        shutdown_ = true;
        stream = std::move(stream_);
        queue = std::move(queue_);
    }
    if (stream) {
        stream->Shutdown(); // 流れが持っている親（このソース）を離す
    }
    if (queue) {
        queue->Shutdown();
    }
    return S_OK;
}

STDMETHODIMP MediaSource::GetSourceAttributes(IMFAttributes** attributes)
{
    if (!attributes) {
        return E_POINTER;
    }
    std::lock_guard lock(mutex_);
    return shutdown_ ? MF_E_SHUTDOWN : attributes_.CopyTo(attributes);
}

STDMETHODIMP MediaSource::GetStreamAttributes(DWORD streamIdentifier, IMFAttributes** attributes)
{
    if (!attributes) {
        return E_POINTER;
    }
    *attributes = nullptr;
    std::lock_guard lock(mutex_);
    if (shutdown_) {
        return MF_E_SHUTDOWN;
    }
    if (streamIdentifier != 0) {
        return MF_E_INVALIDSTREAMNUMBER;
    }
    return stream_.CopyTo(attributes);
}

STDMETHODIMP MediaSource::SetD3DManager(IUnknown*)
{
    return S_OK; // 絵はアプリから CPU のメモリで受け取るので、GPU は使わない
}

STDMETHODIMP MediaSource::SetMediaType(DWORD streamId, IMFMediaType* mediaType)
{
    ComPtr<MediaStream> stream;
    {
        std::lock_guard lock(mutex_);
        if (shutdown_) {
            return MF_E_SHUTDOWN;
        }
        stream = stream_;
    }
    if (streamId != 0) {
        return MF_E_INVALIDSTREAMNUMBER;
    }
    return stream->SetCurrentType(mediaType);
}

STDMETHODIMP MediaSource::GetService(REFGUID, REFIID, LPVOID* object)
{
    if (!object) {
        return E_POINTER;
    }
    *object = nullptr;
    return MF_E_UNSUPPORTED_SERVICE;
}

} // namespace tvc
