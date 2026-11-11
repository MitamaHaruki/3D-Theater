#include "Engine.h"

#include <algorithm>
#include <cfloat>
#include <cmath>
#include <cstring>
#include <vector>

#include "DemoScene.h"
#include "Log.h"
#include "Resources.h"

// FxCompile が中間フォルダに書き出すシェーダーのバイト列
#include "BasicPS.h"
#include "BasicVS.h"
#include "BloomDownPS.h"
#include "BloomPrefilterPS.h"
#include "BloomUpPS.h"
#include "PostCompositePS.h"
#include "PostVS.h"
#include "EdgePS.h"
#include "EdgeVS.h"
#include "DofPS.h"
#include "AoPS.h"
#include "AoBlurPS.h"
#include "AoApplyPS.h"
#include "FogPS.h"
#include "GroundSoftShadowVS.h"
#include "GroundSoftShadowPS.h"
#include "SkyPS.h"
#include "GroundShadowPS.h"
#include "LinearDepthMsPS.h"
#include "LinearDepthPS.h"
#include "ModelPS.h"
#include "ModelVS.h"
#include "ShadowVS.h"

namespace tt {

namespace {

constexpr float kClearColor[4] = {0.13f, 0.14f, 0.16f, 1.0f};

// MMD の初期の照明（光が進む向き (-0.5, -1, 0.5) と、色 154/255）。TT_FRAME_USE_LIGHT が無いときに使う
const DirectX::XMVECTORF32 kLightDirection = {{{-0.4082483f, -0.8164966f, 0.4082483f, 0.0f}}};
constexpr float kLightIntensity = 154.0f / 255.0f;

// セルフシャドウの奥行きの画像の 1 辺（指定が無いとき）と、比べるときに奥へずらす量（ワールドの長さ）
constexpr UINT kDefaultShadowMapSize = 2048;
constexpr float kShadowBiasWorld = 0.08f;
// 地面の影を床から浮かせる高さ（床の面と重なってちらつかないように）
constexpr float kGroundShadowLift = 0.01f;
// 柔らかな影で、ぼかす幅（円の半径）の上限（ワールドの長さ。シェーダーの kMaxSoftShadowWorld と同じ）
constexpr float kMaxSoftShadowWorld = 3.0f;

// 柔らかな影の光源の見かけの半径の tan（0〜0.5。数でなければ 0 = くっきり）
float ShadowSoftnessOf(const TtFrameParams& params) noexcept
{
    return std::isfinite(params.shadow_softness) ? std::clamp(params.shadow_softness, 0.0f, 0.5f) : 0.0f;
}

struct FrameConstants {
    DirectX::XMFLOAT4X4 worldViewProj;
};

struct ObjectConstants {
    DirectX::XMFLOAT4X4 worldViewProj;
    DirectX::XMFLOAT4X4 world;
    DirectX::XMFLOAT4X4 view;
    DirectX::XMFLOAT4 lightDirection;
    DirectX::XMFLOAT4 lightColor;
    DirectX::XMFLOAT4 cameraPosition;
    DirectX::XMFLOAT4 edgeScale;
    DirectX::XMFLOAT4X4 lightViewProj;
    DirectX::XMFLOAT4 shadowParams;
    DirectX::XMFLOAT4 groundShadowColor;
    DirectX::XMFLOAT4 reflectionParams;
    DirectX::XMFLOAT4 shadowSoftness;
    DirectX::XMFLOAT4 passParams; // レンダーパス（ModelFrame と同じ）
};

// 足す照明（シェーダーの LightConstants と同じ並び）
struct LightConstants {
    uint32_t count[4];
    struct Entry {
        DirectX::XMFLOAT4 position;  // w = 種類
        DirectX::XMFLOAT4 direction; // w = 届く距離
        DirectX::XMFLOAT4 color;     // w = スポットの外側の cos
        DirectX::XMFLOAT4 spot;      // x = スポットの内側の cos
    } lights[TT_MAX_LIGHTS];
    DirectX::XMFLOAT4 environment;              // x = 1 なら環境の画像を使う、y = 明るさ、z = 回転、w = 縮小版の数
    DirectX::XMFLOAT4 environmentIrradiance[9]; // 拡散の明るさの球面調和関数の係数
};

// 背景（空）を描く定数（シェーダーの SkyConstants と同じ並び）
struct SkyConstants {
    DirectX::XMFLOAT4X4 inverseViewProj;
    DirectX::XMFLOAT4 params; // y = 明るさ、z = 回転
};

struct PostConstants {
    DirectX::XMFLOAT4 sourceTexel;
    DirectX::XMFLOAT4 bloomParams;
    DirectX::XMFLOAT4 gradeParams;
    DirectX::XMFLOAT4 whiteBalance;
    DirectX::XMFLOAT4 backgroundColor;
    uint32_t flags[4];
    DirectX::XMFLOAT4 lineParams;
    DirectX::XMFLOAT4 lineColor;
    DirectX::XMFLOAT4 paintParams;
    DirectX::XMFLOAT4 shadowTint;
    DirectX::XMFLOAT4 projectionParams;
    DirectX::XMFLOAT4 dofParams;
    DirectX::XMFLOAT4 aoParams;
    DirectX::XMFLOAT4 screenToView;
    DirectX::XMFLOAT4 fogColor;
    DirectX::XMFLOAT4 fogParams;
    DirectX::XMFLOAT4 fogHeightAxis;
};

constexpr uint32_t kKnownPostFlags = TT_POST_COLOR | TT_POST_BLOOM | TT_POST_VIGNETTE | TT_POST_LINES | TT_POST_POSTERIZE
    | TT_POST_SHADOW_TINT | TT_POST_PAPER | TT_POST_DOF | TT_POST_AO | TT_POST_FOG;

// 数でない値は fallback に、範囲の外は範囲に収める
float Sanitize(float value, float fallback, float minimum, float maximum) noexcept
{
    return std::isfinite(value) ? std::clamp(value, minimum, maximum) : fallback;
}

struct MaterialConstants {
    DirectX::XMFLOAT4 diffuse;
    DirectX::XMFLOAT4 ambientAndTexture; // w が 1 ならテクスチャあり
    DirectX::XMFLOAT4 specularAndPower;
    DirectX::XMFLOAT4 textureMultiply;
    DirectX::XMFLOAT4 textureAdd;
    DirectX::XMFLOAT4 sphereMultiply;
    DirectX::XMFLOAT4 sphereAdd;
    DirectX::XMFLOAT4 toonMultiply;
    DirectX::XMFLOAT4 toonAdd;
    DirectX::XMFLOAT4 edgeColor;
    DirectX::XMFLOAT4 options; // x = スフィアの使い方、y = トゥーンあり、z = 輪郭線の太さ、w = セルフシャドウを受ける
    DirectX::XMFLOAT4 pbr;     // x = 1 なら PBR、y = 金属らしさ、z = 粗さ
    DirectX::XMFLOAT4 baseTint;
    DirectX::XMFLOAT4 emissive;
    DirectX::XMFLOAT4 materialIds; // x = 材質の番号（0 から。レンダーパスの材質の番号の色に使う）
};

bool IsFinite3(const float (&v)[3]) noexcept
{
    return std::isfinite(v[0]) && std::isfinite(v[1]) && std::isfinite(v[2]);
}

static_assert(sizeof(TtLineVertex) == sizeof(tt::DemoVertex), "重ね描きの線は、格子と同じ頂点の形で描く");

const wchar_t* FeatureLevelName(D3D_FEATURE_LEVEL level)
{
    switch (level) {
    case D3D_FEATURE_LEVEL_11_1:
        return L"11_1";
    case D3D_FEATURE_LEVEL_11_0:
        return L"11_0";
    default:
        return L"不明";
    }
}

template <class T>
ComPtr<ID3D11Buffer> CreateImmutableBuffer(ID3D11Device* device, const std::vector<T>& data, UINT bindFlags, const wchar_t* what)
{
    D3D11_BUFFER_DESC desc{};
    desc.ByteWidth = static_cast<UINT>(data.size() * sizeof(T));
    desc.Usage = D3D11_USAGE_IMMUTABLE;
    desc.BindFlags = bindFlags;
    D3D11_SUBRESOURCE_DATA init{};
    init.pSysMem = data.data();
    ComPtr<ID3D11Buffer> buffer;
    CheckHr(device->CreateBuffer(&desc, &init, &buffer), what);
    return buffer;
}

} // namespace

Engine::Engine(const TtEngineDesc& desc)
    : ownerThreadId_(GetCurrentThreadId()), useWarp_((desc.flags & TT_ENGINE_FLAG_USE_WARP) != 0),
      debugLayerRequested_((desc.flags & TT_ENGINE_FLAG_DEBUG_LAYER) != 0)
{
    CreateDevice();
    CreateSceneResources();
    CreateModelResources();
    CreatePostResources();
}

Engine::~Engine()
{
    ReleaseDevice();
    magic_ = 0;
}

void Engine::Register(DeviceObject& object)
{
    objects_.push_back(&object);
}

void Engine::Unregister(DeviceObject& object) noexcept
{
    std::erase(objects_, &object);
}

bool Engine::IsDeviceLost() const noexcept
{
    return lost_ || !device_ || FAILED(device_->GetDeviceRemovedReason());
}

void Engine::ThrowIfDeviceLost() const
{
    if (IsDeviceLost()) {
        throw Error(TT_E_DEVICE_LOST,
            L"GPU が使えなくなっています（取り外し・ドライバーの更新・応答なしなど）。tt_engine_recover で作り直してください");
    }
}

void Engine::Recover()
{
    const HRESULT reason = device_ ? device_->GetDeviceRemovedReason() : E_FAIL;
    Log(TT_LOG_WARNING,
        reason == S_OK && lost_ ? std::wstring(L"GPU を作り直します（試験・診断のための模擬）")
                                : std::format(L"GPU を作り直します（理由 0x{:08X}）", static_cast<uint32_t>(reason)));

    // 古いデバイスの資源を全部手放してから、デバイスを作り直す
    for (DeviceObject* object : objects_) {
        object->ReleaseDeviceResources();
    }
    ReleaseDevice();
    lost_ = true; // 途中で失敗したら、使えない状態のまま残す

    CreateDevice();
    CreateSceneResources();
    CreateModelResources();
    CreatePostResources();
    ++generation_;
    for (DeviceObject* object : objects_) {
        object->RestoreDeviceResources();
    }
    lost_ = false;
    Log(TT_LOG_INFO, std::format(L"GPU を作り直しました（ビュー・テクスチャ・メッシュ {} 個を作り直し）", objects_.size()));
}

void Engine::ReleaseDevice() noexcept
{
    if (context_) {
        context_->ClearState();
        context_->Flush();
    }
    gpuDoneQuery_.Reset();
    dofShader_.Reset();
    aoShader_.Reset();
    aoBlurShader_.Reset();
    aoApplyShader_.Reset();
    fogShader_.Reset();
    linearDepthMsShader_.Reset();
    linearDepthShader_.Reset();
    postConstantBuffer_.Reset();
    postCompositeShader_.Reset();
    bloomUpShader_.Reset();
    bloomDownShader_.Reset();
    bloomPrefilterShader_.Reset();
    postVertexShader_.Reset();
    farDepthState_.Reset();
    shadowMapSrv_.Reset();
    shadowMapDsv_.Reset();
    shadowMapSize_ = 0;
    noShadowMap_.Reset();
    shadowSampler_.Reset();
    groundShadowState_.Reset();
    shadowCasterState_.Reset();
    groundShadowPixelShader_.Reset();
    shadowVertexShader_.Reset();
    groundShadowMapSrv_.Reset();
    groundShadowMapDsv_.Reset();
    groundShadowMapSize_ = 0;
    groundSoftShadowVertexShader_.Reset();
    groundSoftShadowPixelShader_.Reset();
    environmentSampler_.Reset();
    skyPixelShader_.Reset();
    skyConstantBuffer_.Reset();
    currentEnvironment_ = nullptr;
    whiteTexture_.Reset();
    clampSampler_.Reset();
    textureSampler_.Reset();
    alphaBlendState_.Reset();
    modelDepthState_.Reset();
    cullFrontState_.Reset();
    mirrorCullState_.Reset();
    cullNoneState_.Reset();
    cullBackState_.Reset();
    materialConstantBuffer_.Reset();
    lightConstantBuffer_.Reset();
    objectConstantBuffer_.Reset();
    modelInputLayout_.Reset();
    edgePixelShader_.Reset();
    edgeVertexShader_.Reset();
    modelPixelShader_.Reset();
    modelVertexShader_.Reset();
    overlayDepthState_.Reset();
    overlayVertexBuffer_.Reset();
    overlayVertexCapacity_ = 0;
    boxEdgeVertexBuffer_.Reset();
    cubeEdgeVertexBuffer_.Reset();
    cubeIndexBuffer_.Reset();
    cubeVertexBuffer_.Reset();
    axisVertexBuffer_.Reset();
    gridVertexBuffer_.Reset();
    depthState_.Reset();
    basicState_ = nullptr;
    multisampleLineState_.Reset();
    rasterizerState_.Reset();
    constantBuffer_.Reset();
    inputLayout_.Reset();
    pixelShader_.Reset();
    vertexShader_.Reset();
    d3d9Device_.Reset();
    d3d9_.Reset();
    context_.Reset();
    device_.Reset();
    adapter_.Reset();
    factory_.Reset();
}

void Engine::CheckThread() const
{
    if (GetCurrentThreadId() != ownerThreadId_) {
        throw Error(TT_E_WRONG_THREAD, L"エンジンを作ったスレッド以外から呼び出されました");
    }
}

void Engine::CreateDevice()
{
    const bool useWarp = useWarp_;
    const bool debugLayer = debugLayerRequested_;
    debugLayerEnabled_ = false;
    UINT factoryFlags = debugLayer ? DXGI_CREATE_FACTORY_DEBUG : 0;
    HRESULT hr = CreateDXGIFactory2(factoryFlags, IID_PPV_ARGS(&factory_));
    if (FAILED(hr) && debugLayer) {
        hr = CreateDXGIFactory2(0, IID_PPV_ARGS(&factory_));
    }
    CheckHr(hr, L"DXGI ファクトリーの作成");

    if (useWarp) {
        ComPtr<IDXGIFactory4> factory4;
        CheckHr(factory_.As(&factory4), L"IDXGIFactory4 の取得");
        CheckHr(factory4->EnumWarpAdapter(IID_PPV_ARGS(&adapter_)), L"WARP アダプターの取得");
    } else {
        // ノート PC などで GPU が 2 つあるときは高性能側を選ぶ
        ComPtr<IDXGIFactory6> factory6;
        if (SUCCEEDED(factory_.As(&factory6))) {
            hr = factory6->EnumAdapterByGpuPreference(0, DXGI_GPU_PREFERENCE_HIGH_PERFORMANCE, IID_PPV_ARGS(&adapter_));
        } else {
            hr = factory_->EnumAdapters1(0, &adapter_);
        }
        CheckHr(hr, L"GPU アダプターの取得");
    }

    const D3D_FEATURE_LEVEL levels[] = {D3D_FEATURE_LEVEL_11_1, D3D_FEATURE_LEVEL_11_0};
    UINT flags = D3D11_CREATE_DEVICE_BGRA_SUPPORT;
    if (debugLayer) {
        hr = D3D11CreateDevice(adapter_.Get(), D3D_DRIVER_TYPE_UNKNOWN, nullptr, flags | D3D11_CREATE_DEVICE_DEBUG,
            levels, ARRAYSIZE(levels), D3D11_SDK_VERSION, &device_, &featureLevel_, &context_);
        if (SUCCEEDED(hr)) {
            debugLayerEnabled_ = true;
        } else {
            Log(TT_LOG_WARNING,
                L"Direct3D の検査機能（デバッグレイヤー）を使えません。Windows の「グラフィックス ツール」が入っていない可能性があります。検査なしで続けます");
        }
    }
    if (!device_) {
        hr = D3D11CreateDevice(adapter_.Get(), D3D_DRIVER_TYPE_UNKNOWN, nullptr, flags, levels, ARRAYSIZE(levels),
            D3D11_SDK_VERSION, &device_, &featureLevel_, &context_);
        CheckHr(hr, L"Direct3D 11 デバイスの作成");
    }

    // 描画の先読みを 1 フレームに抑え、操作から表示までの遅れを減らす
    ComPtr<IDXGIDevice1> dxgiDevice;
    if (SUCCEEDED(device_.As(&dxgiDevice))) {
        dxgiDevice->SetMaximumFrameLatency(1);
    }

    adapter_->GetDesc1(&adapterDesc_);
    Log(TT_LOG_INFO,
        std::format(L"Direct3D 11 を初期化しました: {} / 機能レベル {} / 専用メモリ {} MB / 検査機能 {}",
            adapterDesc_.Description, FeatureLevelName(featureLevel_), adapterDesc_.DedicatedVideoMemory / (1024 * 1024),
            debugLayerEnabled_ ? L"あり" : L"なし"));
}

void Engine::CreateSceneResources()
{
    CheckHr(device_->CreateVertexShader(g_BasicVS, sizeof(g_BasicVS), nullptr, &vertexShader_), L"頂点シェーダーの作成");
    CheckHr(device_->CreatePixelShader(g_BasicPS, sizeof(g_BasicPS), nullptr, &pixelShader_), L"ピクセルシェーダーの作成");

    const D3D11_INPUT_ELEMENT_DESC layout[] = {
        {"POSITION", 0, DXGI_FORMAT_R32G32B32_FLOAT, 0, 0, D3D11_INPUT_PER_VERTEX_DATA, 0},
        {"COLOR", 0, DXGI_FORMAT_R32G32B32A32_FLOAT, 0, 12, D3D11_INPUT_PER_VERTEX_DATA, 0},
    };
    CheckHr(device_->CreateInputLayout(layout, ARRAYSIZE(layout), g_BasicVS, sizeof(g_BasicVS), &inputLayout_),
        L"頂点レイアウトの作成");

    D3D11_BUFFER_DESC cbDesc{};
    cbDesc.ByteWidth = sizeof(FrameConstants);
    cbDesc.Usage = D3D11_USAGE_DYNAMIC;
    cbDesc.BindFlags = D3D11_BIND_CONSTANT_BUFFER;
    cbDesc.CPUAccessFlags = D3D11_CPU_ACCESS_WRITE;
    CheckHr(device_->CreateBuffer(&cbDesc, nullptr, &constantBuffer_), L"定数バッファーの作成");

    D3D11_RASTERIZER_DESC rsDesc{};
    rsDesc.FillMode = D3D11_FILL_SOLID;
    rsDesc.CullMode = D3D11_CULL_NONE; // 確認用の形なので裏面も描く。材質ごとの設定は Phase 2 で行う
    rsDesc.DepthClipEnable = TRUE;
    CheckHr(device_->CreateRasterizerState(&rsDesc, &rasterizerState_), L"ラスタライザー設定の作成");
    rsDesc.MultisampleEnable = TRUE;
    CheckHr(device_->CreateRasterizerState(&rsDesc, &multisampleLineState_), L"なめらかな線の設定の作成");
    basicState_ = rasterizerState_.Get();

    D3D11_DEPTH_STENCIL_DESC dsDesc{};
    dsDesc.DepthEnable = TRUE;
    dsDesc.DepthWriteMask = D3D11_DEPTH_WRITE_MASK_ALL;
    dsDesc.DepthFunc = D3D11_COMPARISON_LESS;
    CheckHr(device_->CreateDepthStencilState(&dsDesc, &depthState_), L"深度設定の作成");

    const std::vector<DemoVertex> grid = BuildGridLines(50, 1.0f);
    gridVertexBuffer_ = CreateImmutableBuffer(device_.Get(), grid, D3D11_BIND_VERTEX_BUFFER, L"格子の頂点バッファー作成");
    gridVertexCount_ = static_cast<UINT>(grid.size());

    axisVertexBuffer_ = CreateImmutableBuffer(device_.Get(), BuildAxisLines(50.0f), D3D11_BIND_VERTEX_BUFFER, L"座標軸の頂点バッファー作成");

    std::vector<DemoVertex> cubeVertices;
    std::vector<uint16_t> cubeIndices;
    BuildCube(cubeVertices, cubeIndices);
    cubeVertexBuffer_ = CreateImmutableBuffer(device_.Get(), cubeVertices, D3D11_BIND_VERTEX_BUFFER, L"立方体の頂点バッファー作成");
    cubeIndexBuffer_ = CreateImmutableBuffer(device_.Get(), cubeIndices, D3D11_BIND_INDEX_BUFFER, L"立方体の索引バッファー作成");
    cubeIndexCount_ = static_cast<UINT>(cubeIndices.size());

    const std::vector<DemoVertex> edges = BuildCubeEdges(1.004f);
    cubeEdgeVertexBuffer_ = CreateImmutableBuffer(device_.Get(), edges, D3D11_BIND_VERTEX_BUFFER, L"立方体の辺の頂点バッファー作成");
    cubeEdgeVertexCount_ = static_cast<UINT>(edges.size());

    const std::vector<DemoVertex> boxEdges = BuildCubeEdges(1.0f);
    boxEdgeVertexBuffer_ = CreateImmutableBuffer(device_.Get(), boxEdges, D3D11_BIND_VERTEX_BUFFER, L"箱の辺の頂点バッファー作成");
    boxEdgeVertexCount_ = static_cast<UINT>(boxEdges.size());

    D3D11_QUERY_DESC queryDesc{};
    queryDesc.Query = D3D11_QUERY_EVENT;
    CheckHr(device_->CreateQuery(&queryDesc, &gpuDoneQuery_), L"GPU 完了確認用クエリの作成");
}

void Engine::WaitForGpu()
{
    ThrowIfDeviceLost();
    if (d3d9Device_) {
        // WPF へ渡す側（Direct3D 9Ex）だけが失われることもある
        const HRESULT state = d3d9Device_->CheckDeviceState(nullptr);
        if (state == D3DERR_DEVICEREMOVED || state == D3DERR_DEVICEHUNG) {
            lost_ = true;
            ThrowIfDeviceLost();
        }
    }
    ID3D11DeviceContext* ctx = context_.Get();
    ctx->End(gpuDoneQuery_.Get());
    ctx->Flush();

    const ULONGLONG start = GetTickCount64();
    for (;;) {
        const HRESULT hr = ctx->GetData(gpuDoneQuery_.Get(), nullptr, 0, 0);
        if (hr == S_OK) {
            return;
        }
        CheckHr(hr, L"GPU の描画完了の確認");
        if (GetTickCount64() - start > 2000) {
            throw Error(TT_E_GRAPHICS, L"GPU の描画完了を 2 秒待っても確認できませんでした");
        }
        SwitchToThread();
    }
}

IDirect3DDevice9Ex* Engine::D3D9Device()
{
    if (d3d9Device_) {
        return d3d9Device_.Get();
    }

    ComPtr<IDirect3D9Ex> d3d9;
    CheckHr(Direct3DCreate9Ex(D3D_SDK_VERSION, &d3d9), L"Direct3D 9Ex の初期化");

    // Direct3D 11 と同じ GPU でないと面を共有できない
    const LUID& target = adapterDesc_.AdapterLuid;
    UINT adapterIndex = UINT_MAX;
    for (UINT i = 0; i < d3d9->GetAdapterCount(); ++i) {
        LUID luid{};
        if (SUCCEEDED(d3d9->GetAdapterLUID(i, &luid)) && luid.LowPart == target.LowPart && luid.HighPart == target.HighPart) {
            adapterIndex = i;
            break;
        }
    }
    if (adapterIndex == UINT_MAX) {
        throw Error(TT_E_NOT_SUPPORTED,
            L"Direct3D 11 で使っている GPU が Direct3D 9Ex から見つかりません。ソフトウェア描画や、画面につながっていない GPU では D3DImage 方式を使えません");
    }

    D3DPRESENT_PARAMETERS pp{};
    pp.Windowed = TRUE;
    pp.SwapEffect = D3DSWAPEFFECT_DISCARD;
    pp.hDeviceWindow = GetDesktopWindow();
    pp.PresentationInterval = D3DPRESENT_INTERVAL_IMMEDIATE;
    pp.BackBufferFormat = D3DFMT_UNKNOWN;
    pp.BackBufferWidth = 1;
    pp.BackBufferHeight = 1;
    CheckHr(d3d9->CreateDeviceEx(adapterIndex, D3DDEVTYPE_HAL, pp.hDeviceWindow,
                D3DCREATE_HARDWARE_VERTEXPROCESSING | D3DCREATE_MULTITHREADED | D3DCREATE_FPU_PRESERVE, &pp, nullptr,
                &d3d9Device_),
        L"Direct3D 9Ex デバイスの作成");
    d3d9_ = d3d9;
    Log(TT_LOG_INFO, L"D3DImage 方式のために Direct3D 9Ex デバイスを作りました");
    return d3d9Device_.Get();
}

void Engine::FillInfo(TtEngineInfo& info) const
{
    const DXGI_ADAPTER_DESC1& desc = adapterDesc_; // 使えない間も、最後に使っていた GPU を答える
    info.feature_level = static_cast<uint32_t>(featureLevel_);
    info.is_software = (desc.Flags & DXGI_ADAPTER_FLAG_SOFTWARE) ? 1u : 0u;
    info.debug_layer_enabled = debugLayerEnabled_ ? 1u : 0u;
    info.dedicated_video_memory = desc.DedicatedVideoMemory;
    wcsncpy_s(info.adapter_name, desc.Description, _TRUNCATE);
}

void Engine::QueryVideoMemory(TtVideoMemoryInfo& info) const
{
    ThrowIfDeviceLost();
    ComPtr<IDXGIAdapter3> adapter3;
    if (!adapter_ || FAILED(adapter_.As(&adapter3))) {
        throw Error(TT_E_NOT_SUPPORTED, L"この環境では GPU メモリの使用量を取得できません");
    }
    DXGI_QUERY_VIDEO_MEMORY_INFO memory{};
    CheckHr(adapter3->QueryVideoMemoryInfo(0, DXGI_MEMORY_SEGMENT_GROUP_LOCAL, &memory), L"GPU メモリ使用量の取得");
    info.usage = memory.CurrentUsage;
    info.budget = memory.Budget;
}

void Engine::SetConstants(DirectX::FXMMATRIX worldViewProj)
{
    D3D11_MAPPED_SUBRESOURCE mapped{};
    CheckHr(context_->Map(constantBuffer_.Get(), 0, D3D11_MAP_WRITE_DISCARD, 0, &mapped), L"定数バッファーの更新");
    auto* constants = static_cast<FrameConstants*>(mapped.pData);
    DirectX::XMStoreFloat4x4(&constants->worldViewProj, worldViewProj);
    context_->Unmap(constantBuffer_.Get(), 0);
}

uint32_t Engine::SupportedSampleCount(uint32_t requested) const
{
    for (uint32_t count = requested; count > 1; count /= 2) {
        UINT colorLevels = 0;
        UINT depthLevels = 0;
        if (SUCCEEDED(device_->CheckMultisampleQualityLevels(kColorFormat, count, &colorLevels)) && colorLevels > 0
            && SUCCEEDED(device_->CheckMultisampleQualityLevels(kDepthFormat, count, &depthLevels)) && depthLevels > 0) {
            return count;
        }
    }
    return 1;
}

void Engine::DrawScene(ID3D11RenderTargetView* rtv, ID3D11DepthStencilView* dsv, int width, int height, const TtFrameParams& params,
    bool multisampled, ScenePart part)
{
    using namespace DirectX;

    auto load = [](const float (&m)[16]) { return XMLoadFloat4x4(reinterpret_cast<const XMFLOAT4X4*>(m)); };

    ID3D11DeviceContext* ctx = context_.Get();
    const XMMATRIX viewProj = load(params.view) * load(params.projection);
    basicState_ = multisampled ? multisampleLineState_.Get() : rasterizerState_.Get();
    const bool drawScene = part != ScenePart::Overlays;

    // 環境の画像（PBR の材質と背景で使う）
    currentEnvironment_ = params.environment ? reinterpret_cast<const Environment*>(params.environment)->View() : nullptr;

    // モデルの描き方（照明・カメラの位置・輪郭線の太さ・影）は、1 枚の中で共通
    ModelFrame frame{};
    XMStoreFloat4x4(&frame.view, load(params.view));
    XMStoreFloat4x4(&frame.viewProj, viewProj);
    XMVECTOR lightDirection = kLightDirection;
    XMVECTOR lightColor = XMVectorReplicate(kLightIntensity);
    if ((params.flags & TT_FRAME_USE_LIGHT) && IsFinite3(params.light_direction) && IsFinite3(params.light_color)) {
        const XMVECTOR direction = XMVectorSet(params.light_direction[0], params.light_direction[1], params.light_direction[2], 0.0f);
        if (XMVectorGetX(XMVector3LengthSq(direction)) > 1e-12f) {
            lightDirection = XMVector3Normalize(direction);
        }
        lightColor = XMVectorMax(XMVectorSet(params.light_color[0], params.light_color[1], params.light_color[2], 0.0f), XMVectorZero());
    }
    XMStoreFloat4(&frame.lightDirection, lightDirection);
    XMStoreFloat4(&frame.lightColor, XMVectorSetW(lightColor, 1.0f));
    XMVECTOR determinant;
    const XMMATRIX inverseView = XMMatrixInverse(&determinant, load(params.view));
    XMStoreFloat4(&frame.cameraPosition, XMVectorSetW(inverseView.r[3], 1.0f));
    // 材質の太さ 1 を edge_pixel_scale ピクセルにする（画面の座標は -1〜1 なので、ピクセルの 2 / 幅）
    const float edgePixels = std::isfinite(params.edge_pixel_scale) ? std::max(0.0f, params.edge_pixel_scale) : 0.0f;
    frame.edgeScale = {2.0f * edgePixels / static_cast<float>(width), 2.0f * edgePixels / static_cast<float>(height), 0.0f, 0.0f};
    XMStoreFloat4x4(&frame.lightViewProj, XMMatrixIdentity());
    frame.groundShadowColor = {params.ground_shadow_color[0], params.ground_shadow_color[1], params.ground_shadow_color[2],
        params.ground_shadow_color[3]};
    // レンダーパス（物の番号は、描く物ごとに下で入れる）
    const uint32_t pass = params.render_pass <= TT_PASS_EMISSION ? params.render_pass : TT_PASS_NONE;
    const float depthNear = std::isfinite(params.pass_depth_near) ? params.pass_depth_near : 0.0f;
    const float depthFar = std::isfinite(params.pass_depth_far) && params.pass_depth_far > depthNear ? params.pass_depth_far : depthNear + 1.0f;
    frame.passParams = {static_cast<float>(pass), 0.0f, depthNear, depthFar};
    if (drawScene) {
        WriteLightConstants(params);
    }
    if (drawScene && (params.flags & TT_FRAME_SELF_SHADOW) && params.item_count > 0) {
        RenderShadowMap(params, frame); // 影を落とすものが無ければ、セルフシャドウなしのまま
    }
    // 柔らかな影のときは、地面の影も照明から見た奥行きで描く（押しつぶす描き方では、境目をぼかせないため）
    ModelFrame groundFrame{};
    XMFLOAT4X4 floorWorld{};
    const bool softGroundShadow = drawScene && (params.flags & TT_FRAME_GROUND_SHADOW) && params.item_count > 0
        && ShadowSoftnessOf(params) > 0.0f && RenderGroundShadowMap(params, frame, groundFrame, floorWorld);

    ctx->OMSetRenderTargets(1, &rtv, dsv);
    const D3D11_VIEWPORT viewport{0.0f, 0.0f, static_cast<float>(width), static_cast<float>(height), 0.0f, 1.0f};
    ctx->RSSetViewports(1, &viewport);
    if (part == ScenePart::Scene) {
        const float transparent[4] = {0.0f, 0.0f, 0.0f, 0.0f}; // 背景は映像効果の仕上げで後ろに置く
        ctx->ClearRenderTargetView(rtv, transparent);
    } else if (part == ScenePart::All && (params.flags & TT_FRAME_CLEAR_COLOR)) {
        float clear[4];
        for (int i = 0; i < 4; ++i) {
            clear[i] = Sanitize(params.clear_color[i], 0.0f, 0.0f, 1.0f);
        }
        ctx->ClearRenderTargetView(rtv, clear);
    } else if (part == ScenePart::All) {
        ctx->ClearRenderTargetView(rtv, kClearColor);
    }
    if (drawScene) {
        ctx->ClearDepthStencilView(dsv, D3D11_CLEAR_DEPTH | D3D11_CLEAR_STENCIL, 1.0f, 0);
    }

    // 環境の画像を背景に描く（ほかの物はすべてこの上に描く）
    if (drawScene && (params.flags & TT_FRAME_ENVIRONMENT_BACKGROUND)) {
        DrawSky(params, viewProj);
    }

    SetBasicPipeline();
    const UINT stride = sizeof(DemoVertex);
    const UINT offset = 0;

    // 固定視点では、格子と座標軸をいちばん奥に描く（全部を描くときは奥行きを無視して先に描き、後から描くモデルを上に重ねる。
    // 映像効果を使うときは、場面の後に「何も描かれていない所」だけへ描く）
    const bool gridBehind = (params.flags & TT_FRAME_GRID_BEHIND) != 0;
    if (part == ScenePart::All) {
        DrawGridAndAxes(params, viewProj, gridBehind ? GridDepth::Ignore : GridDepth::Normal, width, height);
    }

    if (drawScene && (params.flags & TT_FRAME_SHOW_TEST_OBJECT)) {
        const XMMATRIX worldViewProj = load(params.test_object_world) * viewProj;
        SetConstants(worldViewProj);
        ctx->IASetPrimitiveTopology(D3D11_PRIMITIVE_TOPOLOGY_TRIANGLELIST);
        ctx->IASetVertexBuffers(0, 1, cubeVertexBuffer_.GetAddressOf(), &stride, &offset);
        ctx->IASetIndexBuffer(cubeIndexBuffer_.Get(), DXGI_FORMAT_R16_UINT, 0);
        ctx->DrawIndexed(cubeIndexCount_, 0, 0);
    }

    // 床の映り込みは、床より下に描く（地面の影とモデルは、その上に重なる）
    if (drawScene && (params.flags & TT_FRAME_FLOOR_REFLECTION) && params.item_count > 0) {
        DrawFloorReflections(params, frame);
    }

    // 地面の影は、モデルより先に床へ描く（モデルの奥行きで隠れる所は、後から描くモデルが上書きする）
    if (drawScene && (params.flags & TT_FRAME_GROUND_SHADOW) && params.item_count > 0) {
        if (softGroundShadow) {
            DrawSoftGroundShadow(groundFrame, floorWorld);
        } else {
            DrawGroundShadows(params, frame);
        }
    }

    // モデル（並び順に描く。半透明の材質があるので、MMD と同じく並び順をそのまま使う）
    for (uint32_t i = 0; drawScene && i < params.item_count; ++i) {
        const TtDrawItem& item = params.items[i];
        const auto& mesh = *reinterpret_cast<const Mesh*>(item.mesh);
        const XMMATRIX world = load(item.world);
        frame.passParams.y = static_cast<float>(i + 1); // レンダーパスの物の番号
        DrawMesh(mesh, world, frame);
        if (edgePixels > 0.0f && mesh.HasEdges() && !mesh.IsEmpty()) {
            DrawEdges(mesh);
        }
        if (part == ScenePart::All && (item.flags & TT_DRAW_SELECTED)) {
            SetBasicPipeline();
            DrawBox(mesh.BoundsMin(), mesh.BoundsMax(), world, viewProj);
        }
    }

    // 重ね描き（映像効果の後）: 格子と座標軸、選択の枠。奥行きは場面のものを使う
    if (part == ScenePart::Overlays) {
        SetBasicPipeline();
        DrawGridAndAxes(params, viewProj, gridBehind ? GridDepth::Farthest : GridDepth::Normal, width, height);
        for (uint32_t i = 0; i < params.item_count; ++i) {
            const TtDrawItem& item = params.items[i];
            if (item.flags & TT_DRAW_SELECTED) {
                const auto& mesh = *reinterpret_cast<const Mesh*>(item.mesh);
                DrawBox(mesh.BoundsMin(), mesh.BoundsMax(), load(item.world), viewProj);
            }
        }
    }
    if (part != ScenePart::Scene && (params.flags & TT_FRAME_SHOW_TEST_OBJECT) && (params.flags & TT_FRAME_TEST_OBJECT_SELECTED)) {
        SetBasicPipeline();
        SetConstants(load(params.test_object_world) * viewProj);
        ctx->IASetPrimitiveTopology(D3D11_PRIMITIVE_TOPOLOGY_LINELIST);
        ctx->IASetVertexBuffers(0, 1, cubeEdgeVertexBuffer_.GetAddressOf(), &stride, &offset);
        ctx->Draw(cubeEdgeVertexCount_, 0);
    }

    if (part != ScenePart::Scene && params.overlay_vertex_count >= 2) {
        SetBasicPipeline();
        DrawOverlayLines(params.overlay_lines, params.overlay_vertex_count - (params.overlay_vertex_count % 2), viewProj);
    }

    ctx->OMSetBlendState(nullptr, nullptr, 0xFFFFFFFF);
    ctx->OMSetRenderTargets(0, nullptr, nullptr);
}

void Engine::DrawGridAndAxes(const TtFrameParams& params, DirectX::CXMMATRIX viewProj, GridDepth depth, int width, int height)
{
    using namespace DirectX;
    ID3D11DeviceContext* ctx = context_.Get();
    const UINT stride = sizeof(DemoVertex);
    const UINT offset = 0;
    ctx->OMSetDepthStencilState(
        depth == GridDepth::Ignore ? overlayDepthState_.Get() : depth == GridDepth::Farthest ? farDepthState_.Get() : depthState_.Get(), 0);
    if (depth == GridDepth::Farthest) {
        // 奥行きを全部 1 にして、奥行き 1 のまま（何も描かれていない）所だけに描く
        const D3D11_VIEWPORT farthest{0.0f, 0.0f, static_cast<float>(width), static_cast<float>(height), 1.0f, 1.0f};
        ctx->RSSetViewports(1, &farthest);
    }
    ctx->IASetPrimitiveTopology(D3D11_PRIMITIVE_TOPOLOGY_LINELIST);
    if (params.flags & TT_FRAME_SHOW_GRID) {
        SetConstants(XMLoadFloat4x4(reinterpret_cast<const XMFLOAT4X4*>(params.grid_world)) * viewProj);
        ctx->IASetVertexBuffers(0, 1, gridVertexBuffer_.GetAddressOf(), &stride, &offset);
        ctx->Draw(gridVertexCount_, 0);
    }
    SetConstants(viewProj);
    ctx->IASetVertexBuffers(0, 1, axisVertexBuffer_.GetAddressOf(), &stride, &offset);
    const uint32_t axisFlags[] = {TT_FRAME_SHOW_AXIS_X, TT_FRAME_SHOW_AXIS_Y, TT_FRAME_SHOW_AXIS_Z};
    for (UINT axis = 0; axis < 3; ++axis) {
        if (params.flags & axisFlags[axis]) {
            ctx->Draw(2, axis * 2);
        }
    }
    ctx->OMSetDepthStencilState(depthState_.Get(), 0);
    if (depth == GridDepth::Farthest) {
        const D3D11_VIEWPORT viewport{0.0f, 0.0f, static_cast<float>(width), static_cast<float>(height), 0.0f, 1.0f};
        ctx->RSSetViewports(1, &viewport);
    }
}

bool Engine::UsesPostEffects(const TtFrameParams& params) noexcept
{
    return (params.post_flags & kKnownPostFlags) != 0;
}

void Engine::CreatePostSurface(PostSurface& surface, UINT width, UINT height, DXGI_FORMAT format)
{
    D3D11_TEXTURE2D_DESC desc{};
    desc.Width = std::max<UINT>(width, 1);
    desc.Height = std::max<UINT>(height, 1);
    desc.MipLevels = 1;
    desc.ArraySize = 1;
    desc.Format = format;
    desc.SampleDesc.Count = 1;
    desc.Usage = D3D11_USAGE_DEFAULT;
    desc.BindFlags = D3D11_BIND_RENDER_TARGET | D3D11_BIND_SHADER_RESOURCE;
    CheckHr(device_->CreateTexture2D(&desc, nullptr, &surface.texture), L"映像効果の画像の作成");
    CheckHr(device_->CreateRenderTargetView(surface.texture.Get(), nullptr, &surface.rtv), L"映像効果の描画先ビューの作成");
    CheckHr(device_->CreateShaderResourceView(surface.texture.Get(), nullptr, &surface.srv), L"映像効果の画像のビューの作成");
    surface.width = desc.Width;
    surface.height = desc.Height;
}

void Engine::ApplyPostEffects(const TtFrameParams& params, const PostInputs& inputs, ID3D11RenderTargetView* output, int width, int height)
{
    using namespace DirectX;
    ID3D11DeviceContext* ctx = context_.Get();
    uint32_t flags = params.post_flags & kKnownPostFlags;
    ID3D11ShaderResourceView* scene = inputs.scene;
    static const std::vector<PostSurface> kNoLevels;
    const std::vector<PostSurface>& bloomDown = inputs.bloomDown ? *inputs.bloomDown : kNoLevels;
    const std::vector<PostSurface>& bloomUp = inputs.bloomUp ? *inputs.bloomUp : kNoLevels;

    PostConstants constants{};
    const float threshold = Sanitize(params.bloom_threshold, 0.8f, 0.0f, 1.0f);
    const float intensity = Sanitize(params.bloom_intensity, 0.0f, 0.0f, 8.0f);
    constants.bloomParams = {threshold, std::max(threshold * 0.5f, 0.02f), Sanitize(params.bloom_spread, 0.7f, 0.0f, 1.0f), intensity};
    constants.gradeParams = {std::exp2(Sanitize(params.exposure, 0.0f, -5.0f, 5.0f)), Sanitize(params.contrast, 1.0f, 0.0f, 3.0f),
        Sanitize(params.saturation, 1.0f, 0.0f, 3.0f), Sanitize(params.vignette, 0.0f, 0.0f, 1.0f)};
    // 色温度: 黄色く（赤を強め、青を弱める）か、青く。明るさがあまり変わらないよう、両側へ少しずつ
    const float temperature = Sanitize(params.temperature, 0.0f, -1.0f, 1.0f);
    constants.whiteBalance = {1.0f + 0.2f * temperature, 1.0f, 1.0f - 0.2f * temperature, 0.0f};
    if (params.flags & TT_FRAME_CLEAR_COLOR) {
        constants.backgroundColor = {Sanitize(params.clear_color[0], 0.0f, 0.0f, 1.0f), Sanitize(params.clear_color[1], 0.0f, 0.0f, 1.0f),
            Sanitize(params.clear_color[2], 0.0f, 0.0f, 1.0f), Sanitize(params.clear_color[3], 0.0f, 0.0f, 1.0f)};
    } else {
        constants.backgroundColor = {kClearColor[0], kClearColor[1], kClearColor[2], kClearColor[3]};
    }

    // イラスト風: 線画の細かさ 0〜1 を、段差（奥行きの差の割合）と折れ目（曲がりの割合）のしきい値にする
    const float detail = Sanitize(params.line_detail, 0.5f, 0.0f, 1.0f);
    constants.lineParams = {Sanitize(params.line_width, 1.0f, 0.5f, 8.0f), Sanitize(params.line_strength, 1.0f, 0.0f, 1.0f),
        0.08f + (0.005f - 0.08f) * detail, 0.02f + (0.0015f - 0.02f) * detail};
    constants.lineColor = {Sanitize(params.line_color[0], 0.0f, 0.0f, 1.0f), Sanitize(params.line_color[1], 0.0f, 0.0f, 1.0f),
        Sanitize(params.line_color[2], 0.0f, 0.0f, 1.0f), 1.0f};
    constants.paintParams = {static_cast<float>(std::clamp<uint32_t>(params.posterize_levels, 2, 16)),
        Sanitize(params.shadow_tint_strength, 0.0f, 0.0f, 1.0f), Sanitize(params.paper_strength, 0.0f, 0.0f, 1.0f),
        Sanitize(params.paper_grain, 2.0f, 0.5f, 64.0f)};
    // 影の色は色合いだけを使う（明るさを 1 にそろえる。黒に近い色は、色合いが決まらないので灰色にする）
    XMVECTOR tint = XMVectorSet(Sanitize(params.shadow_tint[0], 0.5f, 0.0f, 1.0f), Sanitize(params.shadow_tint[1], 0.5f, 0.0f, 1.0f),
        Sanitize(params.shadow_tint[2], 0.5f, 0.0f, 1.0f), 0.0f);
    const float tintLuminance = XMVectorGetX(XMVector3Dot(tint, XMVectorSet(0.2126f, 0.7152f, 0.0722f, 0.0f)));
    tint = tintLuminance > 0.02f ? XMVectorScale(tint, 1.0f / tintLuminance) : XMVectorSet(1.0f, 1.0f, 1.0f, 0.0f);
    XMStoreFloat4(&constants.shadowTint, tint);
    // 奥行きを距離に直すための投影の値（行ベクトル × 行列の並び。(3,4) が 0 なら平行投影）
    constants.projectionParams = {params.projection[10], params.projection[14], params.projection[11] == 0.0f ? 1.0f : 0.0f, 0.0f};
    const float maxBlur = Sanitize(params.dof_max_blur, 0.0f, 0.0f, 64.0f);
    constants.dofParams = {Sanitize(params.dof_focus_distance, 45.0f, 0.0f, 1e6f), Sanitize(params.dof_focus_range, 0.0f, 0.0f, 1e6f) * 0.5f,
        maxBlur, Sanitize(params.dof_transition, 10.0f, 0.01f, 1e6f)};
    if (maxBlur < 0.5f) {
        flags &= ~static_cast<uint32_t>(TT_POST_DOF); // ぼけが無ければ何もしない
    }
    // 環境光の遮り: 画面での広がりの上限は画像の高さの 15%（大きすぎると、見る点がまばらになる）
    const float aoStrength = Sanitize(params.ao_strength, 0.0f, 0.0f, 2.0f);
    constants.aoParams = {aoStrength, Sanitize(params.ao_radius, 1.0f, 0.01f, 1e4f), std::max(static_cast<float>(height) * 0.15f, 2.0f), 0.0f};
    const bool orthographic = constants.projectionParams.z > 0.5f;
    constants.screenToView = {params.projection[0], params.projection[5], orthographic ? params.projection[12] : params.projection[8],
        orthographic ? params.projection[13] : params.projection[9]};
    const bool screenToViewValid = constants.screenToView.x != 0.0f && constants.screenToView.y != 0.0f;
    if (aoStrength <= 0.0f || !screenToViewValid) {
        flags &= ~static_cast<uint32_t>(TT_POST_AO);
    }
    // 霧: 高さは、見る向きの座標の位置にビュー行列の逆を掛けて出す（行ベクトル × 行列なので、2 列目）。
    // 平行投影（固定の視点）では、カメラからの距離に意味が無いので使わない
    const float fogDensity = Sanitize(params.fog_density, 0.0f, 0.0f, 1e3f);
    constants.fogColor = {Sanitize(params.fog_color[0], 0.7f, 0.0f, 1.0f), Sanitize(params.fog_color[1], 0.7f, 0.0f, 1.0f),
        Sanitize(params.fog_color[2], 0.7f, 0.0f, 1.0f), Sanitize(params.fog_start, 0.0f, 0.0f, 1e6f)};
    constants.fogParams = {fogDensity, Sanitize(params.fog_height, 0.0f, 0.0f, 1e6f), 0.0f, 0.0f};
    XMMATRIX view = XMLoadFloat4x4(reinterpret_cast<const XMFLOAT4X4*>(params.view));
    XMVECTOR determinant;
    XMFLOAT4X4 inverseView;
    XMStoreFloat4x4(&inverseView, XMMatrixInverse(&determinant, view));
    constants.fogHeightAxis = {inverseView._12, inverseView._22, inverseView._32, inverseView._42};
    if (fogDensity <= 0.0f || orthographic || !screenToViewValid || XMVectorGetX(determinant) == 0.0f) {
        flags &= ~static_cast<uint32_t>(TT_POST_FOG);
    }

    ctx->OMSetBlendState(nullptr, nullptr, 0xFFFFFFFF);
    ctx->OMSetDepthStencilState(overlayDepthState_.Get(), 0);
    ctx->RSSetState(rasterizerState_.Get());
    ctx->IASetInputLayout(nullptr);
    ctx->IASetPrimitiveTopology(D3D11_PRIMITIVE_TOPOLOGY_TRIANGLELIST);
    ctx->VSSetShader(postVertexShader_.Get(), nullptr, 0);
    ctx->PSSetConstantBuffers(0, 1, postConstantBuffer_.GetAddressOf());
    ctx->PSSetSamplers(0, 1, clampSampler_.GetAddressOf());

    ID3D11ShaderResourceView* const none[4] = {nullptr, nullptr, nullptr, nullptr};
    ID3D11ShaderResourceView* third = nullptr; // 線画の準備（見る向きの距離）
    ID3D11ShaderResourceView* fourth = nullptr; // 場面の奥行き（線画の準備のときだけ）
    auto pass = [&](ID3D11PixelShader* shader, ID3D11RenderTargetView* target, UINT w, UINT h, ID3D11ShaderResourceView* first,
                    ID3D11ShaderResourceView* second, UINT sourceWidth, UINT sourceHeight) {
        ctx->PSSetShaderResources(0, 4, none); // 前の段で書いた画像を、読む前に描画先から外す
        ctx->OMSetRenderTargets(1, &target, nullptr);
        const D3D11_VIEWPORT viewport{0.0f, 0.0f, static_cast<float>(w), static_cast<float>(h), 0.0f, 1.0f};
        ctx->RSSetViewports(1, &viewport);
        constants.sourceTexel = {1.0f / static_cast<float>(std::max<UINT>(sourceWidth, 1)),
            1.0f / static_cast<float>(std::max<UINT>(sourceHeight, 1)), static_cast<float>(w), static_cast<float>(h)};
        constants.flags[0] = flags;
        D3D11_MAPPED_SUBRESOURCE mapped{};
        CheckHr(ctx->Map(postConstantBuffer_.Get(), 0, D3D11_MAP_WRITE_DISCARD, 0, &mapped), L"映像効果の定数の更新");
        std::memcpy(mapped.pData, &constants, sizeof(constants));
        ctx->Unmap(postConstantBuffer_.Get(), 0);
        ID3D11ShaderResourceView* const sources[4] = {first, second, third, fourth};
        ctx->PSSetShaderResources(0, 4, sources);
        ctx->PSSetShader(shader, nullptr, 0);
        ctx->Draw(3, 0);
    };

    // 線画・被写界深度・環境光の遮りの準備: 奥行きを見る向きの距離に直す
    constexpr uint32_t kDepthEffects = TT_POST_LINES | TT_POST_DOF | TT_POST_AO | TT_POST_FOG;
    if ((flags & kDepthEffects) && inputs.depth && inputs.linearDepth && inputs.linearDepth->rtv) {
        fourth = inputs.depth;
        pass(inputs.depthMultisampled ? linearDepthMsShader_.Get() : linearDepthShader_.Get(), inputs.linearDepth->rtv.Get(),
            static_cast<UINT>(width), static_cast<UINT>(height), nullptr, nullptr, static_cast<UINT>(width), static_cast<UINT>(height));
        fourth = nullptr;
        third = inputs.linearDepth->srv.Get();
    } else {
        flags &= ~kDepthEffects;
    }

    // 環境光の遮り: 光の届く割合を求め、横と縦にならして場面に掛ける（被写界深度でぼかす前に）
    if ((flags & TT_POST_AO) && inputs.ao && inputs.ao->rtv && inputs.aoBlur && inputs.aoBlur->rtv && inputs.aoScene && inputs.aoScene->rtv) {
        pass(aoShader_.Get(), inputs.ao->rtv.Get(), static_cast<UINT>(width), static_cast<UINT>(height), nullptr, nullptr,
            static_cast<UINT>(width), static_cast<UINT>(height));
        pass(aoBlurShader_.Get(), inputs.aoBlur->rtv.Get(), static_cast<UINT>(width), static_cast<UINT>(height), inputs.ao->srv.Get(),
            nullptr, static_cast<UINT>(width), static_cast<UINT>(height));
        pass(aoApplyShader_.Get(), inputs.aoScene->rtv.Get(), static_cast<UINT>(width), static_cast<UINT>(height), scene,
            inputs.aoBlur->srv.Get(), static_cast<UINT>(width), static_cast<UINT>(height));
        scene = inputs.aoScene->srv.Get();
    }

    // 霧: 遠くの物ほど霧の色に溶けこませる（被写界深度でぼかす前に。遠くの霧もぼける）
    if ((flags & TT_POST_FOG) && inputs.fog && inputs.fog->rtv) {
        pass(fogShader_.Get(), inputs.fog->rtv.Get(), static_cast<UINT>(width), static_cast<UINT>(height), scene, nullptr,
            static_cast<UINT>(width), static_cast<UINT>(height));
        scene = inputs.fog->srv.Get();
    }

    // 被写界深度: 場面をぼかしてから、そのあとの効果（ブルーム・仕上げ）にかける
    if ((flags & TT_POST_DOF) && inputs.dof && inputs.dof->rtv) {
        pass(dofShader_.Get(), inputs.dof->rtv.Get(), static_cast<UINT>(width), static_cast<UINT>(height), scene, nullptr,
            static_cast<UINT>(width), static_cast<UINT>(height));
        scene = inputs.dof->srv.Get();
    }

    ID3D11ShaderResourceView* bloom = nullptr;
    if ((flags & TT_POST_BLOOM) && intensity > 0.0f && bloomDown.size() >= 2 && bloomUp.size() + 1 == bloomDown.size()) {
        // 明るい所を取り出して縮め、さらに半分ずつ縮めてから、小さい段から順に広げて混ぜ直す
        pass(bloomPrefilterShader_.Get(), bloomDown[0].rtv.Get(), bloomDown[0].width, bloomDown[0].height, scene, nullptr,
            static_cast<UINT>(width), static_cast<UINT>(height));
        for (size_t i = 1; i < bloomDown.size(); ++i) {
            pass(bloomDownShader_.Get(), bloomDown[i].rtv.Get(), bloomDown[i].width, bloomDown[i].height, bloomDown[i - 1].srv.Get(),
                nullptr, bloomDown[i - 1].width, bloomDown[i - 1].height);
        }
        const PostSurface* lower = &bloomDown.back();
        for (size_t i = bloomUp.size(); i-- > 0;) {
            pass(bloomUpShader_.Get(), bloomUp[i].rtv.Get(), bloomUp[i].width, bloomUp[i].height, bloomDown[i].srv.Get(), lower->srv.Get(),
                lower->width, lower->height);
            lower = &bloomUp[i];
        }
        bloom = bloomUp[0].srv.Get();
    } else {
        flags &= ~static_cast<uint32_t>(TT_POST_BLOOM);
    }
    pass(postCompositeShader_.Get(), output, static_cast<UINT>(width), static_cast<UINT>(height), scene, bloom,
        bloom ? bloomDown[0].width : 1, bloom ? bloomDown[0].height : 1);

    ctx->PSSetShaderResources(0, 4, none);
    ctx->OMSetRenderTargets(0, nullptr, nullptr);
}

void Engine::CreatePostResources()
{
    D3D11_DEPTH_STENCIL_DESC farthest{};
    farthest.DepthEnable = TRUE;
    farthest.DepthWriteMask = D3D11_DEPTH_WRITE_MASK_ZERO;
    farthest.DepthFunc = D3D11_COMPARISON_LESS_EQUAL;
    CheckHr(device_->CreateDepthStencilState(&farthest, &farDepthState_), L"いちばん奥に描く深度設定の作成");

    CheckHr(device_->CreateVertexShader(g_PostVS, sizeof(g_PostVS), nullptr, &postVertexShader_), L"映像効果の頂点シェーダーの作成");
    CheckHr(device_->CreatePixelShader(g_BloomPrefilterPS, sizeof(g_BloomPrefilterPS), nullptr, &bloomPrefilterShader_),
        L"ブルームの取り出しのシェーダーの作成");
    CheckHr(device_->CreatePixelShader(g_BloomDownPS, sizeof(g_BloomDownPS), nullptr, &bloomDownShader_), L"ブルームの縮小のシェーダーの作成");
    CheckHr(device_->CreatePixelShader(g_BloomUpPS, sizeof(g_BloomUpPS), nullptr, &bloomUpShader_), L"ブルームの拡大のシェーダーの作成");
    CheckHr(device_->CreatePixelShader(g_PostCompositePS, sizeof(g_PostCompositePS), nullptr, &postCompositeShader_),
        L"映像効果の仕上げのシェーダーの作成");
    CheckHr(device_->CreatePixelShader(g_LinearDepthPS, sizeof(g_LinearDepthPS), nullptr, &linearDepthShader_),
        L"線画の準備のシェーダーの作成");
    CheckHr(device_->CreatePixelShader(g_LinearDepthMsPS, sizeof(g_LinearDepthMsPS), nullptr, &linearDepthMsShader_),
        L"線画の準備（MSAA）のシェーダーの作成");
    CheckHr(device_->CreatePixelShader(g_DofPS, sizeof(g_DofPS), nullptr, &dofShader_), L"被写界深度のシェーダーの作成");
    CheckHr(device_->CreatePixelShader(g_AoPS, sizeof(g_AoPS), nullptr, &aoShader_), L"環境光の遮りのシェーダーの作成");
    CheckHr(device_->CreatePixelShader(g_AoBlurPS, sizeof(g_AoBlurPS), nullptr, &aoBlurShader_), L"環境光の遮りをならすシェーダーの作成");
    CheckHr(device_->CreatePixelShader(g_FogPS, sizeof(g_FogPS), nullptr, &fogShader_), L"霧のシェーダーの作成");
    CheckHr(device_->CreatePixelShader(g_SkyPS, sizeof(g_SkyPS), nullptr, &skyPixelShader_), L"背景（環境の画像）のシェーダーの作成");
    CheckHr(device_->CreatePixelShader(g_AoApplyPS, sizeof(g_AoApplyPS), nullptr, &aoApplyShader_), L"環境光の遮りをかけるシェーダーの作成");

    D3D11_BUFFER_DESC desc{};
    desc.ByteWidth = sizeof(PostConstants);
    desc.Usage = D3D11_USAGE_DYNAMIC;
    desc.BindFlags = D3D11_BIND_CONSTANT_BUFFER;
    desc.CPUAccessFlags = D3D11_CPU_ACCESS_WRITE;
    CheckHr(device_->CreateBuffer(&desc, nullptr, &postConstantBuffer_), L"映像効果の定数バッファーの作成");
    desc.ByteWidth = sizeof(SkyConstants);
    CheckHr(device_->CreateBuffer(&desc, nullptr, &skyConstantBuffer_), L"背景の定数バッファーの作成");
}

void Engine::SetBasicPipeline()
{
    ID3D11DeviceContext* ctx = context_.Get();
    ctx->IASetInputLayout(inputLayout_.Get());
    ctx->VSSetShader(vertexShader_.Get(), nullptr, 0);
    ctx->VSSetConstantBuffers(0, 1, constantBuffer_.GetAddressOf());
    ctx->PSSetShader(pixelShader_.Get(), nullptr, 0);
    ctx->RSSetState(basicState_);
    ctx->OMSetDepthStencilState(depthState_.Get(), 0);
    ctx->OMSetBlendState(nullptr, nullptr, 0xFFFFFFFF);
}

void Engine::WriteObjectConstants(DirectX::FXMMATRIX world, DirectX::CXMMATRIX worldViewProj, const ModelFrame& frame)
{
    using namespace DirectX;
    ID3D11DeviceContext* ctx = context_.Get();
    D3D11_MAPPED_SUBRESOURCE mapped{};
    CheckHr(ctx->Map(objectConstantBuffer_.Get(), 0, D3D11_MAP_WRITE_DISCARD, 0, &mapped), L"モデルの定数バッファーの更新");
    auto* object = static_cast<ObjectConstants*>(mapped.pData);
    XMStoreFloat4x4(&object->worldViewProj, worldViewProj);
    XMStoreFloat4x4(&object->world, world);
    object->view = frame.view;
    object->lightDirection = frame.lightDirection;
    object->lightColor = frame.lightColor;
    object->cameraPosition = frame.cameraPosition;
    object->edgeScale = frame.edgeScale;
    object->lightViewProj = frame.lightViewProj;
    object->shadowParams = frame.shadowParams;
    object->groundShadowColor = frame.groundShadowColor;
    object->reflectionParams = frame.reflectionParams;
    object->shadowSoftness = frame.shadowSoftness;
    object->passParams = frame.passParams;
    ctx->Unmap(objectConstantBuffer_.Get(), 0);
}

void Engine::DrawFloorReflections(const TtFrameParams& params, const ModelFrame& frame)
{
    using namespace DirectX;
    const float strength = std::isfinite(params.reflection_strength) ? std::clamp(params.reflection_strength, 0.0f, 1.0f) : 0.0f;
    const float fade = std::isfinite(params.reflection_fade) ? std::max(params.reflection_fade, 0.01f) : 10.0f;
    if (strength <= 0.0f || frame.cameraPosition.y <= 0.0f) {
        return; // 床より下から見るときは、床の裏なので映さない
    }
    // 照明・カメラの位置・セルフシャドウも床で上下に映す。映した点を、映した照明で照らせば、もとの点と同じ見え方になる
    const XMMATRIX mirror = XMMatrixScaling(1.0f, -1.0f, 1.0f);
    ModelFrame mirrored = frame;
    mirrored.lightDirection.y = -frame.lightDirection.y;
    mirrored.cameraPosition.y = -frame.cameraPosition.y;
    XMStoreFloat4x4(&mirrored.lightViewProj, mirror * XMLoadFloat4x4(&frame.lightViewProj));
    mirrored.reflectionParams = {1.0f, strength, fade, 0.0f};

    WriteLightConstants(params, true);
    drawingReflection_ = true;
    auto load = [](const float (&m)[16]) { return XMLoadFloat4x4(reinterpret_cast<const XMFLOAT4X4*>(m)); };
    for (uint32_t i = 0; i < params.item_count; ++i) {
        const auto& mesh = *reinterpret_cast<const Mesh*>(params.items[i].mesh);
        DrawMesh(mesh, load(params.items[i].world) * mirror, mirrored);
    }
    drawingReflection_ = false;
    WriteLightConstants(params, false);
}

void Engine::WriteLightConstants(const TtFrameParams& params, bool mirrored)
{
    using namespace DirectX;
    LightConstants constants{};
    uint32_t count = 0;
    for (uint32_t i = 0; i < params.light_count && count < TT_MAX_LIGHTS; ++i) {
        const TtLight& light = params.lights[i];
        if (light.type > TT_LIGHT_SPOT || !IsFinite3(light.position) || !IsFinite3(light.direction) || !IsFinite3(light.color)
            || !std::isfinite(light.range) || !std::isfinite(light.spot_angle) || !std::isfinite(light.spot_softness)) {
            continue; // 使えない照明は飛ばす
        }
        XMVECTOR direction = XMVectorSet(light.direction[0], light.direction[1], light.direction[2], 0.0f);
        if (light.type != TT_LIGHT_POINT) {
            if (XMVectorGetX(XMVector3LengthSq(direction)) < 1e-12f) {
                continue;
            }
            direction = XMVector3Normalize(direction);
        }
        if (mirrored) {
            direction = XMVectorMultiply(direction, XMVectorSet(1.0f, -1.0f, 1.0f, 1.0f));
        }
        LightConstants::Entry& entry = constants.lights[count++];
        entry.position = {light.position[0], mirrored ? -light.position[1] : light.position[1], light.position[2],
            static_cast<float>(light.type)};
        XMStoreFloat4(&entry.direction, XMVectorSetW(direction, std::max(light.range, 0.01f)));
        const float half = XMConvertToRadians(std::clamp(light.spot_angle, 1.0f, 179.0f) * 0.5f);
        const float softness = std::clamp(light.spot_softness, 0.0f, 1.0f);
        entry.color = {std::max(light.color[0], 0.0f), std::max(light.color[1], 0.0f), std::max(light.color[2], 0.0f), std::cos(half)};
        // 内側の角度で 1、外側の角度で 0。ぼかしが 0 でも境目に少しだけ幅を残す（ギザギザにならないように）
        entry.spot = {std::cos(half * (1.0f - std::max(softness, 0.02f))), 0.0f, 0.0f, 0.0f};
    }
    constants.count[0] = count;
    // 環境の画像（床の映り込みでは、シェーダーで景色も上下に映して読む）
    if (params.environment) {
        const auto& environment = *reinterpret_cast<const Environment*>(params.environment);
        if (environment.View()) {
            constants.environment = {1.0f, Sanitize(params.environment_intensity, 1.0f, 0.0f, 100.0f),
                Sanitize(params.environment_rotation, 0.0f, -1e4f, 1e4f), environment.MipCount()};
            for (int k = 0; k < 9; ++k) {
                constants.environmentIrradiance[k] = environment.Irradiance()[k];
            }
        }
    }
    D3D11_MAPPED_SUBRESOURCE mapped{};
    CheckHr(context_->Map(lightConstantBuffer_.Get(), 0, D3D11_MAP_WRITE_DISCARD, 0, &mapped), L"足す照明の定数バッファーの更新");
    std::memcpy(mapped.pData, &constants, sizeof(constants));
    context_->Unmap(lightConstantBuffer_.Get(), 0);
}

void Engine::BindMesh(const Mesh& mesh)
{
    ID3D11DeviceContext* ctx = context_.Get();
    const UINT strides[] = {sizeof(TtMeshVertex), sizeof(float)};
    const UINT offsets[] = {0, 0};
    ID3D11Buffer* vertexBuffers[] = {mesh.VertexBuffer(), mesh.EdgeScaleBuffer()};
    ctx->IASetInputLayout(modelInputLayout_.Get());
    ctx->IASetPrimitiveTopology(D3D11_PRIMITIVE_TOPOLOGY_TRIANGLELIST);
    ctx->IASetVertexBuffers(0, 2, vertexBuffers, strides, offsets);
    ctx->IASetIndexBuffer(mesh.IndexBuffer(), DXGI_FORMAT_R32_UINT, 0);
    ID3D11Buffer* constantBuffers[] = {objectConstantBuffer_.Get(), materialConstantBuffer_.Get(), lightConstantBuffer_.Get()};
    ctx->VSSetConstantBuffers(0, 3, constantBuffers);
    ctx->PSSetConstantBuffers(0, 3, constantBuffers);
}

void Engine::DrawMesh(const Mesh& mesh, DirectX::FXMMATRIX world, const ModelFrame& frame)
{
    using namespace DirectX;
    if (mesh.IsEmpty()) {
        return;
    }
    ID3D11DeviceContext* ctx = context_.Get();
    WriteObjectConstants(world, world * XMLoadFloat4x4(&frame.viewProj), frame);
    BindMesh(mesh);
    ctx->VSSetShader(modelVertexShader_.Get(), nullptr, 0);
    ctx->PSSetShader(modelPixelShader_.Get(), nullptr, 0);
    ID3D11SamplerState* samplers[] = {textureSampler_.Get(), clampSampler_.Get(), shadowSampler_.Get(), environmentSampler_.Get()};
    ctx->PSSetSamplers(0, 4, samplers);
    ctx->OMSetDepthStencilState(modelDepthState_.Get(), 0);
    ctx->OMSetBlendState(alphaBlendState_.Get(), nullptr, 0xFFFFFFFF);
    const bool selfShadow = frame.shadowParams.z > 0.5f;
    ID3D11ShaderResourceView* shadowMap = selfShadow && shadowMapSrv_ ? shadowMapSrv_.Get() : noShadowMap_.Get();
    ctx->PSSetShaderResources(3, 1, &shadowMap);
    ctx->PSSetShaderResources(4, 1, &currentEnvironment_);
    D3D11_MAPPED_SUBRESOURCE mapped{};

    auto view = [&](const std::shared_ptr<TextureImage>& image) {
        return image && image->View() ? image->View() : whiteTexture_.Get();
    };
    uint32_t materialIndex = 0;
    for (const Mesh::Material& material : mesh.Materials()) {
        const float index = static_cast<float>(materialIndex++); // 材質の番号（描かない材質も数える。モデルの材質の並びと同じ）
        if (material.indexCount == 0) {
            continue;
        }
        CheckHr(ctx->Map(materialConstantBuffer_.Get(), 0, D3D11_MAP_WRITE_DISCARD, 0, &mapped), L"材質の定数バッファーの更新");
        auto* constants = static_cast<MaterialConstants*>(mapped.pData);
        constants->diffuse = material.diffuse;
        constants->ambientAndTexture = {material.ambient.x, material.ambient.y, material.ambient.z, material.texture ? 1.0f : 0.0f};
        constants->specularAndPower = {material.specular.x, material.specular.y, material.specular.z, material.specularPower};
        constants->textureMultiply = material.textureMultiply;
        constants->textureAdd = material.textureAdd;
        constants->sphereMultiply = material.sphereMultiply;
        constants->sphereAdd = material.sphereAdd;
        constants->toonMultiply = material.toonMultiply;
        constants->toonAdd = material.toonAdd;
        constants->edgeColor = material.edgeColor;
        constants->options = {static_cast<float>(material.sphere ? material.sphereMode : TT_SPHERE_NONE),
            material.toon ? 1.0f : 0.0f, material.edgeSize, selfShadow && material.receiveShadow ? 1.0f : 0.0f};
        constants->pbr = {material.shading == TT_SHADING_PBR ? 1.0f : 0.0f, material.metallic, material.roughness, 0.0f};
        constants->baseTint = material.baseTint;
        constants->emissive = material.emissive;
        constants->materialIds = {index, 0.0f, 0.0f, 0.0f};
        ctx->Unmap(materialConstantBuffer_.Get(), 0);

        ID3D11ShaderResourceView* textures[] = {view(material.texture), view(material.toon), view(material.sphere)};
        ctx->PSSetShaderResources(0, 3, textures);
        ctx->RSSetState(material.doubleSided ? cullNoneState_.Get() : drawingReflection_ ? mirrorCullState_.Get() : cullBackState_.Get());
        ctx->DrawIndexed(material.indexCount, material.indexStart, 0);
    }

    ID3D11ShaderResourceView* none[4] = {};
    ctx->PSSetShaderResources(0, 4, none);
}

void Engine::DrawEdges(const Mesh& mesh)
{
    // DrawMesh の直後に呼ぶ（頂点・定数・合成の設定はそのまま使う）。MMD と同じく、モデルの材質を全部描いた後に描く
    ID3D11DeviceContext* ctx = context_.Get();
    ctx->VSSetShader(edgeVertexShader_.Get(), nullptr, 0);
    ctx->PSSetShader(edgePixelShader_.Get(), nullptr, 0);
    ctx->RSSetState(cullFrontState_.Get());
    for (const Mesh::Material& material : mesh.Materials()) {
        // 消した（不透明度 0 の）材質の線は描かない
        if (!material.edge || material.indexCount == 0 || material.edgeSize <= 0.0f || material.diffuse.w <= 0.0f
            || material.edgeColor.w <= 0.0f) {
            continue;
        }
        D3D11_MAPPED_SUBRESOURCE mapped{};
        CheckHr(ctx->Map(materialConstantBuffer_.Get(), 0, D3D11_MAP_WRITE_DISCARD, 0, &mapped), L"輪郭線の定数バッファーの更新");
        auto* constants = static_cast<MaterialConstants*>(mapped.pData);
        *constants = MaterialConstants{};
        constants->diffuse = material.diffuse;
        constants->edgeColor = material.edgeColor;
        constants->options = {0.0f, 0.0f, material.edgeSize, 0.0f};
        ctx->Unmap(materialConstantBuffer_.Get(), 0);
        ctx->DrawIndexed(material.indexCount, material.indexStart, 0);
    }
}

void Engine::EnsureShadowMap(UINT size)
{
    EnsureDepthMap(size, shadowMapDsv_, shadowMapSrv_, shadowMapSize_, L"セルフシャドウ");
}

void Engine::EnsureDepthMap(UINT size, ComPtr<ID3D11DepthStencilView>& dsv, ComPtr<ID3D11ShaderResourceView>& srv, UINT& current,
    const wchar_t* what)
{
    if (current == size && dsv && srv) {
        return;
    }
    srv.Reset();
    dsv.Reset();
    current = 0;
    D3D11_TEXTURE2D_DESC desc{};
    desc.Width = size;
    desc.Height = size;
    desc.MipLevels = 1;
    desc.ArraySize = 1;
    desc.Format = DXGI_FORMAT_R32_TYPELESS; // 奥行きとして書き、数として読む
    desc.SampleDesc.Count = 1;
    desc.Usage = D3D11_USAGE_DEFAULT;
    desc.BindFlags = D3D11_BIND_DEPTH_STENCIL | D3D11_BIND_SHADER_RESOURCE;
    ComPtr<ID3D11Texture2D> texture;
    CheckHr(device_->CreateTexture2D(&desc, nullptr, &texture), (std::wstring(what) + L"の奥行きの画像の作成").c_str());
    D3D11_DEPTH_STENCIL_VIEW_DESC dsvDesc{};
    dsvDesc.Format = DXGI_FORMAT_D32_FLOAT;
    dsvDesc.ViewDimension = D3D11_DSV_DIMENSION_TEXTURE2D;
    CheckHr(device_->CreateDepthStencilView(texture.Get(), &dsvDesc, &dsv), (std::wstring(what) + L"の奥行きの書き込み先の作成").c_str());
    D3D11_SHADER_RESOURCE_VIEW_DESC srvDesc{};
    srvDesc.Format = DXGI_FORMAT_R32_FLOAT;
    srvDesc.ViewDimension = D3D11_SRV_DIMENSION_TEXTURE2D;
    srvDesc.Texture2D.MipLevels = 1;
    CheckHr(device_->CreateShaderResourceView(texture.Get(), &srvDesc, &srv), (std::wstring(what) + L"の奥行きの読み出しの作成").c_str());
    current = size;
}

void Engine::DrawLightDepth(const TtFrameParams& params, DirectX::CXMMATRIX lightViewProj, const ModelFrame& frame,
    ID3D11DepthStencilView* dsv, UINT size, bool groundCasters)
{
    using namespace DirectX;
    auto load = [](const float (&m)[16]) { return XMLoadFloat4x4(reinterpret_cast<const XMFLOAT4X4*>(m)); };
    ID3D11DeviceContext* ctx = context_.Get();
    ID3D11ShaderResourceView* none = nullptr;
    ctx->PSSetShaderResources(3, 1, &none); // 前の描画で読んでいた画像には書けないので外す
    ctx->OMSetRenderTargets(0, nullptr, dsv);
    const D3D11_VIEWPORT viewport{0.0f, 0.0f, static_cast<float>(size), static_cast<float>(size), 0.0f, 1.0f};
    ctx->RSSetViewports(1, &viewport);
    ctx->ClearDepthStencilView(dsv, D3D11_CLEAR_DEPTH, 1.0f, 0);
    ctx->VSSetShader(shadowVertexShader_.Get(), nullptr, 0);
    ctx->PSSetShader(nullptr, nullptr, 0); // 奥行きだけを書く
    ctx->RSSetState(shadowCasterState_.Get());
    ctx->OMSetDepthStencilState(modelDepthState_.Get(), 0);
    ctx->OMSetBlendState(nullptr, nullptr, 0xFFFFFFFF);

    for (uint32_t i = 0; i < params.item_count; ++i) {
        const auto& mesh = *reinterpret_cast<const Mesh*>(params.items[i].mesh);
        if (mesh.IsEmpty() || !(groundCasters ? mesh.HasGroundShadow() : mesh.CastsShadow())) {
            continue;
        }
        const XMMATRIX world = load(params.items[i].world);
        WriteObjectConstants(world, world * lightViewProj, frame);
        BindMesh(mesh);
        for (const Mesh::Material& material : mesh.Materials()) {
            const bool casts = groundCasters ? material.groundShadow : material.castShadow;
            if (casts && material.indexCount > 0 && material.diffuse.w > 0.0f) {
                ctx->DrawIndexed(material.indexCount, material.indexStart, 0);
            }
        }
    }
    ctx->OMSetRenderTargets(0, nullptr, nullptr);
}

bool Engine::RenderShadowMap(const TtFrameParams& params, ModelFrame& frame)
{
    using namespace DirectX;
    auto load = [](const float (&m)[16]) { return XMLoadFloat4x4(reinterpret_cast<const XMFLOAT4X4*>(m)); };

    // 照明から見た向き。真上・真下から照らすときは、上の向きを Z にする
    const XMVECTOR direction = XMLoadFloat4(&frame.lightDirection);
    const XMVECTOR up = std::abs(XMVectorGetY(direction)) > 0.99f ? XMVectorSet(0, 0, 1, 0) : XMVectorSet(0, 1, 0, 0);
    const XMMATRIX lightView = XMMatrixLookToLH(XMVectorZero(), direction, up);

    // 照明から見た縦横の範囲は、影を落とすモデルの囲みの箱が全部入るように（影はその外へは落ちない）。
    // 奥行きの範囲は、影を受けるモデルも入るように（影を落とすものより照明から遠い床なども、範囲の外にしない）
    XMVECTOR lo = XMVectorReplicate(FLT_MAX);
    XMVECTOR hi = XMVectorReplicate(-FLT_MAX);
    float nearZ = FLT_MAX;
    float farZ = -FLT_MAX;
    bool any = false;
    for (uint32_t i = 0; i < params.item_count; ++i) {
        const auto& mesh = *reinterpret_cast<const Mesh*>(params.items[i].mesh);
        if (mesh.IsEmpty()) {
            continue;
        }
        const XMMATRIX toLight = load(params.items[i].world) * lightView;
        const XMFLOAT3& a = mesh.BoundsMin();
        const XMFLOAT3& b = mesh.BoundsMax();
        for (int corner = 0; corner < 8; ++corner) {
            const XMVECTOR p = XMVector3Transform(
                XMVectorSet((corner & 1) ? b.x : a.x, (corner & 2) ? b.y : a.y, (corner & 4) ? b.z : a.z, 1.0f), toLight);
            nearZ = std::min(nearZ, XMVectorGetZ(p));
            farZ = std::max(farZ, XMVectorGetZ(p));
            if (mesh.CastsShadow()) {
                lo = XMVectorMin(lo, p);
                hi = XMVectorMax(hi, p);
                any = true;
            }
        }
    }
    if (!any) {
        return false;
    }
    XMFLOAT3 l;
    XMFLOAT3 h;
    XMStoreFloat3(&l, lo);
    XMStoreFloat3(&h, hi);
    l.z = nearZ;
    h.z = farZ;
    const float pad = std::max({h.x - l.x, h.y - l.y, h.z - l.z}) * 0.02f + 0.01f;
    const float depthRange = (h.z - l.z) + 2.0f * pad;
    const XMMATRIX lightProj = XMMatrixOrthographicOffCenterLH(l.x - pad, h.x + pad, l.y - pad, h.y + pad, l.z - pad, h.z + pad);
    const XMMATRIX lightViewProj = lightView * lightProj;

    const UINT size = params.shadow_map_size == 0 ? kDefaultShadowMapSize : std::clamp<UINT>(params.shadow_map_size, 256, 8192);
    EnsureShadowMap(size);
    DrawLightDepth(params, lightViewProj, frame, shadowMapDsv_.Get(), size, false);

    XMStoreFloat4x4(&frame.lightViewProj, lightViewProj);
    frame.shadowParams = {1.0f / static_cast<float>(size), kShadowBiasWorld / std::max(depthRange, 1e-3f), 1.0f, 0.0f};
    // 柔らかな影: 奥行きの画像の奥行き 1 と、座標 1 が、ワールドでどれだけの長さか
    frame.shadowSoftness = {depthRange, ShadowSoftnessOf(params), 1.0f / std::max(h.x - l.x + 2.0f * pad, 1e-3f),
        1.0f / std::max(h.y - l.y + 2.0f * pad, 1e-3f)};
    return true;
}

bool Engine::RenderGroundShadowMap(const TtFrameParams& params, const ModelFrame& frame, ModelFrame& groundFrame,
    DirectX::XMFLOAT4X4& floorWorld)
{
    using namespace DirectX;
    auto load = [](const float (&m)[16]) { return XMLoadFloat4x4(reinterpret_cast<const XMFLOAT4X4*>(m)); };
    // 下から・真横から照らすときは、床に影を落とさない（押しつぶす描き方と同じ）
    const XMVECTOR direction = XMLoadFloat4(&frame.lightDirection);
    const float down = -XMVectorGetY(direction);
    if (down < 0.05f || frame.groundShadowColor.w <= 0.0f) {
        return false;
    }
    const XMVECTOR up = down > 0.99f ? XMVectorSet(0, 0, 1, 0) : XMVectorSet(0, 1, 0, 0);
    const XMMATRIX lightView = XMMatrixLookToLH(XMVectorZero(), direction, up);

    // 範囲: 影を落とす物の囲みの箱の角と、その角から照明の向きに進んで床（高さ 0）に届いた点が全部入るように
    XMVECTOR lo = XMVectorReplicate(FLT_MAX);
    XMVECTOR hi = XMVectorReplicate(-FLT_MAX);
    float floorMinX = FLT_MAX;
    float floorMaxX = -FLT_MAX;
    float floorMinZ = FLT_MAX;
    float floorMaxZ = -FLT_MAX;
    bool any = false;
    for (uint32_t i = 0; i < params.item_count; ++i) {
        const auto& mesh = *reinterpret_cast<const Mesh*>(params.items[i].mesh);
        if (mesh.IsEmpty() || !mesh.HasGroundShadow()) {
            continue;
        }
        const XMMATRIX world = load(params.items[i].world);
        const XMFLOAT3& a = mesh.BoundsMin();
        const XMFLOAT3& b = mesh.BoundsMax();
        for (int corner = 0; corner < 8; ++corner) {
            const XMVECTOR p = XMVector3Transform(
                XMVectorSet((corner & 1) ? b.x : a.x, (corner & 2) ? b.y : a.y, (corner & 4) ? b.z : a.z, 1.0f), world);
            const XMVECTOR onFloor = XMVectorSetY(XMVectorAdd(p, XMVectorScale(direction, std::max(XMVectorGetY(p), 0.0f) / down)), 0.0f);
            for (const XMVECTOR& point : {p, onFloor}) {
                const XMVECTOR inLight = XMVector3Transform(point, lightView);
                lo = XMVectorMin(lo, inLight);
                hi = XMVectorMax(hi, inLight);
            }
            floorMinX = std::min(floorMinX, XMVectorGetX(onFloor));
            floorMaxX = std::max(floorMaxX, XMVectorGetX(onFloor));
            floorMinZ = std::min(floorMinZ, XMVectorGetZ(onFloor));
            floorMaxZ = std::max(floorMaxZ, XMVectorGetZ(onFloor));
            any = true;
        }
    }
    if (!any) {
        return false;
    }
    XMFLOAT3 l;
    XMFLOAT3 h;
    XMStoreFloat3(&l, lo);
    XMStoreFloat3(&h, hi);
    const float pad = std::max({h.x - l.x, h.y - l.y, h.z - l.z}) * 0.02f + 0.01f;
    const float depthRange = (h.z - l.z) + 2.0f * pad;
    const XMMATRIX lightViewProj
        = lightView * XMMatrixOrthographicOffCenterLH(l.x - pad, h.x + pad, l.y - pad, h.y + pad, l.z - pad, h.z + pad);
    const UINT size = params.shadow_map_size == 0 ? kDefaultShadowMapSize : std::clamp<UINT>(params.shadow_map_size, 256, 8192);
    EnsureDepthMap(size, groundShadowMapDsv_, groundShadowMapSrv_, groundShadowMapSize_, L"地面の影");
    DrawLightDepth(params, lightViewProj, frame, groundShadowMapDsv_.Get(), size, true);

    groundFrame = frame;
    XMStoreFloat4x4(&groundFrame.lightViewProj, lightViewProj);
    groundFrame.shadowParams = {1.0f / static_cast<float>(size), kShadowBiasWorld / std::max(depthRange, 1e-3f), 1.0f, 0.0f};
    groundFrame.shadowSoftness = {depthRange, ShadowSoftnessOf(params), 1.0f / std::max(h.x - l.x + 2.0f * pad, 1e-3f),
        1.0f / std::max(h.y - l.y + 2.0f * pad, 1e-3f)};
    // 床に描く四角形: 影の届く範囲を、ぼけの分だけ広げる（斜めから照らすほど、床の上のぼけは長く伸びる）
    const float margin = kMaxSoftShadowWorld / std::max(down, 0.2f);
    XMStoreFloat4x4(&floorWorld, XMMatrixScaling(floorMaxX - floorMinX + 2.0f * margin, 1.0f, floorMaxZ - floorMinZ + 2.0f * margin)
        * XMMatrixTranslation(floorMinX - margin, kGroundShadowLift, floorMinZ - margin));
    return true;
}

void Engine::DrawSky(const TtFrameParams& params, DirectX::CXMMATRIX viewProj)
{
    using namespace DirectX;
    if (!currentEnvironment_ || params.projection[11] == 0.0f) {
        return; // 環境の画像が無い・平行投影（固定の視点。どの点も同じ向きになる）
    }
    XMVECTOR determinant;
    const XMMATRIX inverse = XMMatrixInverse(&determinant, viewProj);
    if (XMVectorGetX(determinant) == 0.0f) {
        return;
    }
    SkyConstants constants{};
    XMStoreFloat4x4(&constants.inverseViewProj, inverse);
    constants.params = {0.0f, Sanitize(params.environment_intensity, 1.0f, 0.0f, 100.0f), Sanitize(params.environment_rotation, 0.0f, -1e4f, 1e4f),
        0.0f};
    ID3D11DeviceContext* ctx = context_.Get();
    D3D11_MAPPED_SUBRESOURCE mapped{};
    CheckHr(ctx->Map(skyConstantBuffer_.Get(), 0, D3D11_MAP_WRITE_DISCARD, 0, &mapped), L"背景の定数の更新");
    std::memcpy(mapped.pData, &constants, sizeof(constants));
    ctx->Unmap(skyConstantBuffer_.Get(), 0);
    ctx->IASetInputLayout(nullptr);
    ctx->IASetPrimitiveTopology(D3D11_PRIMITIVE_TOPOLOGY_TRIANGLELIST);
    ctx->VSSetShader(postVertexShader_.Get(), nullptr, 0);
    ctx->PSSetShader(skyPixelShader_.Get(), nullptr, 0);
    ctx->PSSetConstantBuffers(0, 1, skyConstantBuffer_.GetAddressOf());
    ctx->PSSetShaderResources(0, 1, &currentEnvironment_);
    ctx->PSSetSamplers(0, 1, environmentSampler_.GetAddressOf());
    ctx->RSSetState(rasterizerState_.Get());
    ctx->OMSetDepthStencilState(overlayDepthState_.Get(), 0); // 奥行きは比べず、書かない
    ctx->OMSetBlendState(nullptr, nullptr, 0xFFFFFFFF);
    ctx->Draw(3, 0);
    ID3D11ShaderResourceView* none = nullptr;
    ctx->PSSetShaderResources(0, 1, &none);
}

void Engine::DrawSoftGroundShadow(const ModelFrame& groundFrame, const DirectX::XMFLOAT4X4& floorWorld)
{
    using namespace DirectX;
    ID3D11DeviceContext* ctx = context_.Get();
    const XMMATRIX world = XMLoadFloat4x4(&floorWorld);
    WriteObjectConstants(world, world * XMLoadFloat4x4(&groundFrame.viewProj), groundFrame);
    ID3D11Buffer* constantBuffers[] = {objectConstantBuffer_.Get(), materialConstantBuffer_.Get(), lightConstantBuffer_.Get()};
    ctx->VSSetConstantBuffers(0, 3, constantBuffers);
    ctx->PSSetConstantBuffers(0, 3, constantBuffers);
    ctx->IASetInputLayout(nullptr);
    ctx->IASetPrimitiveTopology(D3D11_PRIMITIVE_TOPOLOGY_TRIANGLESTRIP);
    ctx->VSSetShader(groundSoftShadowVertexShader_.Get(), nullptr, 0);
    ctx->PSSetShader(groundSoftShadowPixelShader_.Get(), nullptr, 0);
    ID3D11SamplerState* samplers[] = {textureSampler_.Get(), clampSampler_.Get(), shadowSampler_.Get(), environmentSampler_.Get()};
    ctx->PSSetSamplers(0, 4, samplers);
    ID3D11ShaderResourceView* map = groundShadowMapSrv_.Get();
    ctx->PSSetShaderResources(3, 1, &map);
    ctx->RSSetState(cullNoneState_.Get());
    ctx->OMSetDepthStencilState(groundShadowState_.Get(), 0);
    ctx->OMSetBlendState(alphaBlendState_.Get(), nullptr, 0xFFFFFFFF);
    ctx->Draw(4, 0);
}

void Engine::DrawGroundShadows(const TtFrameParams& params, const ModelFrame& frame)
{
    using namespace DirectX;
    auto load = [](const float (&m)[16]) { return XMLoadFloat4x4(reinterpret_cast<const XMFLOAT4X4*>(m)); };
    // 下から・真横から照らすときは、床に影を落とさない
    const XMVECTOR direction = XMLoadFloat4(&frame.lightDirection);
    if (XMVectorGetY(direction) > -0.05f || frame.groundShadowColor.w <= 0.0f) {
        return;
    }
    // 高さ 0 の床へ、照明の向きに押しつぶす（DirectXMath の影の行列は、w = 0 の「光の来る向き」で平行光になる）
    const XMMATRIX flatten = XMMatrixShadow(XMVectorSet(0.0f, 1.0f, 0.0f, 0.0f), XMVectorNegate(direction))
        * XMMatrixTranslation(0.0f, kGroundShadowLift, 0.0f);
    const XMMATRIX viewProj = XMLoadFloat4x4(&frame.viewProj);

    ID3D11DeviceContext* ctx = context_.Get();
    ctx->VSSetShader(shadowVertexShader_.Get(), nullptr, 0);
    ctx->PSSetShader(groundShadowPixelShader_.Get(), nullptr, 0);
    ctx->RSSetState(cullNoneState_.Get()); // 押しつぶすと面の向きが変わることがある
    ctx->OMSetDepthStencilState(groundShadowState_.Get(), 0);
    ctx->OMSetBlendState(alphaBlendState_.Get(), nullptr, 0xFFFFFFFF);
    for (uint32_t i = 0; i < params.item_count; ++i) {
        const auto& mesh = *reinterpret_cast<const Mesh*>(params.items[i].mesh);
        if (mesh.IsEmpty() || !mesh.HasGroundShadow()) {
            continue;
        }
        const XMMATRIX world = load(params.items[i].world);
        WriteObjectConstants(world, world * flatten * viewProj, frame);
        BindMesh(mesh);
        for (const Mesh::Material& material : mesh.Materials()) {
            if (material.groundShadow && material.indexCount > 0 && material.diffuse.w > 0.0f) {
                ctx->DrawIndexed(material.indexCount, material.indexStart, 0);
            }
        }
    }
}

void Engine::DrawBox(
    const DirectX::XMFLOAT3& minimum, const DirectX::XMFLOAT3& maximum, DirectX::FXMMATRIX world, DirectX::CXMMATRIX viewProj)
{
    using namespace DirectX;
    // 1 辺 1 の箱を、囲む箱の大きさに広げて中心へ動かす
    const XMVECTOR lo = XMLoadFloat3(&minimum);
    const XMVECTOR hi = XMLoadFloat3(&maximum);
    const XMVECTOR size = XMVectorMax(XMVectorSubtract(hi, lo), XMVectorReplicate(1e-4f));
    const XMVECTOR center = XMVectorScale(XMVectorAdd(lo, hi), 0.5f);
    const XMMATRIX box = XMMatrixScalingFromVector(size) * XMMatrixTranslationFromVector(center);
    SetConstants(box * world * viewProj);

    const UINT stride = sizeof(DemoVertex);
    const UINT offset = 0;
    context_->IASetPrimitiveTopology(D3D11_PRIMITIVE_TOPOLOGY_LINELIST);
    context_->IASetVertexBuffers(0, 1, boxEdgeVertexBuffer_.GetAddressOf(), &stride, &offset);
    context_->Draw(boxEdgeVertexCount_, 0);
}

void Engine::DrawOverlayLines(const TtLineVertex* vertices, uint32_t count, DirectX::CXMMATRIX viewProj)
{
    if (count > overlayVertexCapacity_) {
        // 足りなければ倍の大きさで作り直す（毎フレーム作り直さないように）
        UINT capacity = std::max<UINT>(count, overlayVertexCapacity_ * 2);
        capacity = std::max<UINT>(capacity, 1024);
        D3D11_BUFFER_DESC desc{};
        desc.ByteWidth = capacity * sizeof(TtLineVertex);
        desc.Usage = D3D11_USAGE_DYNAMIC;
        desc.BindFlags = D3D11_BIND_VERTEX_BUFFER;
        desc.CPUAccessFlags = D3D11_CPU_ACCESS_WRITE;
        overlayVertexBuffer_.Reset();
        CheckHr(device_->CreateBuffer(&desc, nullptr, &overlayVertexBuffer_), L"重ね描きの線のバッファーの作成");
        overlayVertexCapacity_ = capacity;
    }
    D3D11_MAPPED_SUBRESOURCE mapped{};
    CheckHr(context_->Map(overlayVertexBuffer_.Get(), 0, D3D11_MAP_WRITE_DISCARD, 0, &mapped), L"重ね描きの線の更新");
    std::memcpy(mapped.pData, vertices, static_cast<size_t>(count) * sizeof(TtLineVertex));
    context_->Unmap(overlayVertexBuffer_.Get(), 0);

    SetConstants(viewProj);
    const UINT stride = sizeof(TtLineVertex);
    const UINT offset = 0;
    context_->OMSetDepthStencilState(overlayDepthState_.Get(), 0);
    context_->IASetPrimitiveTopology(D3D11_PRIMITIVE_TOPOLOGY_LINELIST);
    context_->IASetVertexBuffers(0, 1, overlayVertexBuffer_.GetAddressOf(), &stride, &offset);
    context_->Draw(count, 0);
}

void Engine::CreateModelResources()
{
    D3D11_DEPTH_STENCIL_DESC overlay{};
    overlay.DepthEnable = FALSE; // 体の中にあるボーンも見えるよう、奥行きを無視して手前に描く
    overlay.DepthWriteMask = D3D11_DEPTH_WRITE_MASK_ZERO;
    overlay.DepthFunc = D3D11_COMPARISON_ALWAYS;
    CheckHr(device_->CreateDepthStencilState(&overlay, &overlayDepthState_), L"重ね描きの深度設定の作成");

    CheckHr(device_->CreateVertexShader(g_ModelVS, sizeof(g_ModelVS), nullptr, &modelVertexShader_), L"モデルの頂点シェーダーの作成");
    CheckHr(device_->CreatePixelShader(g_ModelPS, sizeof(g_ModelPS), nullptr, &modelPixelShader_), L"モデルのピクセルシェーダーの作成");
    CheckHr(device_->CreateVertexShader(g_EdgeVS, sizeof(g_EdgeVS), nullptr, &edgeVertexShader_), L"輪郭線の頂点シェーダーの作成");
    CheckHr(device_->CreatePixelShader(g_EdgePS, sizeof(g_EdgePS), nullptr, &edgePixelShader_), L"輪郭線のピクセルシェーダーの作成");

    // 変形した頂点（0 番）と、頂点ごとの輪郭線の倍率（1 番、変形しない）を別のバッファーから読む
    const D3D11_INPUT_ELEMENT_DESC layout[] = {
        {"POSITION", 0, DXGI_FORMAT_R32G32B32_FLOAT, 0, 0, D3D11_INPUT_PER_VERTEX_DATA, 0},
        {"NORMAL", 0, DXGI_FORMAT_R32G32B32_FLOAT, 0, 12, D3D11_INPUT_PER_VERTEX_DATA, 0},
        {"TEXCOORD", 0, DXGI_FORMAT_R32G32_FLOAT, 0, 24, D3D11_INPUT_PER_VERTEX_DATA, 0},
        {"TEXCOORD", 1, DXGI_FORMAT_R32_FLOAT, 1, 0, D3D11_INPUT_PER_VERTEX_DATA, 0},
    };
    CheckHr(device_->CreateVertexShader(g_ShadowVS, sizeof(g_ShadowVS), nullptr, &shadowVertexShader_), L"影の頂点シェーダーの作成");
    CheckHr(device_->CreateVertexShader(g_GroundSoftShadowVS, sizeof(g_GroundSoftShadowVS), nullptr, &groundSoftShadowVertexShader_),
        L"柔らかな地面の影の頂点シェーダーの作成");
    CheckHr(device_->CreatePixelShader(g_GroundSoftShadowPS, sizeof(g_GroundSoftShadowPS), nullptr, &groundSoftShadowPixelShader_),
        L"柔らかな地面の影のピクセルシェーダーの作成");
    CheckHr(device_->CreatePixelShader(g_GroundShadowPS, sizeof(g_GroundShadowPS), nullptr, &groundShadowPixelShader_),
        L"地面の影のピクセルシェーダーの作成");
    // 輪郭線の頂点シェーダーは全部の要素を使うので、そちらに合わせて作る（モデルの頂点シェーダーにも使える）
    CheckHr(device_->CreateInputLayout(layout, ARRAYSIZE(layout), g_EdgeVS, sizeof(g_EdgeVS), &modelInputLayout_),
        L"モデルの頂点レイアウトの作成");

    auto dynamicBuffer = [&](UINT size, ComPtr<ID3D11Buffer>& out, const wchar_t* what) {
        D3D11_BUFFER_DESC desc{};
        desc.ByteWidth = size;
        desc.Usage = D3D11_USAGE_DYNAMIC;
        desc.BindFlags = D3D11_BIND_CONSTANT_BUFFER;
        desc.CPUAccessFlags = D3D11_CPU_ACCESS_WRITE;
        CheckHr(device_->CreateBuffer(&desc, nullptr, &out), what);
    };
    dynamicBuffer(sizeof(ObjectConstants), objectConstantBuffer_, L"モデルの定数バッファーの作成");
    dynamicBuffer(sizeof(MaterialConstants), materialConstantBuffer_, L"材質の定数バッファーの作成");
    dynamicBuffer(sizeof(LightConstants), lightConstantBuffer_, L"足す照明の定数バッファーの作成");

    D3D11_RASTERIZER_DESC rs{};
    rs.FillMode = D3D11_FILL_SOLID;
    rs.CullMode = D3D11_CULL_BACK; // 時計回りの面が表（MMD と同じ）
    rs.DepthClipEnable = TRUE;
    CheckHr(device_->CreateRasterizerState(&rs, &cullBackState_), L"裏面を描かない設定の作成");
    rs.CullMode = D3D11_CULL_NONE;
    CheckHr(device_->CreateRasterizerState(&rs, &cullNoneState_), L"両面を描く設定の作成");
    rs.CullMode = D3D11_CULL_FRONT;
    CheckHr(device_->CreateRasterizerState(&rs, &mirrorCullState_), L"床の映り込みの面の設定の作成");
    // 輪郭線は裏側の面だけを描き、奥行きを少し奥へずらす（両面の材質を裏から見たとき、同じ奥行きの面を線の色で
    // 塗りつぶさないように。線は面の外へはみ出した所だけに残る）
    rs.CullMode = D3D11_CULL_FRONT;
    rs.DepthBias = 64;
    rs.SlopeScaledDepthBias = 1.0f;
    CheckHr(device_->CreateRasterizerState(&rs, &cullFrontState_), L"裏側の面だけ描く設定の作成");
    // セルフシャドウの奥行きの画像は両面を描き、面の傾きに応じて奥へずらす（影が自分の面に細かく落ちるのを防ぐ）
    rs.CullMode = D3D11_CULL_NONE;
    rs.DepthBias = 0;
    rs.SlopeScaledDepthBias = 1.5f;
    rs.DepthBiasClamp = 0.01f;
    CheckHr(device_->CreateRasterizerState(&rs, &shadowCasterState_), L"影を落とす面の設定の作成");

    D3D11_DEPTH_STENCIL_DESC ds{};
    ds.DepthEnable = TRUE;
    ds.DepthWriteMask = D3D11_DEPTH_WRITE_MASK_ALL;
    ds.DepthFunc = D3D11_COMPARISON_LESS_EQUAL;
    CheckHr(device_->CreateDepthStencilState(&ds, &modelDepthState_), L"モデルの深度設定の作成");
    // 地面の影: 奥行きは比べるだけ。ステンシルが 0 の所だけに描いて 1 にする（重なった影を二重に濃くしない）
    D3D11_DEPTH_STENCIL_DESC shadowDs = ds;
    shadowDs.DepthWriteMask = D3D11_DEPTH_WRITE_MASK_ZERO;
    shadowDs.StencilEnable = TRUE;
    shadowDs.StencilReadMask = 0xFF;
    shadowDs.StencilWriteMask = 0xFF;
    shadowDs.FrontFace = {D3D11_STENCIL_OP_KEEP, D3D11_STENCIL_OP_KEEP, D3D11_STENCIL_OP_INCR_SAT, D3D11_COMPARISON_EQUAL};
    shadowDs.BackFace = shadowDs.FrontFace;
    CheckHr(device_->CreateDepthStencilState(&shadowDs, &groundShadowState_), L"地面の影の深度設定の作成");

    D3D11_BLEND_DESC blend{};
    blend.RenderTarget[0].BlendEnable = TRUE;
    blend.RenderTarget[0].SrcBlend = D3D11_BLEND_SRC_ALPHA;
    blend.RenderTarget[0].DestBlend = D3D11_BLEND_INV_SRC_ALPHA;
    blend.RenderTarget[0].BlendOp = D3D11_BLEND_OP_ADD;
    blend.RenderTarget[0].SrcBlendAlpha = D3D11_BLEND_ONE;
    blend.RenderTarget[0].DestBlendAlpha = D3D11_BLEND_INV_SRC_ALPHA;
    blend.RenderTarget[0].BlendOpAlpha = D3D11_BLEND_OP_ADD;
    blend.RenderTarget[0].RenderTargetWriteMask = D3D11_COLOR_WRITE_ENABLE_ALL;
    CheckHr(device_->CreateBlendState(&blend, &alphaBlendState_), L"半透明の合成設定の作成");

    D3D11_SAMPLER_DESC sampler{};
    sampler.Filter = D3D11_FILTER_ANISOTROPIC;
    sampler.MaxAnisotropy = 8;
    sampler.AddressU = D3D11_TEXTURE_ADDRESS_WRAP;
    sampler.AddressV = D3D11_TEXTURE_ADDRESS_WRAP;
    sampler.AddressW = D3D11_TEXTURE_ADDRESS_WRAP;
    sampler.MaxLOD = D3D11_FLOAT32_MAX;
    CheckHr(device_->CreateSamplerState(&sampler, &textureSampler_), L"テクスチャの読み方の設定の作成");
    // トゥーン・スフィアは端で止める（トゥーンの上端・下端が反対側の色と混ざらないように）
    sampler.Filter = D3D11_FILTER_MIN_MAG_MIP_LINEAR;
    sampler.MaxAnisotropy = 1;
    sampler.AddressU = D3D11_TEXTURE_ADDRESS_CLAMP;
    sampler.AddressV = D3D11_TEXTURE_ADDRESS_CLAMP;
    sampler.AddressW = D3D11_TEXTURE_ADDRESS_CLAMP;
    CheckHr(device_->CreateSamplerState(&sampler, &clampSampler_), L"端で止めるテクスチャの読み方の設定の作成");
    // 環境の画像: 横（経度）はつながっているので繰り返し、縦（真上・真下）は端で止める
    sampler.AddressU = D3D11_TEXTURE_ADDRESS_WRAP;
    CheckHr(device_->CreateSamplerState(&sampler, &environmentSampler_), L"環境の画像の読み方の設定の作成");
    // セルフシャドウ: 奥行きを比べる（範囲の外は「影なし」）
    D3D11_SAMPLER_DESC shadowSampler{};
    shadowSampler.Filter = D3D11_FILTER_COMPARISON_MIN_MAG_LINEAR_MIP_POINT;
    shadowSampler.AddressU = D3D11_TEXTURE_ADDRESS_BORDER;
    shadowSampler.AddressV = D3D11_TEXTURE_ADDRESS_BORDER;
    shadowSampler.AddressW = D3D11_TEXTURE_ADDRESS_BORDER;
    shadowSampler.BorderColor[0] = shadowSampler.BorderColor[1] = shadowSampler.BorderColor[2] = shadowSampler.BorderColor[3] = 1.0f;
    shadowSampler.ComparisonFunc = D3D11_COMPARISON_LESS_EQUAL;
    shadowSampler.MaxLOD = D3D11_FLOAT32_MAX;
    CheckHr(device_->CreateSamplerState(&shadowSampler, &shadowSampler_), L"影の奥行きの比べ方の設定の作成");

    // テクスチャの無い材質に使う白
    const uint32_t white = 0xFFFFFFFF;
    D3D11_TEXTURE2D_DESC td{};
    td.Width = 1;
    td.Height = 1;
    td.MipLevels = 1;
    td.ArraySize = 1;
    td.Format = DXGI_FORMAT_B8G8R8A8_UNORM;
    td.SampleDesc.Count = 1;
    td.Usage = D3D11_USAGE_IMMUTABLE;
    td.BindFlags = D3D11_BIND_SHADER_RESOURCE;
    D3D11_SUBRESOURCE_DATA init{&white, 4, 0};
    ComPtr<ID3D11Texture2D> texture;
    CheckHr(device_->CreateTexture2D(&td, &init, &texture), L"白いテクスチャの作成");
    CheckHr(device_->CreateShaderResourceView(texture.Get(), nullptr, &whiteTexture_), L"白いテクスチャのビューの作成");

    // セルフシャドウを使わない間に渡す、奥行き 1（何も無い）の 1 x 1 の画像
    const float farDepth = 1.0f;
    D3D11_TEXTURE2D_DESC sd = td;
    sd.Format = DXGI_FORMAT_R32_FLOAT;
    D3D11_SUBRESOURCE_DATA shadowInit{&farDepth, 4, 0};
    ComPtr<ID3D11Texture2D> noShadow;
    CheckHr(device_->CreateTexture2D(&sd, &shadowInit, &noShadow), L"影なしの奥行きの画像の作成");
    CheckHr(device_->CreateShaderResourceView(noShadow.Get(), nullptr, &noShadowMap_), L"影なしの奥行きの画像のビューの作成");
}

} // namespace tt
