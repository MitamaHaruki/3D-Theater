#include "View.h"

#include <algorithm>
#include <cstring>
#include <mutex>

#include "Engine.h"

extern "C" IMAGE_DOS_HEADER __ImageBase;

namespace tt {

namespace {

constexpr wchar_t kViewportClassName[] = L"3DTheaterViewportWindow";

// Present で UI スレッドが止まらないよう、表示完了の合図を待てるスワップチェーンにする
constexpr UINT kSwapChainFlags = DXGI_SWAP_CHAIN_FLAG_FRAME_LATENCY_WAITABLE_OBJECT;

LRESULT CALLBACK ViewportWindowProc(HWND hwnd, UINT message, WPARAM wParam, LPARAM lParam)
{
    switch (message) {
    case WM_ERASEBKGND:
        return 1; // 背景を塗ると描画前に一瞬ちらつくため塗らない
    case WM_PAINT: {
        // 描画はアプリ側の描画ループが行う。ここでは再描画要求を消すだけ
        PAINTSTRUCT ps;
        BeginPaint(hwnd, &ps);
        EndPaint(hwnd, &ps);
        return 0;
    }
    default:
        return DefWindowProcW(hwnd, message, wParam, lParam);
    }
}

HINSTANCE ModuleInstance() noexcept
{
    return reinterpret_cast<HINSTANCE>(&__ImageBase);
}

void RegisterViewportClass()
{
    static std::once_flag once;
    static DWORD registerError = 0;
    std::call_once(once, [] {
        WNDCLASSEXW wc{};
        wc.cbSize = sizeof(wc);
        wc.style = CS_DBLCLKS;
        wc.lpfnWndProc = ViewportWindowProc;
        wc.hInstance = ModuleInstance();
        wc.hCursor = LoadCursorW(nullptr, IDC_ARROW);
        wc.lpszClassName = kViewportClassName;
        if (!RegisterClassExW(&wc) && GetLastError() != ERROR_CLASS_ALREADY_EXISTS) {
            registerError = GetLastError();
        }
    });
    if (registerError != 0) {
        ThrowHr(HRESULT_FROM_WIN32(registerError), L"描画用ウィンドウクラスの登録");
    }
}

} // namespace

View::View(Engine& engine, Kind kind, uint32_t flags) : engine_(engine), kind_(kind), flags_(flags)
{
    engine_.AddView();
}

View::~View()
{
    ReleaseSizeDependentResources();
    if (frameLatencyWaitable_) {
        CloseHandle(frameLatencyWaitable_);
        frameLatencyWaitable_ = nullptr;
    }
    swapChain_.Reset(); // ウィンドウより先にスワップチェーンを手放す
    if (hwnd_) {
        DestroyWindow(hwnd_);
        hwnd_ = nullptr;
    }
    engine_.RemoveView();
    magic_ = 0;
}

std::unique_ptr<View> View::CreateForWindow(Engine& engine, HWND parent, int width, int height)
{
    if (!IsWindow(parent)) {
        throw Error(TT_E_INVALID_ARG, L"親ウィンドウが正しくありません");
    }
    RegisterViewportClass();

    std::unique_ptr<View> view(new View(engine, Kind::Window, 0));
    const int initialWidth = std::max(width, 1);
    const int initialHeight = std::max(height, 1);
    view->hwnd_ = CreateWindowExW(0, kViewportClassName, L"", WS_CHILD | WS_VISIBLE | WS_CLIPSIBLINGS | WS_CLIPCHILDREN,
        0, 0, initialWidth, initialHeight, parent, nullptr, ModuleInstance(), nullptr);
    if (!view->hwnd_) {
        ThrowHr(HRESULT_FROM_WIN32(GetLastError()), L"描画用の子ウィンドウの作成");
    }

    DXGI_SWAP_CHAIN_DESC1 desc{};
    desc.Width = static_cast<UINT>(initialWidth);
    desc.Height = static_cast<UINT>(initialHeight);
    desc.Format = DXGI_FORMAT_B8G8R8A8_UNORM;
    desc.SampleDesc.Count = 1;
    desc.BufferUsage = DXGI_USAGE_RENDER_TARGET_OUTPUT;
    desc.BufferCount = 2;
    desc.Scaling = DXGI_SCALING_STRETCH;
    desc.SwapEffect = DXGI_SWAP_EFFECT_FLIP_DISCARD;
    desc.AlphaMode = DXGI_ALPHA_MODE_IGNORE;
    desc.Flags = kSwapChainFlags;
    ComPtr<IDXGISwapChain1> swapChain1;
    CheckHr(engine.Factory()->CreateSwapChainForHwnd(engine.Device(), view->hwnd_, &desc, nullptr, nullptr, &swapChain1),
        L"スワップチェーンの作成");
    CheckHr(swapChain1.As(&view->swapChain_), L"IDXGISwapChain2 の取得");
    CheckHr(view->swapChain_->SetMaximumFrameLatency(1), L"表示待ちの上限の設定");
    view->frameLatencyWaitable_ = view->swapChain_->GetFrameLatencyWaitableObject();
    engine.Factory()->MakeWindowAssociation(view->hwnd_, DXGI_MWA_NO_ALT_ENTER);

    view->width_ = width;
    view->height_ = height;
    if (width > 0 && height > 0) {
        if (width != initialWidth || height != initialHeight) {
            CheckHr(view->swapChain_->ResizeBuffers(
                        0, static_cast<UINT>(width), static_cast<UINT>(height), DXGI_FORMAT_UNKNOWN, kSwapChainFlags),
                L"スワップチェーンの大きさ変更");
        }
        view->CreateSizeDependentResources();
    }
    return view;
}

std::unique_ptr<View> View::CreateOffscreen(Engine& engine, int width, int height, uint32_t flags)
{
    std::unique_ptr<View> view(new View(engine, Kind::Offscreen, flags));
    if (flags & TT_VIEW_FLAG_SHARE_D3D9) {
        engine.D3D9Device(); // 共有できない環境ならここで分かる
    }
    view->width_ = width;
    view->height_ = height;
    view->CreateSizeDependentResources();
    return view;
}

void View::CreateSizeDependentResources()
{
    ID3D11Device* device = engine_.Device();

    if (kind_ == Kind::Window) {
        CheckHr(swapChain_->GetBuffer(0, IID_PPV_ARGS(&colorTexture_)), L"バックバッファーの取得");
    } else {
        D3D11_TEXTURE2D_DESC desc{};
        desc.Width = static_cast<UINT>(width_);
        desc.Height = static_cast<UINT>(height_);
        desc.MipLevels = 1;
        desc.ArraySize = 1;
        desc.Format = DXGI_FORMAT_B8G8R8A8_UNORM;
        desc.SampleDesc.Count = 1;
        desc.Usage = D3D11_USAGE_DEFAULT;
        desc.BindFlags = D3D11_BIND_RENDER_TARGET | D3D11_BIND_SHADER_RESOURCE;
        desc.MiscFlags = (flags_ & TT_VIEW_FLAG_SHARE_D3D9) ? D3D11_RESOURCE_MISC_SHARED : 0;
        CheckHr(device->CreateTexture2D(&desc, nullptr, &colorTexture_), L"描画先テクスチャの作成");

        if (flags_ & TT_VIEW_FLAG_SHARE_D3D9) {
            ComPtr<IDXGIResource> dxgiResource;
            CheckHr(colorTexture_.As(&dxgiResource), L"IDXGIResource の取得");
            HANDLE sharedHandle = nullptr;
            CheckHr(dxgiResource->GetSharedHandle(&sharedHandle), L"共有ハンドルの取得");
            CheckHr(engine_.D3D9Device()->CreateTexture(static_cast<UINT>(width_), static_cast<UINT>(height_), 1,
                        D3DUSAGE_RENDERTARGET, D3DFMT_A8R8G8B8, D3DPOOL_DEFAULT, &d3d9Texture_, &sharedHandle),
                L"Direct3D 9Ex 側の共有テクスチャを開く");
            CheckHr(d3d9Texture_->GetSurfaceLevel(0, &d3d9Surface_), L"共有面の取得");
        }
    }
    CheckHr(device->CreateRenderTargetView(colorTexture_.Get(), nullptr, &rtv_), L"描画先ビューの作成");

    D3D11_TEXTURE2D_DESC depthDesc{};
    depthDesc.Width = static_cast<UINT>(width_);
    depthDesc.Height = static_cast<UINT>(height_);
    depthDesc.MipLevels = 1;
    depthDesc.ArraySize = 1;
    depthDesc.Format = DXGI_FORMAT_D24_UNORM_S8_UINT;
    depthDesc.SampleDesc.Count = 1;
    depthDesc.Usage = D3D11_USAGE_DEFAULT;
    depthDesc.BindFlags = D3D11_BIND_DEPTH_STENCIL;
    CheckHr(device->CreateTexture2D(&depthDesc, nullptr, &depthTexture_), L"深度バッファーの作成");
    CheckHr(device->CreateDepthStencilView(depthTexture_.Get(), nullptr, &dsv_), L"深度ビューの作成");
}

void View::ReleaseSizeDependentResources() noexcept
{
    engine_.Context()->OMSetRenderTargets(0, nullptr, nullptr);
    rtv_.Reset();
    dsv_.Reset();
    depthTexture_.Reset();
    colorTexture_.Reset();
    d3d9Surface_.Reset();
    d3d9Texture_.Reset();
}

void View::Resize(int width, int height)
{
    if (width == width_ && height == height_) {
        return;
    }
    ReleaseSizeDependentResources();
    width_ = width;
    height_ = height;
    if (width_ == 0 || height_ == 0) {
        return; // 最小化など。描画は止める
    }
    if (kind_ == Kind::Window) {
        engine_.Context()->Flush();
        CheckHr(swapChain_->ResizeBuffers(
                    0, static_cast<UINT>(width_), static_cast<UINT>(height_), DXGI_FORMAT_UNKNOWN, kSwapChainFlags),
            L"スワップチェーンの大きさ変更");
    }
    CreateSizeDependentResources();
}

bool View::Render(const TtFrameParams& params)
{
    if (width_ == 0 || height_ == 0) {
        return true;
    }
    if (kind_ == Kind::Window && WaitForSingleObjectEx(frameLatencyWaitable_, 0, FALSE) != WAIT_OBJECT_0) {
        return false; // 前の表示がまだ終わっていない。待たずに次の機会に描く
    }

    engine_.DrawScene(rtv_.Get(), dsv_.Get(), width_, height_, params);

    if (kind_ == Kind::Window) {
        CheckHr(swapChain_->Present(0, 0), L"画面への表示 (Present)");
    }
    // 画面外ビューは待たない。WPF へ渡す前に、呼び出し側が全ビュー分まとめて tt_engine_wait_for_gpu で待つ
    return true;
}

void View::ReadPixels(void* buffer, int stride, int bufferSize)
{
    if (kind_ != Kind::Offscreen) {
        throw Error(TT_E_NOT_SUPPORTED, L"ウィンドウに直接出すビューからは内容を読み出せません");
    }
    if (!colorTexture_) {
        throw Error(TT_E_INVALID_STATE, L"大きさが 0 のため読み出せる内容がありません");
    }
    if (!buffer || stride < width_ * 4 || static_cast<int64_t>(bufferSize) < static_cast<int64_t>(stride) * height_) {
        throw Error(TT_E_INVALID_ARG, L"読み出し先の大きさが足りません");
    }

    D3D11_TEXTURE2D_DESC desc{};
    colorTexture_->GetDesc(&desc);
    desc.Usage = D3D11_USAGE_STAGING;
    desc.BindFlags = 0;
    desc.CPUAccessFlags = D3D11_CPU_ACCESS_READ;
    desc.MiscFlags = 0;
    ComPtr<ID3D11Texture2D> staging;
    CheckHr(engine_.Device()->CreateTexture2D(&desc, nullptr, &staging), L"読み出し用テクスチャの作成");

    ID3D11DeviceContext* ctx = engine_.Context();
    ctx->CopyResource(staging.Get(), colorTexture_.Get());
    D3D11_MAPPED_SUBRESOURCE mapped{};
    CheckHr(ctx->Map(staging.Get(), 0, D3D11_MAP_READ, 0, &mapped), L"読み出し用テクスチャのマップ");
    auto* dst = static_cast<uint8_t*>(buffer);
    const auto* src = static_cast<const uint8_t*>(mapped.pData);
    for (int y = 0; y < height_; ++y) {
        std::memcpy(dst + static_cast<size_t>(y) * stride, src + static_cast<size_t>(y) * mapped.RowPitch,
            static_cast<size_t>(width_) * 4);
    }
    ctx->Unmap(staging.Get(), 0);
}

} // namespace tt
