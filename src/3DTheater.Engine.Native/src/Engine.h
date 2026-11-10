// GPU デバイスと、全ビューで共有する描画資源を持つ。
#pragma once

#include <DirectXMath.h>

#include <vector>

#include "Common.h"

namespace tt {

class Mesh;

// GPU の上に資源を持つもの（ビュー・テクスチャ・メッシュ）。
// GPU が使えなくなったとき、エンジンが全部の資源を手放させ、デバイスを作り直してから作り直させる。
class DeviceObject {
public:
    virtual ~DeviceObject() = default;

    // 古いデバイスの資源を手放す（作り直しの前に呼ばれる）
    virtual void ReleaseDeviceResources() noexcept = 0;
    // 新しいデバイスで資源を作り直す（CPU 側に持っている中身から）
    virtual void RestoreDeviceResources() = 0;
};

class Engine {
public:
    explicit Engine(const TtEngineDesc& desc);
    ~Engine();

    Engine(const Engine&) = delete;
    Engine& operator=(const Engine&) = delete;

    bool IsValid() const noexcept { return magic_ == kMagic; }
    void CheckThread() const;

    ID3D11Device* Device() const noexcept { return device_.Get(); }
    ID3D11DeviceContext* Context() const noexcept { return context_.Get(); }
    IDXGIFactory2* Factory() const noexcept { return factory_.Get(); }

    // WPF の D3DImage と面を共有するための Direct3D 9Ex デバイス。初めて必要になったときに作る。
    IDirect3DDevice9Ex* D3D9Device();

    void FillInfo(TtEngineInfo& info) const;
    void QueryVideoMemory(TtVideoMemoryInfo& info) const;

    // 1 つのビューの場面を描く（GPU へ頼むだけで完了は待たない）。描き終えたら描画先の割り当てを外す。
    // params.items の中身は呼び出し側（C API の入口）で確かめ済みであること。
    void DrawScene(ID3D11RenderTargetView* rtv, ID3D11DepthStencilView* dsv, int width, int height, const TtFrameParams& params);

    // それまでに頼んだ描画を GPU が終えるまで待つ。
    void WaitForGpu();

    // ---- GPU が使えなくなったとき（取り外し・ドライバーの更新・応答なし） ----

    // GPU が使えない状態か（作り直しに失敗したままのときも true）
    bool IsDeviceLost() const noexcept;
    // 使えない状態なら TT_E_DEVICE_LOST を投げる。GPU を使う操作の入口で呼ぶ
    void ThrowIfDeviceLost() const;
    // デバイスと全部の資源を作り直す。失敗したら例外を投げ、使えない状態のまま残る（あとでもう一度呼べる）
    void Recover();
    // 試験・診断用: GPU が使えなくなったことにする（本物の取り外しは起こせないため）
    void SimulateDeviceLost() noexcept { lost_ = true; }
    // デバイスを作り直した回数（資源が古いデバイスのものかを見分ける）
    uint32_t DeviceGeneration() const noexcept { return generation_; }

    // このエンジンで作ったビュー・テクスチャ・メッシュ。残っているうちはエンジンを破棄しない
    void Register(DeviceObject& object);
    void Unregister(DeviceObject& object) noexcept;
    int LiveObjectCount() const noexcept { return static_cast<int>(objects_.size()); }

private:
    static constexpr uint32_t kMagic = 0x4E454754; // "TGEN"

    void CreateDevice();
    void CreateSceneResources();
    void CreateModelResources();
    void ReleaseDevice() noexcept;
    void SetBasicPipeline();
    void SetConstants(DirectX::FXMMATRIX worldViewProj);
    // 1 枚の描画で共通の、モデルの描き方（照明・カメラ・輪郭線の太さ）
    struct ModelFrame {
        DirectX::XMFLOAT4X4 view;
        DirectX::XMFLOAT4X4 viewProj;
        DirectX::XMFLOAT4 lightDirection;
        DirectX::XMFLOAT4 lightColor;
        DirectX::XMFLOAT4 cameraPosition;
        DirectX::XMFLOAT4 edgeScale;
    };
    void DrawMesh(const Mesh& mesh, DirectX::FXMMATRIX world, const ModelFrame& frame);
    void DrawEdges(const Mesh& mesh);
    void DrawBox(const DirectX::XMFLOAT3& minimum, const DirectX::XMFLOAT3& maximum, DirectX::FXMMATRIX world, DirectX::CXMMATRIX viewProj);
    void DrawOverlayLines(const TtLineVertex* vertices, uint32_t count, DirectX::CXMMATRIX viewProj);

    uint32_t magic_ = kMagic;
    DWORD ownerThreadId_;
    bool useWarp_ = false;
    bool debugLayerRequested_ = false;
    bool debugLayerEnabled_ = false;
    bool lost_ = false; // 試験で使えないことにした・D3D9 側で失った・作り直しに失敗した
    uint32_t generation_ = 0;
    std::vector<DeviceObject*> objects_;
    D3D_FEATURE_LEVEL featureLevel_ = D3D_FEATURE_LEVEL_11_0;
    DXGI_ADAPTER_DESC1 adapterDesc_{};

    ComPtr<IDXGIFactory2> factory_;
    ComPtr<IDXGIAdapter1> adapter_;
    ComPtr<ID3D11Device> device_;
    ComPtr<ID3D11DeviceContext> context_;

    ComPtr<IDirect3D9Ex> d3d9_;
    ComPtr<IDirect3DDevice9Ex> d3d9Device_;

    // 線と確認用の立方体（頂点の色をそのまま出す）
    ComPtr<ID3D11VertexShader> vertexShader_;
    ComPtr<ID3D11PixelShader> pixelShader_;
    ComPtr<ID3D11InputLayout> inputLayout_;
    ComPtr<ID3D11Buffer> constantBuffer_;
    ComPtr<ID3D11RasterizerState> rasterizerState_;
    ComPtr<ID3D11DepthStencilState> depthState_;

    ComPtr<ID3D11Buffer> gridVertexBuffer_;
    UINT gridVertexCount_ = 0;
    ComPtr<ID3D11Buffer> axisVertexBuffer_;
    ComPtr<ID3D11Buffer> cubeVertexBuffer_;
    ComPtr<ID3D11Buffer> cubeIndexBuffer_;
    UINT cubeIndexCount_ = 0;
    ComPtr<ID3D11Buffer> cubeEdgeVertexBuffer_;
    UINT cubeEdgeVertexCount_ = 0;
    ComPtr<ID3D11Buffer> boxEdgeVertexBuffer_; // 1 辺 1 の箱の辺（拡大せず）。選択中のモデルの強調に使う
    UINT boxEdgeVertexCount_ = 0;
    ComPtr<ID3D11Buffer> overlayVertexBuffer_; // 重ね描きの線（足りなくなったら大きく作り直す）
    UINT overlayVertexCapacity_ = 0;
    ComPtr<ID3D11DepthStencilState> overlayDepthState_;

    // モデル（MMD と同じトゥーンの色の計算と輪郭線）
    ComPtr<ID3D11VertexShader> modelVertexShader_;
    ComPtr<ID3D11PixelShader> modelPixelShader_;
    ComPtr<ID3D11VertexShader> edgeVertexShader_;
    ComPtr<ID3D11PixelShader> edgePixelShader_;
    ComPtr<ID3D11InputLayout> modelInputLayout_;
    ComPtr<ID3D11Buffer> objectConstantBuffer_;
    ComPtr<ID3D11Buffer> materialConstantBuffer_;
    ComPtr<ID3D11RasterizerState> cullBackState_;
    ComPtr<ID3D11RasterizerState> cullNoneState_;
    ComPtr<ID3D11RasterizerState> cullFrontState_; // 輪郭線（裏側の面だけ描く）
    ComPtr<ID3D11DepthStencilState> modelDepthState_;
    ComPtr<ID3D11BlendState> alphaBlendState_;
    ComPtr<ID3D11SamplerState> textureSampler_;
    ComPtr<ID3D11SamplerState> clampSampler_;
    ComPtr<ID3D11ShaderResourceView> whiteTexture_;

    ComPtr<ID3D11Query> gpuDoneQuery_;
};

} // namespace tt
