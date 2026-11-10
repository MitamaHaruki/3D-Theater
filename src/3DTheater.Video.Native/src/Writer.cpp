#include "Writer.h"

#include <codecapi.h>
#include <mferror.h>
#include <objbase.h>

#include <algorithm>
#include <cstring>
#include <format>

namespace tv {

namespace {

constexpr LONGLONG kTicksPerSecond = 10'000'000; // Media Foundation の時刻は 100 ナノ秒単位

LONGLONG FrameTime(int64_t frame, UINT32 rate)
{
    return static_cast<LONGLONG>(frame) * kTicksPerSecond / static_cast<LONGLONG>(rate);
}

// 色の決まり（BT.709・明るさ 16〜235）を形式に書く。符号化の側はこれを動画に書き込み（VUI）、再生する側はこれで色に戻す
void SetColorStandard(IMFMediaType* type)
{
    CheckHr(type->SetUINT32(MF_MT_VIDEO_NOMINAL_RANGE, MFNominalRange_16_235), L"映像の色の決まりの設定");
    CheckHr(type->SetUINT32(MF_MT_YUV_MATRIX, MFVideoTransferMatrix_BT709), L"映像の色の決まりの設定");
    CheckHr(type->SetUINT32(MF_MT_VIDEO_PRIMARIES, MFVideoPrimaries_BT709), L"映像の色の決まりの設定");
    CheckHr(type->SetUINT32(MF_MT_TRANSFER_FUNCTION, MFVideoTransFunc_709), L"映像の色の決まりの設定");
}

// BGRA（上の行から）を NV12（明るさの面のあとに、2 × 2 画素ごとの色差 U・V を交互に並べた面）にする。
// BT.709・明るさ 16〜235・色差 16〜240。Windows の変換に任せると、小さい動画では別の決まり（BT.601）で変換されるため自分で行う
void BgraToNv12(const BYTE* bgra, int stride, UINT32 width, UINT32 height, BYTE* nv12)
{
    // 係数は 65536 倍の整数（BT.709 の Kr = 0.2126、Kb = 0.0722 を、明るさ 219 段・色差 224 段に合わせたもの）
    constexpr int kYr = 11966, kYg = 40254, kYb = 4064;
    constexpr int kUr = -6596, kUg = -22189, kUb = 28784;
    constexpr int kVr = 28784, kVg = -26145, kVb = -2639;
    constexpr int kHalf = 1 << 15;
    BYTE* luma = nv12;
    BYTE* chroma = nv12 + static_cast<size_t>(width) * height;
    for (UINT32 y = 0; y < height; y += 2) {
        const BYTE* row0 = bgra + static_cast<size_t>(y) * stride;
        const BYTE* row1 = row0 + stride;
        BYTE* luma0 = luma + static_cast<size_t>(y) * width;
        BYTE* luma1 = luma0 + width;
        BYTE* uv = chroma + static_cast<size_t>(y / 2) * width;
        for (UINT32 x = 0; x < width; x += 2) {
            int sumB = 0, sumG = 0, sumR = 0;
            const BYTE* pixels[4] = { row0 + x * 4, row0 + x * 4 + 4, row1 + x * 4, row1 + x * 4 + 4 };
            BYTE* targets[4] = { luma0 + x, luma0 + x + 1, luma1 + x, luma1 + x + 1 };
            for (int i = 0; i < 4; ++i) {
                const int b = pixels[i][0], g = pixels[i][1], r = pixels[i][2];
                *targets[i] = static_cast<BYTE>(((kYr * r + kYg * g + kYb * b + kHalf) >> 16) + 16);
                sumB += b;
                sumG += g;
                sumR += r;
            }
            // 4 画素の平均（合計を 4 で割る分は、シフトを 2 つ増やして行う）
            const int u = ((kUr * sumR + kUg * sumG + kUb * sumB + (kHalf << 2)) >> 18) + 128;
            const int v = ((kVr * sumR + kVg * sumG + kVb * sumB + (kHalf << 2)) >> 18) + 128;
            uv[x] = static_cast<BYTE>(std::clamp(u, 16, 240));
            uv[x + 1] = static_cast<BYTE>(std::clamp(v, 16, 240));
        }
    }
}

ComPtr<IMFMediaType> NewType(const GUID& major, const GUID& subtype)
{
    ComPtr<IMFMediaType> type;
    CheckHr(MFCreateMediaType(&type), L"形式の作成");
    CheckHr(type->SetGUID(MF_MT_MAJOR_TYPE, major), L"形式の設定");
    CheckHr(type->SetGUID(MF_MT_SUBTYPE, subtype), L"形式の設定");
    return type;
}

void SetVideoShape(IMFMediaType* type, UINT32 width, UINT32 height, UINT32 rate)
{
    CheckHr(type->SetUINT32(MF_MT_INTERLACE_MODE, MFVideoInterlace_Progressive), L"映像の形式の設定");
    CheckHr(MFSetAttributeSize(type, MF_MT_FRAME_SIZE, width, height), L"映像の大きさの設定");
    CheckHr(MFSetAttributeRatio(type, MF_MT_FRAME_RATE, rate, 1), L"映像のフレームの速さの設定");
    CheckHr(MFSetAttributeRatio(type, MF_MT_PIXEL_ASPECT_RATIO, 1, 1), L"映像の画素の縦横比の設定");
}

void SetAudioShape(IMFMediaType* type, UINT32 sampleRate, UINT32 channels)
{
    CheckHr(type->SetUINT32(MF_MT_AUDIO_BITS_PER_SAMPLE, 16), L"音声の形式の設定");
    CheckHr(type->SetUINT32(MF_MT_AUDIO_SAMPLES_PER_SECOND, sampleRate), L"音声の形式の設定");
    CheckHr(type->SetUINT32(MF_MT_AUDIO_NUM_CHANNELS, channels), L"音声の形式の設定");
}

} // namespace

void CheckHr(HRESULT hr, const wchar_t* what, TvResult result)
{
    if (FAILED(hr)) {
        throw Error(result, std::format(L"{}に失敗しました（0x{:08X}）", what, static_cast<uint32_t>(hr)));
    }
}

MediaStartup::MediaStartup()
{
    // 呼び出したスレッドが STA でも Media Foundation は使えるので、MTA にできなくても続ける
    const HRESULT com = CoInitializeEx(nullptr, COINIT_MULTITHREADED);
    uninitializeCom_ = SUCCEEDED(com);
    if (FAILED(com) && com != RPC_E_CHANGED_MODE) {
        CheckHr(com, L"COM の初期化");
    }
    const HRESULT media = MFStartup(MF_VERSION, MFSTARTUP_FULL);
    if (FAILED(media)) {
        if (uninitializeCom_) {
            CoUninitialize();
        }
        CheckHr(media, L"Media Foundation の開始");
    }
    shutdownMedia_ = true;
}

MediaStartup::~MediaStartup()
{
    if (shutdownMedia_) {
        MFShutdown();
    }
    if (uninitializeCom_) {
        CoUninitialize();
    }
}

Writer::Writer(const TvWriterDesc& desc)
    : width_(desc.width), height_(desc.height), frameRate_(desc.frame_rate), sampleRate_(desc.audio_sample_rate),
      channels_(desc.audio_channels)
{
    hasAudio_ = desc.audio_sample_rate != 0;

    ComPtr<IMFAttributes> attributes;
    CheckHr(MFCreateAttributes(&attributes, 2), L"書き出しの設定の作成");
    // GPU の符号化があれば使う。書く速さを再生の速さに合わせない（できるだけ速く書く）
    CheckHr(attributes->SetUINT32(MF_READWRITE_ENABLE_HARDWARE_TRANSFORMS, TRUE), L"書き出しの設定");
    CheckHr(attributes->SetUINT32(MF_SINK_WRITER_DISABLE_THROTTLING, TRUE), L"書き出しの設定");
    CheckHr(MFCreateSinkWriterFromURL(desc.path, nullptr, attributes.Get(), &writer_), L"動画のファイルの作成");

    // 映像: H.264（High）へ。渡すのは NV12（BGRA から自分で変換したもの・上の行から）。色の決まりは BT.709
    ComPtr<IMFMediaType> videoOut = NewType(MFMediaType_Video, MFVideoFormat_H264);
    CheckHr(videoOut->SetUINT32(MF_MT_AVG_BITRATE, desc.video_bitrate), L"映像の形式の設定");
    CheckHr(videoOut->SetUINT32(MF_MT_MPEG2_PROFILE, eAVEncH264VProfile_High), L"映像の形式の設定");
    SetVideoShape(videoOut.Get(), width_, height_, frameRate_);
    SetColorStandard(videoOut.Get());
    CheckHr(writer_->AddStream(videoOut.Get(), &videoStream_), L"映像の入れ物の作成", TV_E_NOT_SUPPORTED);

    ComPtr<IMFMediaType> videoIn = NewType(MFMediaType_Video, MFVideoFormat_NV12);
    SetVideoShape(videoIn.Get(), width_, height_, frameRate_);
    SetColorStandard(videoIn.Get());
    CheckHr(videoIn->SetUINT32(MF_MT_DEFAULT_STRIDE, width_), L"映像の形式の設定");
    CheckHr(writer_->SetInputMediaType(videoStream_, videoIn.Get(), nullptr),
        L"映像の符号化の準備（この大きさ・フレームの速さに対応していない可能性があります）", TV_E_NOT_SUPPORTED);

    if (hasAudio_) {
        // 音声: AAC へ。渡すのは 16 ビットの PCM
        ComPtr<IMFMediaType> audioOut = NewType(MFMediaType_Audio, MFAudioFormat_AAC);
        SetAudioShape(audioOut.Get(), sampleRate_, channels_);
        CheckHr(audioOut->SetUINT32(MF_MT_AUDIO_AVG_BYTES_PER_SECOND, desc.audio_bitrate / 8), L"音声の形式の設定");
        CheckHr(writer_->AddStream(audioOut.Get(), &audioStream_), L"音声の入れ物の作成", TV_E_NOT_SUPPORTED);

        ComPtr<IMFMediaType> audioIn = NewType(MFMediaType_Audio, MFAudioFormat_PCM);
        SetAudioShape(audioIn.Get(), sampleRate_, channels_);
        CheckHr(audioIn->SetUINT32(MF_MT_AUDIO_BLOCK_ALIGNMENT, channels_ * 2), L"音声の形式の設定");
        CheckHr(audioIn->SetUINT32(MF_MT_AUDIO_AVG_BYTES_PER_SECOND, sampleRate_ * channels_ * 2), L"音声の形式の設定");
        CheckHr(audioIn->SetUINT32(MF_MT_ALL_SAMPLES_INDEPENDENT, TRUE), L"音声の形式の設定");
        CheckHr(writer_->SetInputMediaType(audioStream_, audioIn.Get(), nullptr), L"音声の符号化の準備", TV_E_NOT_SUPPORTED);
    }

    CheckHr(writer_->BeginWriting(), L"書き出しの開始");
}

Writer::~Writer()
{
    writer_.Reset(); // Media Foundation を終える前に手放す
    magic_ = 0;
}

void Writer::WriteVideo(const void* bgra, int stride, int64_t frameIndex)
{
    if (finished_) {
        throw Error(TV_E_INVALID_STATE, L"書き終えた後に映像を書こうとしました");
    }
    const int rowBytes = static_cast<int>(width_) * 4;
    if (!bgra || stride < rowBytes || frameIndex < 0) {
        throw Error(TV_E_INVALID_ARG, L"映像のフレームが正しくありません");
    }
    const DWORD bytes = width_ * height_ * 3 / 2; // NV12: 明るさ（1 画素 1 バイト）と、その半分の色差
    ComPtr<IMFMediaBuffer> buffer;
    CheckHr(MFCreateMemoryBuffer(bytes, &buffer), L"映像のバッファーの作成", TV_E_OUT_OF_MEMORY);
    BYTE* destination = nullptr;
    CheckHr(buffer->Lock(&destination, nullptr, nullptr), L"映像のバッファーのロック");
    BgraToNv12(static_cast<const BYTE*>(bgra), stride, width_, height_, destination);
    buffer->Unlock();
    CheckHr(buffer->SetCurrentLength(bytes), L"映像のバッファーの設定");

    ComPtr<IMFSample> sample;
    CheckHr(MFCreateSample(&sample), L"映像の 1 枚の作成");
    CheckHr(sample->AddBuffer(buffer.Get()), L"映像の 1 枚の作成");
    const LONGLONG start = FrameTime(frameIndex, frameRate_);
    CheckHr(sample->SetSampleTime(start), L"映像の時刻の設定");
    CheckHr(sample->SetSampleDuration(FrameTime(frameIndex + 1, frameRate_) - start), L"映像の長さの設定");
    CheckHr(writer_->WriteSample(videoStream_, sample.Get()), L"映像の書き込み");
}

void Writer::WriteAudio(const int16_t* samples, int frameCount)
{
    if (!hasAudio_) {
        throw Error(TV_E_INVALID_STATE, L"音声なしで作った書き手に、音声を書こうとしました");
    }
    if (finished_) {
        throw Error(TV_E_INVALID_STATE, L"書き終えた後に音声を書こうとしました");
    }
    if (frameCount <= 0) {
        return;
    }
    if (!samples) {
        throw Error(TV_E_INVALID_ARG, L"音声が NULL です");
    }
    const DWORD bytes = static_cast<DWORD>(frameCount) * channels_ * 2;
    ComPtr<IMFMediaBuffer> buffer;
    CheckHr(MFCreateMemoryBuffer(bytes, &buffer), L"音声のバッファーの作成", TV_E_OUT_OF_MEMORY);
    BYTE* destination = nullptr;
    CheckHr(buffer->Lock(&destination, nullptr, nullptr), L"音声のバッファーのロック");
    std::memcpy(destination, samples, bytes);
    buffer->Unlock();
    CheckHr(buffer->SetCurrentLength(bytes), L"音声のバッファーの設定");

    ComPtr<IMFSample> sample;
    CheckHr(MFCreateSample(&sample), L"音声の 1 区切りの作成");
    CheckHr(sample->AddBuffer(buffer.Get()), L"音声の 1 区切りの作成");
    const LONGLONG start = FrameTime(audioFrames_, sampleRate_);
    audioFrames_ += frameCount;
    CheckHr(sample->SetSampleTime(start), L"音声の時刻の設定");
    CheckHr(sample->SetSampleDuration(FrameTime(audioFrames_, sampleRate_) - start), L"音声の長さの設定");
    CheckHr(writer_->WriteSample(audioStream_, sample.Get()), L"音声の書き込み");
}

void Writer::Finish()
{
    if (finished_) {
        return;
    }
    finished_ = true;
    CheckHr(writer_->Finalize(), L"動画のファイルの仕上げ");
}

void Probe(const wchar_t* path, TvProbeInfo& info, void* firstFrame, int stride, int bufferSize)
{
    MediaStartup startup;
    ComPtr<IMFAttributes> attributes;
    CheckHr(MFCreateAttributes(&attributes, 1), L"読み込みの設定の作成");
    CheckHr(attributes->SetUINT32(MF_SOURCE_READER_ENABLE_VIDEO_PROCESSING, TRUE), L"読み込みの設定"); // BGRA へ変換させる
    ComPtr<IMFSourceReader> reader;
    CheckHr(MFCreateSourceReaderFromURL(path, attributes.Get(), &reader), L"動画のファイルを開く");

    const DWORD video = static_cast<DWORD>(MF_SOURCE_READER_FIRST_VIDEO_STREAM);
    ComPtr<IMFMediaType> wanted = NewType(MFMediaType_Video, MFVideoFormat_RGB32);
    CheckHr(reader->SetCurrentMediaType(video, nullptr, wanted.Get()), L"映像の読み出しの形式の設定");
    ComPtr<IMFMediaType> current;
    CheckHr(reader->GetCurrentMediaType(video, &current), L"映像の形式の読み出し");
    UINT32 width = 0;
    UINT32 height = 0;
    UINT32 numerator = 0;
    UINT32 denominator = 1;
    CheckHr(MFGetAttributeSize(current.Get(), MF_MT_FRAME_SIZE, &width, &height), L"映像の大きさの読み出し");
    MFGetAttributeRatio(current.Get(), MF_MT_FRAME_RATE, &numerator, &denominator);
    info.width = width;
    info.height = height;
    info.frame_rate = denominator == 0 ? 0.0 : static_cast<double>(numerator) / denominator;

    PROPVARIANT duration;
    PropVariantInit(&duration);
    if (SUCCEEDED(reader->GetPresentationAttribute(static_cast<DWORD>(MF_SOURCE_READER_MEDIASOURCE), MF_PD_DURATION, &duration))) {
        info.duration_seconds = static_cast<double>(duration.uhVal.QuadPart) / kTicksPerSecond;
    }
    PropVariantClear(&duration);

    ComPtr<IMFMediaType> audio;
    if (SUCCEEDED(reader->GetNativeMediaType(static_cast<DWORD>(MF_SOURCE_READER_FIRST_AUDIO_STREAM), 0, &audio))) {
        info.has_audio = 1;
        info.audio_sample_rate = MFGetAttributeUINT32(audio.Get(), MF_MT_AUDIO_SAMPLES_PER_SECOND, 0);
        info.audio_channels = MFGetAttributeUINT32(audio.Get(), MF_MT_AUDIO_NUM_CHANNELS, 0);
    }

    if (!firstFrame) {
        return;
    }
    const int rowBytes = static_cast<int>(width) * 4;
    if (stride < rowBytes || static_cast<int64_t>(stride) * height > bufferSize) {
        throw Error(TV_E_INVALID_ARG, L"最初のフレームを書き込む場所が小さすぎます");
    }
    ComPtr<IMFSample> sample;
    for (int tries = 0; tries < 100 && !sample; ++tries) {
        DWORD streamIndex = 0;
        DWORD flags = 0;
        LONGLONG timestamp = 0;
        CheckHr(reader->ReadSample(video, 0, &streamIndex, &flags, &timestamp, &sample), L"映像の読み出し");
        if (flags & MF_SOURCE_READERF_ENDOFSTREAM) {
            break;
        }
    }
    if (!sample) {
        throw Error(TV_E_MEDIA, L"映像のフレームがありません");
    }
    ComPtr<IMFMediaBuffer> buffer;
    CheckHr(sample->GetBufferByIndex(0, &buffer), L"映像のバッファーの読み出し");
    ComPtr<IMF2DBuffer> buffer2d;
    if (SUCCEEDED(buffer.As(&buffer2d))) {
        // 2 次元のバッファーなら、上の行の場所と 1 行の長さ（下の行から並ぶときは負）がそのまま分かる
        BYTE* scanline0 = nullptr;
        LONG pitch = 0;
        CheckHr(buffer2d->Lock2D(&scanline0, &pitch), L"映像のバッファーのロック");
        const HRESULT copied = MFCopyImage(static_cast<BYTE*>(firstFrame), stride, scanline0, pitch, static_cast<DWORD>(rowBytes), height);
        buffer2d->Unlock2D();
        CheckHr(copied, L"映像のフレームの写し");
        return;
    }
    LONG pitch = static_cast<LONG>(MFGetAttributeUINT32(current.Get(), MF_MT_DEFAULT_STRIDE, static_cast<UINT32>(rowBytes)));
    BYTE* data = nullptr;
    DWORD length = 0;
    CheckHr(buffer->Lock(&data, nullptr, &length), L"映像のバッファーのロック");
    const BYTE* top = pitch < 0 ? data + static_cast<size_t>(-pitch) * (height - 1) : data;
    const HRESULT copied = MFCopyImage(static_cast<BYTE*>(firstFrame), stride, top, pitch, static_cast<DWORD>(rowBytes), height);
    buffer->Unlock();
    CheckHr(copied, L"映像のフレームの写し");
}

} // namespace tv
