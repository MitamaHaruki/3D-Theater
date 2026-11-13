// 3D Theater のアプリから、名前付きパイプで絵を受け取る。
// 読み書きは重ねた入出力（OVERLAPPED）で行い、時間切れと止める合図で待ちをほどく（アプリが止まっても固まらない）。
#include "FrameClient.h"

#include <cstdarg>
#include <cstdio>
#include <cstring>

namespace tvc {

void FillBlack(BYTE* nv12, uint32_t width, uint32_t height) noexcept
{
    const size_t luma = static_cast<size_t>(width) * height;
    std::memset(nv12, 16, luma);
    std::memset(nv12 + luma, 128, luma / 2);
}

void Trace(const wchar_t* format, ...) noexcept
{
    wchar_t text[512];
    va_list args;
    va_start(args, format);
    const int length = _vsnwprintf_s(text, _TRUNCATE, format, args);
    va_end(args);
    if (length != 0) {
        OutputDebugStringW(L"[3D Theater 仮想カメラ] ");
        OutputDebugStringW(text);
        OutputDebugStringW(L"\n");
    }
}

namespace {

constexpr DWORD kIoTimeout = 2000;      // 1 回の読み書きを待つ長さ（アプリは長くても 0.2 秒ほどで返す）
constexpr DWORD kRetryInterval = 1000;  // アプリにつながらないとき、次に試すまで
constexpr DWORD kNoFrameInterval = 50;  // 絵がまだ無いとき、次に頼むまで

class Handle {
public:
    explicit Handle(HANDLE handle = nullptr) noexcept : handle_(handle) {}
    ~Handle()
    {
        if (handle_ && handle_ != INVALID_HANDLE_VALUE) {
            CloseHandle(handle_);
        }
    }
    Handle(const Handle&) = delete;
    Handle& operator=(const Handle&) = delete;
    HANDLE Get() const noexcept { return handle_; }
    bool IsValid() const noexcept { return handle_ && handle_ != INVALID_HANDLE_VALUE; }

private:
    HANDLE handle_;
};

// アプリのパイプにつなぐ。アプリの側でなりすましの権限（成り代わり）を使えないよう、識別だけを許す。
// 書き込みは FILE_WRITE_DATA だけを頼む（GENERIC_WRITE はパイプを新しく作る権限まで求めてしまうため）
HANDLE Connect(DWORD waitMs) noexcept
{
    const DWORD access = GENERIC_READ | FILE_WRITE_DATA;
    const DWORD flags = FILE_FLAG_OVERLAPPED | SECURITY_SQOS_PRESENT | SECURITY_IDENTIFICATION;
    HANDLE pipe = CreateFileW(TVC_PIPE_NAME, access, 0, nullptr, OPEN_EXISTING, flags, nullptr);
    if (pipe == INVALID_HANDLE_VALUE && GetLastError() == ERROR_PIPE_BUSY && WaitNamedPipeW(TVC_PIPE_NAME, waitMs)) {
        pipe = CreateFileW(TVC_PIPE_NAME, access, 0, nullptr, OPEN_EXISTING, flags, nullptr);
    }
    return pipe;
}

// size バイトを全部読む（書く）。時間切れ・止める合図・切れたら false
bool Transfer(HANDLE pipe, bool write, void* data, DWORD size, HANDLE stop) noexcept
{
    Handle event(CreateEventW(nullptr, TRUE, FALSE, nullptr));
    if (!event.IsValid()) {
        return false;
    }
    auto* bytes = static_cast<BYTE*>(data);
    DWORD done = 0;
    while (done < size) {
        OVERLAPPED overlapped{};
        overlapped.hEvent = event.Get();
        ResetEvent(event.Get());
        const BOOL started = write ? WriteFile(pipe, bytes + done, size - done, nullptr, &overlapped)
                                   : ReadFile(pipe, bytes + done, size - done, nullptr, &overlapped);
        if (!started && GetLastError() != ERROR_IO_PENDING) {
            return false;
        }
        HANDLE waits[2] = { event.Get(), stop };
        const DWORD waited = WaitForMultipleObjects(stop ? 2 : 1, waits, FALSE, kIoTimeout);
        DWORD transferred = 0;
        if (waited != WAIT_OBJECT_0) {
            CancelIoEx(pipe, &overlapped);
            GetOverlappedResult(pipe, &overlapped, &transferred, TRUE);
            return false;
        }
        if (!GetOverlappedResult(pipe, &overlapped, &transferred, FALSE) || transferred == 0) {
            return false;
        }
        done += transferred;
    }
    return true;
}

bool IsValidHeader(const TvcFrameHeader& header) noexcept
{
    return header.magic == TVC_FRAME_MAGIC && header.version == TVC_PROTOCOL_VERSION;
}

} // namespace

FrameClient::FrameClient(uint32_t width, uint32_t height)
    : width_(width), height_(height), frameBytes_(Nv12Bytes(width, height)), latest_(Nv12Bytes(width, height))
{
    stop_ = CreateEventW(nullptr, TRUE, FALSE, nullptr);
}

FrameClient::~FrameClient()
{
    Stop();
    if (stop_) {
        CloseHandle(stop_);
    }
}

void FrameClient::Start()
{
    if (thread_.joinable() || !stop_) {
        return;
    }
    ResetEvent(stop_);
    thread_ = std::thread([this] { Run(); });
}

void FrameClient::Stop()
{
    if (!thread_.joinable()) {
        return;
    }
    SetEvent(stop_);
    thread_.join();
    std::lock_guard lock(mutex_);
    hasFrame_ = false;
}

bool FrameClient::CopyLatest(BYTE* destination, size_t size)
{
    std::lock_guard lock(mutex_);
    if (hasFrame_ && size == frameBytes_) {
        std::memcpy(destination, latest_.data(), frameBytes_);
        return true;
    }
    if (size == frameBytes_) {
        FillBlack(destination, width_, height_);
    }
    return false;
}

bool FrameClient::QuerySize(uint32_t& width, uint32_t& height, DWORD timeoutMs)
{
    Handle pipe(Connect(timeoutMs));
    if (!pipe.IsValid()) {
        return false;
    }
    TvcRequest request{ TVC_REQUEST_MAGIC, TVC_PROTOCOL_VERSION, 0, 0, 0 };
    TvcFrameHeader header{};
    if (!Transfer(pipe.Get(), true, &request, sizeof(request), nullptr)
        || !Transfer(pipe.Get(), false, &header, sizeof(header), nullptr)
        || !IsValidHeader(header) || !IsValidSize(header.width, header.height)) {
        return false;
    }
    width = header.width;
    height = header.height;
    return true;
}

void FrameClient::Run()
{
    std::vector<BYTE> incoming(frameBytes_);
    uint64_t last = 0;
    bool reported = false;
    while (WaitForSingleObject(stop_, 0) != WAIT_OBJECT_0) {
        Handle pipe(Connect(200));
        if (!pipe.IsValid()) {
            if (!reported) {
                Trace(L"アプリにつながりません（%lu）。つながるまで試します", GetLastError());
                reported = true;
            }
            if (WaitForSingleObject(stop_, kRetryInterval) == WAIT_OBJECT_0) {
                break;
            }
            continue;
        }
        Trace(L"アプリにつながりました（%u × %u の絵を頼みます）", width_, height_);
        reported = false;
        while (WaitForSingleObject(stop_, 0) != WAIT_OBJECT_0) {
            TvcRequest request{ TVC_REQUEST_MAGIC, TVC_PROTOCOL_VERSION, width_, height_, last };
            TvcFrameHeader header{};
            if (!Transfer(pipe.Get(), true, &request, sizeof(request), stop_)
                || !Transfer(pipe.Get(), false, &header, sizeof(header), stop_) || !IsValidHeader(header)) {
                break;
            }
            if (header.payload_size == 0) {
                if (WaitForSingleObject(stop_, kNoFrameInterval) == WAIT_OBJECT_0) {
                    break;
                }
                continue;
            }
            // 頼んだ大きさでない絵は、決まりと違うので、つなぎ直す
            if (header.width != width_ || header.height != height_ || header.payload_size != frameBytes_
                || !Transfer(pipe.Get(), false, incoming.data(), static_cast<DWORD>(frameBytes_), stop_)) {
                break;
            }
            {
                std::lock_guard lock(mutex_);
                latest_.swap(incoming);
                hasFrame_ = true;
            }
            last = header.sequence;
        }
        // 切れたら、止まった絵を出し続けず黒にする
        std::lock_guard lock(mutex_);
        hasFrame_ = false;
    }
}

} // namespace tvc
