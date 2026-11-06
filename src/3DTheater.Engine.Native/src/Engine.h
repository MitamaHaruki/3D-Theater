// GPU デバイスと、全ビューで共有する描画資源を持つ。
#pragma once

#include <DirectXMath.h>

#include "Common.h"

namespace tt {

class Mesh;

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

    // このエンジンで作ったビュー・テクスチャ・メッシュの数。残っているうちはエンジンを破棄しない
    void AddObject() noexcept { ++liveObjectCount_; }
    void RemoveObject() noexcept { --liveObjectCount_; }
    int LiveObjectCount() const noexcept { return liveObjectCount_; }

private:
    static constexpr uint32_t kMagic = 0x4E454754; // "TGEN"

    void CreateDevice(bool useWarp, bool debugLayer);
    void CreateSceneResources();
    void CreateModelResources();
    void SetBasicPipeline();
    void SetConstants(DirectX::FXMMATRIX worldViewProj);
    void DrawMesh(const Mesh& mesh, DirectX::FXMMATRIX world, DirectX::CXMMATRIX viewProj);
    void DrawBox(const DirectX::XMFLOAT3& minimum, const DirectX::XMFLOAT3& maximum, DirectX::FXMMATRIX world, DirectX::CXMMATRIX viewProj);
    void DrawOverlayLines(const TtLineVertex* vertices, uint32_t count, DirectX::CXMMATRIX viewProj);

    uint32_t magic_ = kMagic;
    DWORD ownerThreadId_;
    int liveObjectCount_ = 0;
    bool debugLayerEnabled_ = false;
    D3D_FEATURE_LEVEL featureLevel_ = D3D_FEATURE_LEVEL_11_0;

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

    // モデル（テクスチャと簡単な陰影）
    ComPtr<ID3D11VertexShader> modelVertexShader_;
    ComPtr<ID3D11PixelShader> modelPixelShader_;
    ComPtr<ID3D11InputLayout> modelInputLayout_;
    ComPtr<ID3D11Buffer> objectConstantBuffer_;
    ComPtr<ID3D11Buffer> materialConstantBuffer_;
    ComPtr<ID3D11RasterizerState> cullBackState_;
    ComPtr<ID3D11RasterizerState> cullNoneState_;
    ComPtr<ID3D11DepthStencilState> modelDepthState_;
    ComPtr<ID3D11BlendState> alphaBlendState_;
    ComPtr<ID3D11SamplerState> textureSampler_;
    ComPtr<ID3D11ShaderResourceView> whiteTexture_;

    ComPtr<ID3D11Query> gpuDoneQuery_;
};

} // namespace tt
