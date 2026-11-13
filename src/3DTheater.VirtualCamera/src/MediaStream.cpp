// 仮想カメラの映像の流れ。頼み（RequestSample）を受けたら、届けるスレッドが 1/30 秒ずつ空けて、
// アプリから受け取った最新の絵を 1 枚ずつ MEMediaSample で届ける（絵が無ければ黒）。
#include "MediaStream.h"

#include <chrono>

namespace tvc {

namespace {

// 流れの属性（Frame Server がカメラの流れとして扱うのに使う）
HRESULT SetStreamAttributes(IMFAttributes* attributes)
{
    TVC_RETURN_IF_FAILED(attributes->SetGUID(MF_DEVICESTREAM_STREAM_CATEGORY, PINNAME_VIDEO_CAPTURE));
    TVC_RETURN_IF_FAILED(attributes->SetUINT32(MF_DEVICESTREAM_STREAM_ID, 0));
    TVC_RETURN_IF_FAILED(attributes->SetUINT32(MF_DEVICESTREAM_FRAMESERVER_SHARED, 1));
    return attributes->SetUINT32(MF_DEVICESTREAM_ATTRIBUTE_FRAMESOURCE_TYPES, MFFrameSourceTypes_Color);
}

// NV12・決まった大きさ・1 秒に 30 枚・上の行から。明るさ 16〜235。色の決まりは高さで決める（tvc_protocol.h）
HRESULT CreateNv12Type(uint32_t width, uint32_t height, IMFMediaType** result)
{
    ComPtr<IMFMediaType> type;
    TVC_RETURN_IF_FAILED(MFCreateMediaType(&type));
    TVC_RETURN_IF_FAILED(type->SetGUID(MF_MT_MAJOR_TYPE, MFMediaType_Video));
    TVC_RETURN_IF_FAILED(type->SetGUID(MF_MT_SUBTYPE, MFVideoFormat_NV12));
    TVC_RETURN_IF_FAILED(MFSetAttributeSize(type.Get(), MF_MT_FRAME_SIZE, width, height));
    TVC_RETURN_IF_FAILED(MFSetAttributeRatio(type.Get(), MF_MT_FRAME_RATE, TVC_FRAME_RATE, 1));
    TVC_RETURN_IF_FAILED(MFSetAttributeRatio(type.Get(), MF_MT_PIXEL_ASPECT_RATIO, 1, 1));
    TVC_RETURN_IF_FAILED(type->SetUINT32(MF_MT_INTERLACE_MODE, MFVideoInterlace_Progressive));
    TVC_RETURN_IF_FAILED(type->SetUINT32(MF_MT_ALL_SAMPLES_INDEPENDENT, TRUE));
    TVC_RETURN_IF_FAILED(type->SetUINT32(MF_MT_FIXED_SIZE_SAMPLES, TRUE));
    TVC_RETURN_IF_FAILED(type->SetUINT32(MF_MT_SAMPLE_SIZE, static_cast<UINT32>(Nv12Bytes(width, height))));
    TVC_RETURN_IF_FAILED(type->SetUINT32(MF_MT_DEFAULT_STRIDE, width));
    const uint64_t bitrate = static_cast<uint64_t>(width) * height * 12 * TVC_FRAME_RATE;
    TVC_RETURN_IF_FAILED(type->SetUINT32(MF_MT_AVG_BITRATE, static_cast<UINT32>(bitrate > 0xFFFFFFFFull ? 0xFFFFFFFFull : bitrate)));
    TVC_RETURN_IF_FAILED(type->SetUINT32(MF_MT_YUV_MATRIX, height >= TVC_HD_MIN_HEIGHT ? MFVideoTransferMatrix_BT709 : MFVideoTransferMatrix_BT601));
    TVC_RETURN_IF_FAILED(type->SetUINT32(MF_MT_VIDEO_NOMINAL_RANGE, MFNominalRange_16_235));
    *result = type.Detach();
    return S_OK;
}

} // namespace

MediaStream::~MediaStream()
{
    Shutdown();
}

HRESULT MediaStream::RuntimeClassInitialize(IMFMediaSource* source, uint32_t width, uint32_t height)
{
    width_ = width;
    height_ = height;
    TVC_RETURN_IF_FAILED(MFCreateAttributes(&attributes_, 4));
    TVC_RETURN_IF_FAILED(SetStreamAttributes(attributes_.Get()));
    TVC_RETURN_IF_FAILED(MFCreateEventQueue(&queue_));

    ComPtr<IMFMediaType> type;
    TVC_RETURN_IF_FAILED(CreateNv12Type(width, height, &type));
    IMFMediaType* types[] = { type.Get() };
    TVC_RETURN_IF_FAILED(MFCreateStreamDescriptor(0, 1, types, &descriptor_));
    TVC_RETURN_IF_FAILED(SetStreamAttributes(descriptor_.Get()));
    ComPtr<IMFMediaTypeHandler> handler;
    TVC_RETURN_IF_FAILED(descriptor_->GetMediaTypeHandler(&handler));
    TVC_RETURN_IF_FAILED(handler->SetCurrentMediaType(type.Get()));

    source_ = source;
    client_ = std::make_unique<FrameClient>(width, height);
    return S_OK;
}

STDMETHODIMP MediaStream::GetEvent(DWORD flags, IMFMediaEvent** event)
{
    ComPtr<IMFMediaEventQueue> queue;
    {
        std::lock_guard lock(mutex_);
        if (shutdown_) {
            return MF_E_SHUTDOWN;
        }
        queue = queue_;
    }
    return queue->GetEvent(flags, event); // 待つことがあるので、鍵を持たずに呼ぶ
}

STDMETHODIMP MediaStream::BeginGetEvent(IMFAsyncCallback* callback, IUnknown* state)
{
    std::lock_guard lock(mutex_);
    return shutdown_ ? MF_E_SHUTDOWN : queue_->BeginGetEvent(callback, state);
}

STDMETHODIMP MediaStream::EndGetEvent(IMFAsyncResult* result, IMFMediaEvent** event)
{
    std::lock_guard lock(mutex_);
    return shutdown_ ? MF_E_SHUTDOWN : queue_->EndGetEvent(result, event);
}

STDMETHODIMP MediaStream::QueueEvent(MediaEventType type, REFGUID extendedType, HRESULT status, const PROPVARIANT* eventValue)
{
    std::lock_guard lock(mutex_);
    return shutdown_ ? MF_E_SHUTDOWN : queue_->QueueEventParamVar(type, extendedType, status, eventValue);
}

STDMETHODIMP MediaStream::GetMediaSource(IMFMediaSource** source)
{
    if (!source) {
        return E_POINTER;
    }
    std::lock_guard lock(mutex_);
    if (shutdown_ || !source_) {
        return MF_E_SHUTDOWN;
    }
    return source_.CopyTo(source);
}

STDMETHODIMP MediaStream::GetStreamDescriptor(IMFStreamDescriptor** descriptor)
{
    if (!descriptor) {
        return E_POINTER;
    }
    std::lock_guard lock(mutex_);
    return shutdown_ ? MF_E_SHUTDOWN : descriptor_.CopyTo(descriptor);
}

STDMETHODIMP MediaStream::RequestSample(IUnknown* token)
{
    std::lock_guard lock(mutex_);
    if (shutdown_) {
        return MF_E_SHUTDOWN;
    }
    if (state_ != MF_STREAM_STATE_RUNNING) {
        return MF_E_INVALIDREQUEST;
    }
    requests_.emplace_back(token);
    wake_.notify_all();
    return S_OK;
}

STDMETHODIMP MediaStream::SetStreamState(MF_STREAM_STATE state)
{
    std::unique_lock lock(mutex_);
    if (shutdown_) {
        return MF_E_SHUTDOWN;
    }
    switch (state) {
    case MF_STREAM_STATE_RUNNING:
        state_ = state;
        StartDelivery();
        return S_OK;
    case MF_STREAM_STATE_STOPPED:
    case MF_STREAM_STATE_PAUSED:
        state_ = state;
        StopDelivery(lock);
        return S_OK;
    default:
        return E_INVALIDARG;
    }
}

STDMETHODIMP MediaStream::GetStreamState(MF_STREAM_STATE* state)
{
    if (!state) {
        return E_POINTER;
    }
    std::lock_guard lock(mutex_);
    *state = state_;
    return shutdown_ ? MF_E_SHUTDOWN : S_OK;
}

HRESULT MediaStream::Start(const PROPVARIANT* startTime)
{
    std::lock_guard lock(mutex_);
    if (shutdown_) {
        return MF_E_SHUTDOWN;
    }
    state_ = MF_STREAM_STATE_RUNNING;
    StartDelivery();
    return queue_->QueueEventParamVar(MEStreamStarted, GUID_NULL, S_OK, startTime);
}

HRESULT MediaStream::Stop()
{
    std::unique_lock lock(mutex_);
    if (shutdown_) {
        return MF_E_SHUTDOWN;
    }
    state_ = MF_STREAM_STATE_STOPPED;
    StopDelivery(lock);
    return queue_->QueueEventParamVar(MEStreamStopped, GUID_NULL, S_OK, nullptr);
}

void MediaStream::Shutdown()
{
    std::unique_lock lock(mutex_);
    if (shutdown_) {
        return;
    }
    StopDelivery(lock);
    shutdown_ = true;
    state_ = MF_STREAM_STATE_STOPPED;
    if (queue_) {
        queue_->Shutdown();
    }
    source_.Reset();
}

bool MediaStream::IsRunning()
{
    std::lock_guard lock(mutex_);
    return !shutdown_ && state_ == MF_STREAM_STATE_RUNNING;
}

HRESULT MediaStream::GetCurrentType(IMFMediaType** type)
{
    ComPtr<IMFMediaTypeHandler> handler;
    TVC_RETURN_IF_FAILED(descriptor_->GetMediaTypeHandler(&handler));
    return handler->GetCurrentMediaType(type);
}

HRESULT MediaStream::SetCurrentType(IMFMediaType* type)
{
    if (!type) {
        return E_POINTER;
    }
    ComPtr<IMFMediaTypeHandler> handler;
    TVC_RETURN_IF_FAILED(descriptor_->GetMediaTypeHandler(&handler));
    ComPtr<IMFMediaType> ours;
    TVC_RETURN_IF_FAILED(handler->GetMediaTypeByIndex(0, &ours));
    DWORD flags = 0;
    type->IsEqual(ours.Get(), &flags);
    constexpr DWORD kNeeded = MF_MEDIATYPE_EQUAL_MAJOR_TYPES | MF_MEDIATYPE_EQUAL_FORMAT_TYPES | MF_MEDIATYPE_EQUAL_FORMAT_DATA;
    if ((flags & kNeeded) != kNeeded) {
        return MF_E_INVALIDMEDIATYPE; // 出せる形は 1 つだけ
    }
    return handler->SetCurrentMediaType(ours.Get());
}

void MediaStream::StartDelivery()
{
    if (worker_.joinable()) {
        return;
    }
    stopping_ = false;
    client_->Start();
    worker_ = std::thread([this] { Run(); });
}

void MediaStream::StopDelivery(std::unique_lock<std::mutex>& lock)
{
    if (!worker_.joinable()) {
        requests_.clear();
        return;
    }
    stopping_ = true;
    wake_.notify_all();
    std::thread worker = std::move(worker_);
    lock.unlock();
    worker.join();
    client_->Stop();
    lock.lock();
    stopping_ = false;
    requests_.clear();
}

void MediaStream::Run()
{
    LONGLONG next = 0;
    std::unique_lock lock(mutex_);
    while (true) {
        wake_.wait(lock, [this] { return stopping_ || !requests_.empty(); });
        if (stopping_) {
            return;
        }
        // 前に届けてから 1/30 秒たつまで待つ（頼みが続けて来ても、1 秒に 30 枚の速さにする）
        const LONGLONG now = MFGetSystemTime();
        if (next > now) {
            const auto wait = std::chrono::microseconds((next - now) / 10);
            if (wake_.wait_for(lock, wait, [this] { return stopping_; })) {
                return;
            }
        }
        if (requests_.empty()) {
            continue;
        }
        ComPtr<IUnknown> token = std::move(requests_.front());
        requests_.pop_front();
        lock.unlock();
        const HRESULT hr = Deliver(token.Get());
        lock.lock();
        if (FAILED(hr) && hr != MF_E_SHUTDOWN) {
            Trace(L"絵を届けられませんでした（0x%08X）", static_cast<unsigned>(hr));
        }
        const LONGLONG after = MFGetSystemTime();
        next = (next == 0 || after - next > 2 * kFrameDuration) ? after + kFrameDuration : next + kFrameDuration;
    }
}

HRESULT MediaStream::Deliver(IUnknown* token)
{
    const DWORD bytes = static_cast<DWORD>(Nv12Bytes(width_, height_));
    ComPtr<IMFMediaBuffer> buffer;
    TVC_RETURN_IF_FAILED(MFCreateMemoryBuffer(bytes, &buffer));
    BYTE* data = nullptr;
    TVC_RETURN_IF_FAILED(buffer->Lock(&data, nullptr, nullptr));
    client_->CopyLatest(data, bytes);
    buffer->Unlock();
    TVC_RETURN_IF_FAILED(buffer->SetCurrentLength(bytes));

    ComPtr<IMFSample> sample;
    TVC_RETURN_IF_FAILED(MFCreateSample(&sample));
    TVC_RETURN_IF_FAILED(sample->AddBuffer(buffer.Get()));
    TVC_RETURN_IF_FAILED(sample->SetSampleTime(MFGetSystemTime()));
    TVC_RETURN_IF_FAILED(sample->SetSampleDuration(kFrameDuration));
    if (token) {
        TVC_RETURN_IF_FAILED(sample->SetUnknown(MFSampleExtension_Token, token));
    }
    ComPtr<IMFMediaEventQueue> queue;
    {
        std::lock_guard lock(mutex_);
        if (shutdown_) {
            return MF_E_SHUTDOWN;
        }
        queue = queue_;
    }
    return queue->QueueEventParamUnk(MEMediaSample, GUID_NULL, S_OK, sample.Get());
}

} // namespace tvc
