// GPU デバイスと、全ビューで共有する描画資源を持つ。
#pragma once

#include <DirectXMath.h>

#include <vector>

#include "Common.h"

namespace tt {

class Mesh;

// 映像効果に使う画像（描画先にも、読む画像にもなる）
struct PostSurface {
    ComPtr<ID3D11Texture2D> texture;
    ComPtr<ID3D11RenderTargetView> rtv;
    ComPtr<ID3D11ShaderResourceView> srv;
    UINT width = 0;
    UINT height = 0;
};

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

    // 1 枚のうち、どこを描くか。映像効果を使うときは、場面 → 効果 → 重ね描き の順に分けて描く
    enum class ScenePart {
        All,      // 全部（映像効果を使わないとき）。背景で消して、格子 → 場面 → 選択の枠 → 重ね描きの線
        Scene,    // 場面だけ（確認用の立方体・地面の影・モデル）。背景は透明（0, 0, 0, 0）で消す
        Overlays, // 場面の上に重ねる物（格子・座標軸・選択の枠・重ね描きの線）。消さずに、場面の奥行きを使って描く
    };

    // 1 つのビューの場面を描く（GPU へ頼むだけで完了は待たない）。描き終えたら描画先の割り当てを外す。
    // params.items の中身は呼び出し側（C API の入口）で確かめ済みであること。
    // multisampled は描画先が MSAA（1 画素に複数の点）か。線を、点の数に合わせてなめらかに描く
    void DrawScene(ID3D11RenderTargetView* rtv, ID3D11DepthStencilView* dsv, int width, int height, const TtFrameParams& params,
        bool multisampled, ScenePart part = ScenePart::All);

    // 映像効果を使うか（post_flags に知っている効果があるか）
    static bool UsesPostEffects(const TtFrameParams& params) noexcept;
    // 映像効果の画像を作る
    void CreatePostSurface(PostSurface& surface, UINT width, UINT height, DXGI_FORMAT format);
    // 映像効果に使う、場面の画像
    struct PostInputs {
        ID3D11ShaderResourceView* scene = nullptr;         // 背景なしで描いた場面
        const std::vector<PostSurface>* bloomDown = nullptr; // ブルームの縮めた段（半分ずつ小さく、2 つ以上）
        const std::vector<PostSurface>* bloomUp = nullptr;   // 広げ直す段（bloomDown より 1 つ少なく、同じ大きさ）
        ID3D11ShaderResourceView* depth = nullptr;         // 場面の奥行き（線画に使う）
        bool depthMultisampled = false;                    // 奥行きが MSAA か
        const PostSurface* linearDepth = nullptr;          // 線画・被写界深度の準備（見る向きの距離）を書く画像
        const PostSurface* dof = nullptr;                  // 被写界深度でぼかした場面を書く画像
        const PostSurface* ao = nullptr;                   // 環境光の遮りの、光の届く割合を書く画像
        const PostSurface* aoBlur = nullptr;               // 光の届く割合を横にならしたものを書く画像
        const PostSurface* aoScene = nullptr;              // 環境光の遮りをかけた場面を書く画像
        const PostSurface* fog = nullptr;                  // 霧をかけた場面を書く画像
    };
    // 映像効果をかける: 場面から output（width x height）へ。背景も置く
    void ApplyPostEffects(const TtFrameParams& params, const PostInputs& inputs, ID3D11RenderTargetView* output, int width, int height);

    // ビューの描画先の色の形式（スワップチェーンとも同じ）
    static constexpr DXGI_FORMAT kColorFormat = DXGI_FORMAT_B8G8R8A8_UNORM;
    static constexpr DXGI_FORMAT kDepthFormat = DXGI_FORMAT_D24_UNORM_S8_UINT;

    // MSAA の点の数のうち、この GPU が色と奥行きの両方で使える、requested 以下でいちばん多い数（1・2・4・8）
    uint32_t SupportedSampleCount(uint32_t requested) const;

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
    void CreatePostResources();
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
        DirectX::XMFLOAT4X4 lightViewProj; // セルフシャドウ（shadowParams.z が 1 のときだけ使う）
        DirectX::XMFLOAT4 shadowParams;
        DirectX::XMFLOAT4 groundShadowColor;
        DirectX::XMFLOAT4 reflectionParams; // x = 1 なら床の映り込みを描いている、y = 濃さ、z = 消えるまでの距離
        DirectX::XMFLOAT4 shadowSoftness;   // 柔らかな影（シェーダーの ShadowSoftness と同じ）
    };
    void WriteObjectConstants(DirectX::FXMMATRIX world, DirectX::CXMMATRIX worldViewProj, const ModelFrame& frame);
    // 足す照明を定数バッファーへ書く（1 枚の描画で 1 回。床の映り込みを描く間は、床で上下に映した照明を書く）
    void WriteLightConstants(const TtFrameParams& params, bool mirrored = false);
    // 床の映り込み: モデルを高さ 0 の床で上下に映して、床より下に描く
    void DrawFloorReflections(const TtFrameParams& params, const ModelFrame& frame);
    void BindMesh(const Mesh& mesh);
    void DrawMesh(const Mesh& mesh, DirectX::FXMMATRIX world, const ModelFrame& frame);
    void DrawEdges(const Mesh& mesh);
    bool RenderShadowMap(const TtFrameParams& params, ModelFrame& frame);
    void DrawGroundShadows(const TtFrameParams& params, const ModelFrame& frame);
    // 柔らかな地面の影: 「地面影」の材質だけで照明から見た奥行きの画像を作り（床まで入るように）、床に描く四角形の範囲を決める
    bool RenderGroundShadowMap(const TtFrameParams& params, const ModelFrame& frame, ModelFrame& groundFrame,
        DirectX::XMFLOAT4X4& floorWorld);
    void DrawSoftGroundShadow(const ModelFrame& groundFrame, const DirectX::XMFLOAT4X4& floorWorld);
    // 環境の画像を背景（空）に描く（平行投影では描かない）
    void DrawSky(const TtFrameParams& params, DirectX::CXMMATRIX viewProj);
    // 照明から見た奥行きを dsv（size × size）へ描く。groundCasters なら「地面影」、でなければ「セルフ影マップ」の材質を描く
    void DrawLightDepth(const TtFrameParams& params, DirectX::CXMMATRIX lightViewProj, const ModelFrame& frame,
        ID3D11DepthStencilView* dsv, UINT size, bool groundCasters);
    void EnsureShadowMap(UINT size);
    void EnsureDepthMap(UINT size, ComPtr<ID3D11DepthStencilView>& dsv, ComPtr<ID3D11ShaderResourceView>& srv, UINT& current,
        const wchar_t* what);
    void DrawBox(const DirectX::XMFLOAT3& minimum, const DirectX::XMFLOAT3& maximum, DirectX::FXMMATRIX world, DirectX::CXMMATRIX viewProj);
    // 格子と座標軸。Normal = 奥行きを比べる、Ignore = 奥行きを無視していちばん先に描く、Farthest = 何も描かれていない所だけに描く
    enum class GridDepth { Normal, Ignore, Farthest };
    void DrawGridAndAxes(const TtFrameParams& params, DirectX::CXMMATRIX viewProj, GridDepth depth, int width, int height);
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
    ComPtr<ID3D11RasterizerState> multisampleLineState_; // MSAA の描画先では、線を四角形として描いてなめらかにする
    ID3D11RasterizerState* basicState_ = nullptr;         // 今の 1 枚で使う方（上の 2 つのどちらか）
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
    ComPtr<ID3D11DepthStencilState> farDepthState_; // 奥行き 1（何も描かれていない所）だけに描く

    // 映像効果（画面全体の三角形を描いて、画像から画像へ処理する）
    ComPtr<ID3D11VertexShader> postVertexShader_;
    ComPtr<ID3D11PixelShader> bloomPrefilterShader_;
    ComPtr<ID3D11PixelShader> bloomDownShader_;
    ComPtr<ID3D11PixelShader> bloomUpShader_;
    ComPtr<ID3D11PixelShader> postCompositeShader_;
    ComPtr<ID3D11PixelShader> linearDepthShader_;
    ComPtr<ID3D11PixelShader> linearDepthMsShader_;
    ComPtr<ID3D11PixelShader> dofShader_;
    ComPtr<ID3D11PixelShader> aoShader_;
    ComPtr<ID3D11PixelShader> aoBlurShader_;
    ComPtr<ID3D11PixelShader> aoApplyShader_;
    ComPtr<ID3D11PixelShader> fogShader_;
    ComPtr<ID3D11Buffer> postConstantBuffer_;

    // モデル（MMD と同じトゥーンの色の計算と輪郭線）
    ComPtr<ID3D11VertexShader> modelVertexShader_;
    ComPtr<ID3D11PixelShader> modelPixelShader_;
    ComPtr<ID3D11VertexShader> edgeVertexShader_;
    ComPtr<ID3D11PixelShader> edgePixelShader_;
    ComPtr<ID3D11InputLayout> modelInputLayout_;
    ComPtr<ID3D11Buffer> objectConstantBuffer_;
    ComPtr<ID3D11Buffer> materialConstantBuffer_;
    ComPtr<ID3D11Buffer> lightConstantBuffer_; // 足す照明
    ComPtr<ID3D11RasterizerState> cullBackState_;
    ComPtr<ID3D11RasterizerState> cullNoneState_;
    ComPtr<ID3D11RasterizerState> cullFrontState_; // 輪郭線（裏側の面だけ描く）
    ComPtr<ID3D11RasterizerState> mirrorCullState_; // 床の映り込み（上下に映すと面の表裏が入れ替わるので、表の判定を逆にする）
    bool drawingReflection_ = false;
    ComPtr<ID3D11DepthStencilState> modelDepthState_;
    ComPtr<ID3D11BlendState> alphaBlendState_;
    ComPtr<ID3D11SamplerState> textureSampler_;
    ComPtr<ID3D11SamplerState> clampSampler_;
    ComPtr<ID3D11ShaderResourceView> whiteTexture_;

    // 影（セルフシャドウの奥行きの画像は、使うときに作る。使わない間は 1 x 1 の「影なし」を渡す）
    ComPtr<ID3D11VertexShader> shadowVertexShader_;
    ComPtr<ID3D11PixelShader> groundShadowPixelShader_;
    ComPtr<ID3D11RasterizerState> shadowCasterState_;   // 両面・奥行きを少し奥へ
    ComPtr<ID3D11DepthStencilState> groundShadowState_; // 奥行きは比べるだけ・ステンシルで 1 ピクセル 1 回
    ComPtr<ID3D11SamplerState> shadowSampler_;
    ComPtr<ID3D11ShaderResourceView> noShadowMap_;
    ComPtr<ID3D11DepthStencilView> shadowMapDsv_;
    ComPtr<ID3D11ShaderResourceView> shadowMapSrv_;
    UINT shadowMapSize_ = 0;
    ComPtr<ID3D11DepthStencilView> groundShadowMapDsv_; // 柔らかな地面の影の奥行きの画像
    ComPtr<ID3D11ShaderResourceView> groundShadowMapSrv_;
    UINT groundShadowMapSize_ = 0;
    ComPtr<ID3D11SamplerState> environmentSampler_; // 環境の画像（横は繰り返し、縦は端で止める）
    ComPtr<ID3D11PixelShader> skyPixelShader_;
    ComPtr<ID3D11Buffer> skyConstantBuffer_;
    ID3D11ShaderResourceView* currentEnvironment_ = nullptr; // 今描いている 1 枚の環境の画像（無ければ null）
    ComPtr<ID3D11VertexShader> groundSoftShadowVertexShader_;
    ComPtr<ID3D11PixelShader> groundSoftShadowPixelShader_;

    ComPtr<ID3D11Query> gpuDoneQuery_;
};

} // namespace tt
