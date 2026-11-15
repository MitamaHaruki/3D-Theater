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
    // TT_VIEW_FLAG_DEFERRED_PUBLISH のとき、最後に描いた絵を見える面へ写す（GPU へ頼むだけ）。そのほかは何もしない
    void Publish();
    // 測れた中でいちばん新しい、1 回の描画に GPU が掛けた時間（ミリ秒）。まだ測れていなければ負（待たずに結果を読む）
    float GpuMilliseconds() noexcept;
    // GpuMilliseconds と同じ回の、段ごとの時間（TT_GPU_STAGE_* の順に count 個まで）。まだ測れていなければ false
    bool GpuBreakdown(float* out, uint32_t count) noexcept;

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
    // 映像効果の画像（使うときに作る。大きさが変わったら作り直す）
    void EnsurePostResources(bool bloom, bool depth, bool dof, bool ao, bool fog, bool ssr);
    void ReleaseSwapChain() noexcept;

    // GPU の時刻の記録 1 回分（描画の始めと終わりと、段の区切り。disjoint は、間で時計の速さが変わらなかったか）
    struct GpuTiming {
        ComPtr<ID3D11Query> disjoint;
        ComPtr<ID3D11Query> start;
        ComPtr<ID3D11Query> end;
        ComPtr<ID3D11Query> markQueries[TT_GPU_STAGE_COUNT - 1]; // 段の終わり（最後の段の終わりは end）
        Engine::GpuMarks marks;                                   // 描く間エンジンへ渡す区切り（markQueries を指す）
        bool pending = false; // 記録を頼んで、まだ結果を読んでいない
    };
    static constexpr int kGpuTimings = 4;
    // 描画の始めに時刻の記録を頼む（使える記録が無ければ null。その回は測らない）
    GpuTiming* BeginGpuTiming() noexcept;
    void EndGpuTiming(GpuTiming& timing) noexcept;
    // 結果が出た記録を、古い順に待たずに読む
    void CollectGpuTimings() noexcept;

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
    // TT_VIEW_FLAG_DEFERRED_PUBLISH のときの見える面（共有するときはこちらを Direct3D 9 と共有する）。colorTexture_ は描くだけの面になる
    ComPtr<ID3D11Texture2D> publishedTexture_;
    bool hasRendered_ = false; // 今の大きさで 1 度でも描いたか（見える面へ写せる絵があるか）
    ComPtr<ID3D11RenderTargetView> rtv_;
    ComPtr<ID3D11Texture2D> msaaTexture_; // MSAA のときだけ。ここへ描いてから colorTexture_ へまとめる
    ComPtr<ID3D11RenderTargetView> msaaRtv_;
    PostSurface scene_;                   // 映像効果を使うときの、背景なしの場面
    std::vector<PostSurface> bloomDown_;  // ブルームの縮めた段（半分ずつ）
    std::vector<PostSurface> bloomUp_;    // ブルームの広げ直す段
    PostSurface linearDepth_;             // 線画・被写界深度の準備（見る向きの距離）
    PostSurface dof_;                     // 被写界深度でぼかした場面
    PostSurface ao_;                      // 環境光の遮りの、光の届く割合
    PostSurface aoBlur_;                  // 光の届く割合を横にならしたもの
    PostSurface aoScene_;                 // 環境光の遮りをかけた場面
    PostSurface fog_;                     // 霧をかけた場面
    ReflectionTargets reflection_;        // 床の映り込みをぼかすときの画像（使うときに作り、使わなくなったら手放す）
    SurfaceTargets surface_;              // 画面の中の反射の下準備（使うときに作り、使わなくなったら手放す）
    bool surfaceUnsupported_ = false;     // この大きさ・点の数では下準備を作れない（MSAA の形式が使えない）
    PostSurface ssr_;                     // 画面の中の反射をかけた場面
    ComPtr<ID3D11Texture2D> depthTexture_;
    ComPtr<ID3D11DepthStencilView> dsv_;
    ComPtr<ID3D11ShaderResourceView> depthSrv_; // 奥行きを読む（線画に使う）

    ComPtr<IDirect3DTexture9> d3d9Texture_;
    ComPtr<IDirect3DSurface9> d3d9Surface_;

    GpuTiming gpuTimings_[kGpuTimings];
    int nextGpuTiming_ = 0;
    float gpuMilliseconds_ = -1.0f; // まだ測れていなければ負
    float gpuStages_[TT_GPU_STAGE_COUNT] = {}; // gpuMilliseconds_ と同じ回の、段ごとの時間
    bool gpuStagesReady_ = false;
};

} // namespace tt
