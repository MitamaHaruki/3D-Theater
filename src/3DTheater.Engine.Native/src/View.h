// 1 つの描画先（画面の 1 区画）。ウィンドウに直接出す方式と、画面外のテクスチャに描く方式がある。
#pragma once

#include <memory>

#include "Common.h"

namespace tt {

class Engine;

class View {
public:
    static std::unique_ptr<View> CreateForWindow(Engine& engine, HWND parent, int width, int height);
    static std::unique_ptr<View> CreateOffscreen(Engine& engine, int width, int height, uint32_t flags);

    ~View();

    View(const View&) = delete;
    View& operator=(const View&) = delete;

    bool IsValid() const noexcept { return magic_ == kMagic; }
    Engine& OwnerEngine() const noexcept { return engine_; }
    HWND Window() const noexcept { return hwnd_; }
    IDirect3DSurface9* D3D9Surface() const noexcept { return d3d9Surface_.Get(); }

    void Resize(int width, int height);
    // 表示側の準備ができておらず描かなかったときは false
    bool Render(const TtFrameParams& params);
    void ReadPixels(void* buffer, int stride, int bufferSize);

private:
    enum class Kind { Window, Offscreen };

    static constexpr uint32_t kMagic = 0x57454956; // "VIEW"

    View(Engine& engine, Kind kind, uint32_t flags);

    void CreateSizeDependentResources();
    void ReleaseSizeDependentResources() noexcept;
    void WaitForGpu();

    uint32_t magic_ = kMagic;
    Engine& engine_;
    Kind kind_;
    uint32_t flags_;
    int width_ = 0;
    int height_ = 0;

    HWND hwnd_ = nullptr;
    ComPtr<IDXGISwapChain2> swapChain_;
    HANDLE frameLatencyWaitable_ = nullptr; // 前の表示が終わると合図が来る

    ComPtr<ID3D11Texture2D> colorTexture_;
    ComPtr<ID3D11RenderTargetView> rtv_;
    ComPtr<ID3D11Texture2D> depthTexture_;
    ComPtr<ID3D11DepthStencilView> dsv_;

    ComPtr<IDirect3DTexture9> d3d9Texture_;
    ComPtr<IDirect3DSurface9> d3d9Surface_;
    ComPtr<ID3D11Query> gpuDoneQuery_;
};

} // namespace tt
