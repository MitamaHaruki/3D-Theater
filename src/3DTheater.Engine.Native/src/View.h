// 1 つの描画先（画面の 1 区画）。ウィンドウに直接出す方式と、画面外のテクスチャに描く方式がある。
#pragma once

#include <memory>

#include "Engine.h"

namespace tt {

class View final : public DeviceObject {
public:
    static std::unique_ptr<View> CreateForWindow(Engine& engine, HWND parent, int width, int height);
    static std::unique_ptr<View> CreateOffscreen(Engine& engine, int width, int height, uint32_t flags);

    ~View() override;

    View(const View&) = delete;
    View& operator=(const View&) = delete;

    bool IsValid() const noexcept { return magic_ == kMagic; }
    Engine& OwnerEngine() const noexcept { return engine_; }
    HWND Window() const noexcept { return hwnd_; }
    IDirect3DSurface9* D3D9Surface() const noexcept { return d3d9Surface_.Get(); }

    void Resize(int width, int height);
    // なめらかさ（MSAA の点の数 1・2・4・8）。使えない数なら少なくする。実際の数を返す
    uint32_t SetSampleCount(uint32_t requested);
    // 表示側の準備ができておらず描かなかったときは false
    bool Render(const TtFrameParams& params);
    void ReadPixels(void* buffer, int stride, int bufferSize);

    // GPU の作り直し。D3DImage 用の面は別の物になるので、呼び出し側は面を取り直すこと
    void ReleaseDeviceResources() noexcept override;
    void RestoreDeviceResources() override;

private:
    enum class Kind { Window, Offscreen };

    static constexpr uint32_t kMagic = 0x57454956; // "VIEW"

    View(Engine& engine, Kind kind, uint32_t flags);

    void CreateSwapChain();
    void CreateSizeDependentResources();
    void ReleaseSizeDependentResources() noexcept;
    // MSAA の描画先と奥行き（点の数で変わる物）
    void CreateSampleResources();
    void ReleaseSampleResources() noexcept;
    void ReleaseSwapChain() noexcept;

    uint32_t magic_ = kMagic;
    Engine& engine_;
    Kind kind_;
    uint32_t flags_;
    int width_ = 0;
    int height_ = 0;
    uint32_t requestedSampleCount_ = 1;
    uint32_t sampleCount_ = 1;

    HWND hwnd_ = nullptr;
    ComPtr<IDXGISwapChain2> swapChain_;
    HANDLE frameLatencyWaitable_ = nullptr; // 前の表示が終わると合図が来る

    ComPtr<ID3D11Texture2D> colorTexture_; // 表示・読み出しに使う面（MSAA のときは、描いた後にここへまとめる）
    ComPtr<ID3D11RenderTargetView> rtv_;
    ComPtr<ID3D11Texture2D> msaaTexture_; // MSAA のときだけ。ここへ描いてから colorTexture_ へまとめる
    ComPtr<ID3D11RenderTargetView> msaaRtv_;
    ComPtr<ID3D11Texture2D> depthTexture_;
    ComPtr<ID3D11DepthStencilView> dsv_;

    ComPtr<IDirect3DTexture9> d3d9Texture_;
    ComPtr<IDirect3DSurface9> d3d9Surface_;
};

} // namespace tt
