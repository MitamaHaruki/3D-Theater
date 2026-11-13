// カメラと動画のファイルから映像を読む。読み出しは呼んだスレッドで待つ（同期）。作ったスレッドで読み、壊すこと。
#include "Capture.h"

#include <mfapi.h>
#include <mferror.h>

#include <algorithm>
#include <cmath>
#include <format>
#include <memory>

namespace tv {

namespace {

struct ActivateList {
    IMFActivate** items = nullptr;
    UINT32 count = 0;
    ~ActivateList()
    {
        for (UINT32 i = 0; i < count; ++i) {
            if (items[i]) {
                items[i]->Release();
            }
        }
        CoTaskMemFree(items);
    }
};

std::wstring GetString(IMFActivate* activate, const GUID& key)
{
    WCHAR* text = nullptr;
    UINT32 length = 0;
    if (FAILED(activate->GetAllocatedString(key, &text, &length)) || !text) {
        return {};
    }
    std::wstring result(text, length);
    CoTaskMemFree(text);
    return result;
}

void EnumerateCameras(ActivateList& list)
{
    ComPtr<IMFAttributes> attributes;
    CheckHr(MFCreateAttributes(&attributes, 1), L"カメラを探す設定の作成");
    CheckHr(attributes->SetGUID(MF_DEVSOURCE_ATTRIBUTE_SOURCE_TYPE, MF_DEVSOURCE_ATTRIBUTE_SOURCE_TYPE_VIDCAP_GUID), L"カメラを探す設定");
    CheckHr(MFEnumDeviceSources(attributes.Get(), &list.items, &list.count), L"カメラの一覧の取得");
}

const DWORD kVideo = static_cast<DWORD>(MF_SOURCE_READER_FIRST_VIDEO_STREAM);

} // namespace

int ListCameras(TvCameraInfo* items, int capacity)
{
    MediaStartup startup;
    ActivateList list;
    EnumerateCameras(list);
    for (UINT32 i = 0; i < list.count && static_cast<int>(i) < capacity; ++i) {
        TvCameraInfo& item = items[i];
        item.struct_size = sizeof(TvCameraInfo);
        wcsncpy_s(item.name, GetString(list.items[i], MF_DEVSOURCE_ATTRIBUTE_FRIENDLY_NAME).c_str(), _TRUNCATE);
        wcsncpy_s(item.id, GetString(list.items[i], MF_DEVSOURCE_ATTRIBUTE_SOURCE_TYPE_VIDCAP_SYMBOLIC_LINK).c_str(), _TRUNCATE);
    }
    return static_cast<int>(list.count);
}

Capture* Capture::OpenCamera(const wchar_t* id, int width, int height, int frameRate)
{
    auto capture = std::unique_ptr<Capture>(new Capture());
    ComPtr<IMFAttributes> attributes;
    CheckHr(MFCreateAttributes(&attributes, 2), L"カメラを開く設定の作成");
    CheckHr(attributes->SetGUID(MF_DEVSOURCE_ATTRIBUTE_SOURCE_TYPE, MF_DEVSOURCE_ATTRIBUTE_SOURCE_TYPE_VIDCAP_GUID), L"カメラを開く設定");
    CheckHr(attributes->SetString(MF_DEVSOURCE_ATTRIBUTE_SOURCE_TYPE_VIDCAP_SYMBOLIC_LINK, id), L"カメラを開く設定");
    CheckHr(MFCreateDeviceSource(attributes.Get(), &capture->source_), L"カメラを開く（ほかのアプリが使っていないか、カメラの使用が許可されているか確かめてください）");

    ComPtr<IMFAttributes> readerAttributes;
    CheckHr(MFCreateAttributes(&readerAttributes, 1), L"読み込みの設定の作成");
    CheckHr(readerAttributes->SetUINT32(MF_SOURCE_READER_ENABLE_VIDEO_PROCESSING, TRUE), L"読み込みの設定"); // BGRA へ変換させる
    CheckHr(MFCreateSourceReaderFromMediaSource(capture->source_.Get(), readerAttributes.Get(), &capture->reader_), L"カメラの読み手の作成");

    // カメラが出せる形のうち、大きさがいちばん近く、フレームの速さが足りるものを選ぶ
    ComPtr<IMFMediaType> best;
    double bestScore = 1e300;
    for (DWORD i = 0;; ++i) {
        ComPtr<IMFMediaType> type;
        if (FAILED(capture->reader_->GetNativeMediaType(kVideo, i, &type))) {
            break;
        }
        UINT32 w = 0;
        UINT32 h = 0;
        UINT32 num = 0;
        UINT32 den = 1;
        if (FAILED(MFGetAttributeSize(type.Get(), MF_MT_FRAME_SIZE, &w, &h)) || w == 0 || h == 0) {
            continue;
        }
        MFGetAttributeRatio(type.Get(), MF_MT_FRAME_RATE, &num, &den);
        const double fps = den == 0 ? 0.0 : static_cast<double>(num) / den;
        double score = std::abs(static_cast<double>(w) - width) + std::abs(static_cast<double>(h) - height);
        if (fps + 0.5 < frameRate) {
            score += 10000.0 + (frameRate - fps) * 100.0; // 遅い形は、ほかに無いときだけ
        }
        if (score < bestScore) {
            bestScore = score;
            best = type;
        }
    }
    if (best) {
        CheckHr(capture->reader_->SetCurrentMediaType(kVideo, nullptr, best.Get()), L"カメラの形の設定");
    }
    capture->UseRgbOutput();
    return capture.release();
}

Capture* Capture::OpenFile(const wchar_t* path, bool loop)
{
    auto capture = std::unique_ptr<Capture>(new Capture());
    capture->isFile_ = true;
    capture->loop_ = loop;
    ComPtr<IMFAttributes> attributes;
    CheckHr(MFCreateAttributes(&attributes, 1), L"読み込みの設定の作成");
    CheckHr(attributes->SetUINT32(MF_SOURCE_READER_ENABLE_VIDEO_PROCESSING, TRUE), L"読み込みの設定");
    CheckHr(MFCreateSourceReaderFromURL(path, attributes.Get(), &capture->reader_), L"動画のファイルを開く");
    capture->reader_->SetStreamSelection(static_cast<DWORD>(MF_SOURCE_READER_ALL_STREAMS), FALSE);
    capture->reader_->SetStreamSelection(kVideo, TRUE);
    capture->UseRgbOutput();
    return capture.release();
}

void Capture::UseRgbOutput()
{
    ComPtr<IMFMediaType> wanted;
    CheckHr(MFCreateMediaType(&wanted), L"形式の作成");
    CheckHr(wanted->SetGUID(MF_MT_MAJOR_TYPE, MFMediaType_Video), L"形式の設定");
    CheckHr(wanted->SetGUID(MF_MT_SUBTYPE, MFVideoFormat_RGB32), L"形式の設定");
    CheckHr(reader_->SetCurrentMediaType(kVideo, nullptr, wanted.Get()), L"映像の読み出しの形式（BGRA）の設定");
    ComPtr<IMFMediaType> current;
    CheckHr(reader_->GetCurrentMediaType(kVideo, &current), L"映像の形式の読み出し");
    CheckHr(MFGetAttributeSize(current.Get(), MF_MT_FRAME_SIZE, &width_, &height_), L"映像の大きさの読み出し");
    UINT32 num = 0;
    UINT32 den = 1;
    MFGetAttributeRatio(current.Get(), MF_MT_FRAME_RATE, &num, &den);
    frameRate_ = den == 0 ? 0.0 : static_cast<double>(num) / den;
    defaultStride_ = static_cast<LONG>(MFGetAttributeUINT32(current.Get(), MF_MT_DEFAULT_STRIDE, width_ * 4));
}

Capture::~Capture()
{
    magic_ = 0;
    reader_.Reset();
    if (source_) {
        source_->Shutdown();
        source_.Reset();
    }
}

void Capture::Cancel()
{
    cancelled_ = true;
    if (source_) {
        source_->Shutdown(); // 待っている ReadSample が MF_E_SHUTDOWN で戻る（Media Foundation のオブジェクトはどのスレッドからでも呼べる）
    }
}

void Capture::GetInfo(TvCaptureInfo& info) const
{
    info.width = width_;
    info.height = height_;
    info.frame_rate = frameRate_;
    info.is_file = isFile_ ? 1u : 0u;
}

bool Capture::Read(void* bgra, int stride, int bufferSize, int64_t& timestamp)
{
    const int rowBytes = static_cast<int>(width_) * 4;
    if (!bgra || stride < rowBytes || static_cast<int64_t>(stride) * height_ > bufferSize) {
        throw Error(TV_E_INVALID_ARG, L"フレームを書き込む場所が小さすぎます");
    }
    ComPtr<IMFSample> sample;
    for (int tries = 0; !sample; ++tries) {
        if (cancelled_) {
            throw Error(TV_E_INVALID_STATE, L"映像の読み込みをやめました");
        }
        if (tries > 1000) {
            throw Error(TV_E_MEDIA, L"映像のフレームが届きません");
        }
        DWORD streamIndex = 0;
        DWORD flags = 0;
        LONGLONG time = 0;
        CheckHr(reader_->ReadSample(kVideo, 0, &streamIndex, &flags, &time, &sample), L"映像の読み出し");
        if (flags & MF_SOURCE_READERF_ERROR) {
            throw Error(TV_E_MEDIA, L"映像の読み出しでエラーが起きました（カメラが外れた可能性があります）");
        }
        if (flags & MF_SOURCE_READERF_ENDOFSTREAM) {
            if (!loop_) {
                return false;
            }
            PROPVARIANT start;
            PropVariantInit(&start);
            start.vt = VT_I8;
            start.hVal.QuadPart = 0;
            const HRESULT hr = reader_->SetCurrentPosition(GUID_NULL, start);
            PropVariantClear(&start);
            CheckHr(hr, L"動画を先頭に戻す");
            sample.Reset();
            continue;
        }
        timestamp = time;
    }

    // 1 つ目のバッファーが 2 次元なら、上の行の場所と 1 行の長さ（下の行から並ぶときは負）がそのまま分かる。
    // 解凍した映像は、1 行を 16 の倍数などに揃えて持つことがあるので、幅 × 4 とは限らない
    ComPtr<IMFMediaBuffer> first;
    CheckHr(sample->GetBufferByIndex(0, &first), L"映像のバッファーの読み出し");
    ComPtr<IMF2DBuffer> buffer2d;
    if (SUCCEEDED(first.As(&buffer2d))) {
        BYTE* scanline0 = nullptr;
        LONG pitch = 0;
        CheckHr(buffer2d->Lock2D(&scanline0, &pitch), L"映像のバッファーのロック");
        const HRESULT copied = MFCopyImage(static_cast<BYTE*>(bgra), stride, scanline0, pitch, static_cast<DWORD>(rowBytes), height_);
        buffer2d->Unlock2D();
        CheckHr(copied, L"映像のフレームの写し");
        return true;
    }
    ComPtr<IMFMediaBuffer> buffer;
    CheckHr(sample->ConvertToContiguousBuffer(&buffer), L"映像のバッファーの読み出し");
    BYTE* data = nullptr;
    DWORD length = 0;
    CheckHr(buffer->Lock(&data, nullptr, &length), L"映像のバッファーのロック");
    // 1 行の長さは、バッファーの大きさ ÷ 高さ（揃えた分を含む）。符号（負なら下の行から並ぶ）は形式の決まりに合わせる
    LONG pitch = defaultStride_ == 0 ? rowBytes : defaultStride_;
    if (height_ > 0 && length % height_ == 0 && static_cast<LONG>(length / height_) >= rowBytes) {
        const LONG actual = static_cast<LONG>(length / height_);
        pitch = pitch < 0 ? -actual : actual;
    }
    const BYTE* top = pitch < 0 ? data + static_cast<size_t>(-pitch) * (height_ - 1) : data;
    const HRESULT copied = (static_cast<int64_t>(std::abs(pitch)) * height_ <= length)
        ? MFCopyImage(static_cast<BYTE*>(bgra), stride, top, pitch, static_cast<DWORD>(rowBytes), height_)
        : E_UNEXPECTED;
    buffer->Unlock();
    CheckHr(copied, L"映像のフレームの写し");
    return true;
}

} // namespace tv
