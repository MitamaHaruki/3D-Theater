#include "View.h"

#include <algorithm>
#include <cstring>
#include <mutex>

#include "Engine.h"
#include "Log.h"

extern "C" IMAGE_DOS_HEADER __ImageBase;

namespace tt {

namespace {

constexpr wchar_t kViewportClassName[] = L"3DTheaterViewportWindow";

// 抜けるときに必ず呼ぶ（例外で抜けても）
template <class F>
struct ScopeExit {
    F action;
    ~ScopeExit() { action(); }
};

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
    engine_.Register(*this);
}

View::~View()
{
    ReleaseSizeDependentResources();
    ReleaseSwapChain(); // ウィンドウより先にスワップチェーンを手放す
    if (hwnd_) {
        DestroyWindow(hwnd_);
        hwnd_ = nullptr;
    }
    engine_.Unregister(*this);
    magic_ = 0;
}

std::unique_ptr<View> View::CreateForWindow(Engine& engine, HWND parent, int width, int height)
{
    if (!IsWindow(parent)) {
        throw Error(TT_E_INVALID_ARG, L"親ウィンドウが正しくありません");
    }
    engine.ThrowIfDeviceLost();
    RegisterViewportClass();

    std::unique_ptr<View> view(new View(engine, Kind::Window, 0));
    const int initialWidth = std::max(width, 1);
    const int initialHeight = std::max(height, 1);
    view->hwnd_ = CreateWindowExW(0, kViewportClassName, L"", WS_CHILD | WS_VISIBLE | WS_CLIPSIBLINGS | WS_CLIPCHILDREN,
        0, 0, initialWidth, initialHeight, parent, nullptr, ModuleInstance(), nullptr);
    if (!view->hwnd_) {
        ThrowHr(HRESULT_FROM_WIN32(GetLastError()), L"描画用の子ウィンドウの作成");
    }

    view->width_ = width;
    view->height_ = height;
    view->CreateSwapChain();
    if (width > 0 && height > 0) {
        view->CreateSizeDependentResources();
    }
    return view;
}

void View::CreateSwapChain()
{
    DXGI_SWAP_CHAIN_DESC1 desc{};
    desc.Width = static_cast<UINT>(std::max(width_, 1));
    desc.Height = static_cast<UINT>(std::max(height_, 1));
    desc.Format = DXGI_FORMAT_B8G8R8A8_UNORM;
    desc.SampleDesc.Count = 1;
    desc.BufferUsage = DXGI_USAGE_RENDER_TARGET_OUTPUT;
    desc.BufferCount = 2;
    desc.Scaling = DXGI_SCALING_STRETCH;
    desc.SwapEffect = DXGI_SWAP_EFFECT_FLIP_DISCARD;
    desc.AlphaMode = DXGI_ALPHA_MODE_IGNORE;
    desc.Flags = kSwapChainFlags;
    ComPtr<IDXGISwapChain1> swapChain1;
    CheckHr(engine_.Factory()->CreateSwapChainForHwnd(engine_.Device(), hwnd_, &desc, nullptr, nullptr, &swapChain1),
        L"スワップチェーンの作成");
    CheckHr(swapChain1.As(&swapChain_), L"IDXGISwapChain2 の取得");
    CheckHr(swapChain_->SetMaximumFrameLatency(1), L"表示待ちの上限の設定");
    frameLatencyWaitable_ = swapChain_->GetFrameLatencyWaitableObject();
    engine_.Factory()->MakeWindowAssociation(hwnd_, DXGI_MWA_NO_ALT_ENTER);
}

void View::ReleaseSwapChain() noexcept
{
    if (frameLatencyWaitable_) {
        CloseHandle(frameLatencyWaitable_);
        frameLatencyWaitable_ = nullptr;
    }
    swapChain_.Reset();
}

std::unique_ptr<View> View::CreateOffscreen(Engine& engine, int width, int height, uint32_t flags)
{
    engine.ThrowIfDeviceLost();
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
        // 見える面を分けるときは、描く面は共有しない（共有するのは見える面）
        const bool deferred = (flags_ & TT_VIEW_FLAG_DEFERRED_PUBLISH) != 0;
        const bool shared = (flags_ & TT_VIEW_FLAG_SHARE_D3D9) != 0;
        D3D11_TEXTURE2D_DESC desc{};
        desc.Width = static_cast<UINT>(width_);
        desc.Height = static_cast<UINT>(height_);
        desc.MipLevels = 1;
        desc.ArraySize = 1;
        desc.Format = Engine::kColorFormat;
        desc.SampleDesc.Count = 1;
        desc.Usage = D3D11_USAGE_DEFAULT;
        desc.BindFlags = D3D11_BIND_RENDER_TARGET | D3D11_BIND_SHADER_RESOURCE;
        desc.MiscFlags = shared && !deferred ? D3D11_RESOURCE_MISC_SHARED : 0;
        CheckHr(device->CreateTexture2D(&desc, nullptr, &colorTexture_), L"描画先テクスチャの作成");
        hasRendered_ = false;
        if (deferred) {
            desc.MiscFlags = shared ? D3D11_RESOURCE_MISC_SHARED : 0;
            CheckHr(device->CreateTexture2D(&desc, nullptr, &publishedTexture_), L"見える面のテクスチャの作成");
            // 初めは透明な黒（まだ何も写していない）
            ComPtr<ID3D11RenderTargetView> clearView;
            CheckHr(device->CreateRenderTargetView(publishedTexture_.Get(), nullptr, &clearView), L"見える面のビューの作成");
            const float transparent[4] = {0.0f, 0.0f, 0.0f, 0.0f};
            engine_.Context()->ClearRenderTargetView(clearView.Get(), transparent);
        }

        if (shared) {
            ComPtr<IDXGIResource> dxgiResource;
            CheckHr((deferred ? publishedTexture_ : colorTexture_).As(&dxgiResource), L"IDXGIResource の取得");
            HANDLE sharedHandle = nullptr;
            CheckHr(dxgiResource->GetSharedHandle(&sharedHandle), L"共有ハンドルの取得");
            CheckHr(engine_.D3D9Device()->CreateTexture(static_cast<UINT>(width_), static_cast<UINT>(height_), 1,
                        D3DUSAGE_RENDERTARGET, D3DFMT_A8R8G8B8, D3DPOOL_DEFAULT, &d3d9Texture_, &sharedHandle),
                L"Direct3D 9Ex 側の共有テクスチャを開く");
            CheckHr(d3d9Texture_->GetSurfaceLevel(0, &d3d9Surface_), L"共有面の取得");
        }
    }
    CheckHr(device->CreateRenderTargetView(colorTexture_.Get(), nullptr, &rtv_), L"描画先ビューの作成");
    CreateSampleResources();
}

void View::CreateSampleResources()
{
    ID3D11Device* device = engine_.Device();
    if (sampleCount_ > 1) {
        D3D11_TEXTURE2D_DESC desc{};
        desc.Width = static_cast<UINT>(width_);
        desc.Height = static_cast<UINT>(height_);
        desc.MipLevels = 1;
        desc.ArraySize = 1;
        desc.Format = Engine::kColorFormat;
        desc.SampleDesc.Count = sampleCount_;
        desc.Usage = D3D11_USAGE_DEFAULT;
        desc.BindFlags = D3D11_BIND_RENDER_TARGET;
        CheckHr(device->CreateTexture2D(&desc, nullptr, &msaaTexture_), L"なめらかに描く描画先（MSAA）の作成");
        CheckHr(device->CreateRenderTargetView(msaaTexture_.Get(), nullptr, &msaaRtv_), L"なめらかに描く描画先ビューの作成");
    }

    // 奥行きは、描くときは深度バッファーとして使い、線画では画像として読む（形式を決めずに作り、ビューごとに決める）
    D3D11_TEXTURE2D_DESC depthDesc{};
    depthDesc.Width = static_cast<UINT>(width_);
    depthDesc.Height = static_cast<UINT>(height_);
    depthDesc.MipLevels = 1;
    depthDesc.ArraySize = 1;
    depthDesc.Format = DXGI_FORMAT_R24G8_TYPELESS;
    depthDesc.SampleDesc.Count = sampleCount_;
    depthDesc.Usage = D3D11_USAGE_DEFAULT;
    depthDesc.BindFlags = D3D11_BIND_DEPTH_STENCIL | D3D11_BIND_SHADER_RESOURCE;
    CheckHr(device->CreateTexture2D(&depthDesc, nullptr, &depthTexture_), L"深度バッファーの作成");
    const bool multisampled = sampleCount_ > 1;
    D3D11_DEPTH_STENCIL_VIEW_DESC dsvDesc{};
    dsvDesc.Format = Engine::kDepthFormat;
    dsvDesc.ViewDimension = multisampled ? D3D11_DSV_DIMENSION_TEXTURE2DMS : D3D11_DSV_DIMENSION_TEXTURE2D;
    CheckHr(device->CreateDepthStencilView(depthTexture_.Get(), &dsvDesc, &dsv_), L"深度ビューの作成");
    D3D11_SHADER_RESOURCE_VIEW_DESC srvDesc{};
    srvDesc.Format = DXGI_FORMAT_R24_UNORM_X8_TYPELESS;
    if (multisampled) {
        srvDesc.ViewDimension = D3D11_SRV_DIMENSION_TEXTURE2DMS;
    } else {
        srvDesc.ViewDimension = D3D11_SRV_DIMENSION_TEXTURE2D;
        srvDesc.Texture2D.MipLevels = 1;
    }
    CheckHr(device->CreateShaderResourceView(depthTexture_.Get(), &srvDesc, &depthSrv_), L"奥行きを読むビューの作成");
}

void View::ReleaseSampleResources() noexcept
{
    if (ID3D11DeviceContext* ctx = engine_.Context()) {
        ctx->OMSetRenderTargets(0, nullptr, nullptr);
    }
    msaaRtv_.Reset();
    msaaTexture_.Reset();
    surface_ = SurfaceTargets{}; // 画面の中の反射の下準備も、点の数に合わせて作り直す
    surfaceUnsupported_ = false;
    depthSrv_.Reset();
    dsv_.Reset();
    depthTexture_.Reset();
}

uint32_t View::SetSampleCount(uint32_t requested)
{
    requestedSampleCount_ = requested;
    if (engine_.IsDeviceLost()) {
        return sampleCount_; // GPU を作り直すときに、頼まれた数で作る
    }
    const uint32_t actual = engine_.SupportedSampleCount(requested);
    if (actual == sampleCount_) {
        return actual;
    }
    sampleCount_ = actual;
    if (rtv_) {
        ReleaseSampleResources();
        try {
            CreateSampleResources();
        } catch (const Error&) {
            // 作れなかった（メモリ不足など）ときは 1 点に戻して描けるようにしておき、失敗は呼び出し側へ知らせる
            ReleaseSampleResources();
            sampleCount_ = 1;
            requestedSampleCount_ = 1;
            CreateSampleResources();
            throw;
        }
    }
    return actual;
}

void View::EnsurePostResources(bool bloom, bool depth, bool dof, bool ao, bool fog, bool ssr)
{
    if (!scene_.texture) {
        engine_.CreatePostSurface(scene_, static_cast<UINT>(width_), static_cast<UINT>(height_), Engine::kColorFormat);
    }
    if (depth && !linearDepth_.texture) {
        engine_.CreatePostSurface(linearDepth_, static_cast<UINT>(width_), static_cast<UINT>(height_), DXGI_FORMAT_R32G32_FLOAT);
    }
    if (dof && !dof_.texture) {
        engine_.CreatePostSurface(dof_, static_cast<UINT>(width_), static_cast<UINT>(height_), Engine::kColorFormat);
    }
    if (ao && !ao_.texture) {
        engine_.CreatePostSurface(ao_, static_cast<UINT>(width_), static_cast<UINT>(height_), DXGI_FORMAT_R8_UNORM);
        engine_.CreatePostSurface(aoBlur_, static_cast<UINT>(width_), static_cast<UINT>(height_), DXGI_FORMAT_R8_UNORM);
        engine_.CreatePostSurface(aoScene_, static_cast<UINT>(width_), static_cast<UINT>(height_), Engine::kColorFormat);
    }
    if (fog && !fog_.texture) {
        engine_.CreatePostSurface(fog_, static_cast<UINT>(width_), static_cast<UINT>(height_), Engine::kColorFormat);
    }
    if (ssr && !ssr_.texture) {
        engine_.CreatePostSurface(ssr_, static_cast<UINT>(width_), static_cast<UINT>(height_), Engine::kColorFormat);
    }
    if (!bloom || !bloomDown_.empty()) {
        return;
    }
    // 1/2 から半分ずつ 6 段まで（小さくなりすぎたら止める）。光が色の段階に分かれないよう、16 ビットの浮動小数点で持つ
    constexpr int kBloomLevels = 6;
    UINT w = static_cast<UINT>(width_);
    UINT h = static_cast<UINT>(height_);
    for (int level = 0; level < kBloomLevels; ++level) {
        w = std::max<UINT>(1, (w + 1) / 2);
        h = std::max<UINT>(1, (h + 1) / 2);
        PostSurface surface;
        engine_.CreatePostSurface(surface, w, h, DXGI_FORMAT_R16G16B16A16_FLOAT);
        bloomDown_.push_back(std::move(surface));
        if (w <= 2 || h <= 2) {
            break;
        }
    }
    for (size_t i = 0; i + 1 < bloomDown_.size(); ++i) {
        PostSurface surface;
        engine_.CreatePostSurface(surface, bloomDown_[i].width, bloomDown_[i].height, DXGI_FORMAT_R16G16B16A16_FLOAT);
        bloomUp_.push_back(std::move(surface));
    }
}

void View::ReleaseSizeDependentResources() noexcept
{
    scene_ = PostSurface{};
    linearDepth_ = PostSurface{};
    dof_ = PostSurface{};
    ao_ = PostSurface{};
    aoBlur_ = PostSurface{};
    aoScene_ = PostSurface{};
    fog_ = PostSurface{};
    reflection_ = ReflectionTargets{};
    ssr_ = PostSurface{};
    bloomDown_.clear();
    bloomUp_.clear();
    ReleaseSampleResources();
    rtv_.Reset();
    colorTexture_.Reset();
    publishedTexture_.Reset();
    hasRendered_ = false;
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
    if (engine_.IsDeviceLost()) {
        return; // GPU を作り直すときに、今の大きさで作る
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
    engine_.ThrowIfDeviceLost();
    if (width_ == 0 || height_ == 0) {
        return true;
    }
    if (kind_ == Kind::Window && WaitForSingleObjectEx(frameLatencyWaitable_, 0, FALSE) != WAIT_OBJECT_0) {
        return false; // 前の表示がまだ終わっていない。待たずに次の機会に描く
    }
    // GPU の時刻を、描画の始めと終わりで記録する（途中で失敗しても、終わりの記録は必ず頼む）
    GpuTiming* timing = BeginGpuTiming();
    ScopeExit endTiming{[this, timing]() noexcept {
        if (timing) {
            EndGpuTiming(*timing);
        }
    }};

    const bool multisampled = sampleCount_ > 1;
    ID3D11RenderTargetView* target = multisampled ? msaaRtv_.Get() : rtv_.Get();
    ReflectionTargets* reflection = nullptr;
    if (Engine::UsesBlurredFloorReflection(params)) {
        if (!reflection_.color) {
            engine_.CreateReflectionTargets(reflection_, static_cast<UINT>(width_), static_cast<UINT>(height_));
        }
        reflection = &reflection_;
    } else if (reflection_.color) {
        reflection_ = ReflectionTargets{};
    }
    if (!Engine::UsesPostEffects(params)) {
        engine_.DrawScene(target, dsv_.Get(), width_, height_, params, multisampled, Engine::ScenePart::All, reflection);
    } else {
        // 場面を背景なしで描き（MSAA ならまとめてから）、映像効果をかけて描画先へ書き、その上に格子や選択の枠を重ねる。
        // MSAA の描画先へ書いた効果の結果は、どの点も同じ色なので、重ね描きの線だけがなめらかに混ざる
        const bool dof = (params.post_flags & TT_POST_DOF) != 0;
        const bool ao = (params.post_flags & TT_POST_AO) != 0;
        const bool fog = (params.post_flags & TT_POST_FOG) != 0;
        // 画面の中の反射は、下準備の画像を作れたときだけ（この GPU で MSAA の形式が使えなければ、反射なしで描く）
        SurfaceTargets* surface = nullptr;
        if (Engine::UsesScreenReflections(params)) {
            if (!surface_.rtv[0] && !surfaceUnsupported_) {
                surfaceUnsupported_ = !engine_.CreateSurfaceTargets(surface_, static_cast<UINT>(width_), static_cast<UINT>(height_), sampleCount_);
                if (surfaceUnsupported_) {
                    Log(TT_LOG_WARNING, L"この GPU では、今の MSAA の点の数で画面の中の反射の下準備を作れないため、反射なしで描きます");
                }
            }
            surface = surface_.rtv[0] ? &surface_ : nullptr;
        } else if (surface_.rtv[0]) {
            surface_ = SurfaceTargets{};
        }
        const bool ssr = surface != nullptr;
        const bool lines = (params.post_flags & TT_POST_LINES) != 0 || dof || ao || fog || ssr; // 奥行きを使う効果
        EnsurePostResources((params.post_flags & TT_POST_BLOOM) != 0, lines, dof, ao, fog, ssr);
        engine_.DrawScene(multisampled ? msaaRtv_.Get() : scene_.rtv.Get(), dsv_.Get(), width_, height_, params, multisampled,
            Engine::ScenePart::Scene, reflection, surface);
        if (multisampled) {
            engine_.Context()->ResolveSubresource(scene_.texture.Get(), 0, msaaTexture_.Get(), 0, Engine::kColorFormat);
        }
        if (surface) {
            engine_.ResolveSurfaceTargets(*surface);
        }
        Engine::PostInputs inputs;
        inputs.scene = scene_.srv.Get();
        inputs.bloomDown = &bloomDown_;
        inputs.bloomUp = &bloomUp_;
        inputs.depth = lines ? depthSrv_.Get() : nullptr;
        inputs.depthMultisampled = multisampled;
        inputs.linearDepth = lines ? &linearDepth_ : nullptr;
        inputs.dof = dof ? &dof_ : nullptr;
        inputs.ao = ao ? &ao_ : nullptr;
        inputs.aoBlur = ao ? &aoBlur_ : nullptr;
        inputs.aoScene = ao ? &aoScene_ : nullptr;
        inputs.fog = fog ? &fog_ : nullptr;
        inputs.surface = surface;
        inputs.ssr = ssr ? &ssr_ : nullptr;
        engine_.ApplyPostEffects(params, inputs, target, width_, height_);
        engine_.DrawScene(target, dsv_.Get(), width_, height_, params, multisampled, Engine::ScenePart::Overlays);
    }
    if (multisampled) {
        // 1 画素の点を平均して、表示・読み出しに使う面へまとめる
        engine_.Context()->ResolveSubresource(colorTexture_.Get(), 0, msaaTexture_.Get(), 0, Engine::kColorFormat);
    }
    hasRendered_ = true;

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
    engine_.ThrowIfDeviceLost();
    if (!colorTexture_) {
        throw Error(TT_E_INVALID_STATE, L"大きさが 0 のため読み出せる内容がありません");
    }
    if (!buffer || stride < width_ * 4 || static_cast<int64_t>(bufferSize) < static_cast<int64_t>(stride) * height_) {
        throw Error(TT_E_INVALID_ARG, L"読み出し先の大きさが足りません");
    }

    // 見える面を分けたビューは、見える面（最後に写した絵）を読む
    ID3D11Texture2D* visible = publishedTexture_ ? publishedTexture_.Get() : colorTexture_.Get();
    D3D11_TEXTURE2D_DESC desc{};
    visible->GetDesc(&desc);
    desc.Usage = D3D11_USAGE_STAGING;
    desc.BindFlags = 0;
    desc.CPUAccessFlags = D3D11_CPU_ACCESS_READ;
    desc.MiscFlags = 0;
    ComPtr<ID3D11Texture2D> staging;
    CheckHr(engine_.Device()->CreateTexture2D(&desc, nullptr, &staging), L"読み出し用テクスチャの作成");

    ID3D11DeviceContext* ctx = engine_.Context();
    ctx->CopyResource(staging.Get(), visible);
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

View::GpuTiming* View::BeginGpuTiming() noexcept
{
    ID3D11DeviceContext* ctx = engine_.Context();
    if (!ctx) {
        return nullptr;
    }
    CollectGpuTimings();
    GpuTiming& timing = gpuTimings_[nextGpuTiming_];
    if (timing.pending) {
        return nullptr; // GPU が 4 回分より遅れている。この回は測らない
    }
    if (!timing.disjoint) {
        D3D11_QUERY_DESC disjoint{D3D11_QUERY_TIMESTAMP_DISJOINT, 0};
        D3D11_QUERY_DESC stamp{D3D11_QUERY_TIMESTAMP, 0};
        ID3D11Device* device = engine_.Device();
        if (FAILED(device->CreateQuery(&disjoint, &timing.disjoint)) || FAILED(device->CreateQuery(&stamp, &timing.start))
            || FAILED(device->CreateQuery(&stamp, &timing.end))) {
            timing = GpuTiming{};
            return nullptr; // 時刻を記録できない GPU（測らずに描く）
        }
    }
    ctx->Begin(timing.disjoint.Get());
    ctx->End(timing.start.Get());
    return &timing;
}

void View::EndGpuTiming(GpuTiming& timing) noexcept
{
    ID3D11DeviceContext* ctx = engine_.Context();
    if (!ctx || !timing.disjoint) {
        return;
    }
    ctx->End(timing.end.Get());
    ctx->End(timing.disjoint.Get());
    timing.pending = true;
    nextGpuTiming_ = (nextGpuTiming_ + 1) % kGpuTimings;
}

void View::CollectGpuTimings() noexcept
{
    ID3D11DeviceContext* ctx = engine_.Context();
    if (!ctx || engine_.IsDeviceLost()) {
        return;
    }
    // 古い順（次に使う記録から）に読む。まだ結果が出ていない記録があれば、それより新しいものもまだ
    for (int k = 0; k < kGpuTimings; ++k) {
        GpuTiming& timing = gpuTimings_[(nextGpuTiming_ + k) % kGpuTimings];
        if (!timing.pending) {
            continue;
        }
        D3D11_QUERY_DATA_TIMESTAMP_DISJOINT disjoint{};
        UINT64 start = 0;
        UINT64 end = 0;
        if (ctx->GetData(timing.disjoint.Get(), &disjoint, sizeof(disjoint), D3D11_ASYNC_GETDATA_DONOTFLUSH) != S_OK
            || ctx->GetData(timing.start.Get(), &start, sizeof(start), D3D11_ASYNC_GETDATA_DONOTFLUSH) != S_OK
            || ctx->GetData(timing.end.Get(), &end, sizeof(end), D3D11_ASYNC_GETDATA_DONOTFLUSH) != S_OK) {
            break;
        }
        timing.pending = false;
        if (!disjoint.Disjoint && disjoint.Frequency > 0 && end >= start) {
            gpuMilliseconds_ = static_cast<float>(static_cast<double>(end - start) * 1000.0 / static_cast<double>(disjoint.Frequency));
        }
    }
}

float View::GpuMilliseconds() noexcept
{
    CollectGpuTimings();
    return gpuMilliseconds_;
}

void View::Publish()
{
    if (!publishedTexture_ || !hasRendered_) {
        return; // 見える面を分けていない・まだ描いていない
    }
    engine_.ThrowIfDeviceLost();
    engine_.Context()->CopyResource(publishedTexture_.Get(), colorTexture_.Get());
}

void View::ReleaseDeviceResources() noexcept
{
    for (GpuTiming& timing : gpuTimings_) {
        timing = GpuTiming{}; // 古いデバイスの記録は読めない
    }
    nextGpuTiming_ = 0;
    ReleaseSizeDependentResources();
    ReleaseSwapChain();
}

void View::RestoreDeviceResources()
{
    sampleCount_ = engine_.SupportedSampleCount(requestedSampleCount_); // 新しい GPU で使える数にする
    if (kind_ == Kind::Window) {
        CreateSwapChain();
    }
    if (width_ > 0 && height_ > 0) {
        CreateSizeDependentResources();
    }
}

} // namespace tt
