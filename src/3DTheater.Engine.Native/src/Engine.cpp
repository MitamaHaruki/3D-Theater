#include "Engine.h"

#include <vector>

#include "DemoScene.h"
#include "Log.h"

// FxCompile が中間フォルダに書き出すシェーダーのバイト列
#include "BasicPS.h"
#include "BasicVS.h"

namespace tt {

namespace {

constexpr float kClearColor[4] = {0.13f, 0.14f, 0.16f, 1.0f};

struct FrameConstants {
    DirectX::XMFLOAT4X4 worldViewProj;
};

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

Engine::Engine(const TtEngineDesc& desc) : ownerThreadId_(GetCurrentThreadId())
{
    CreateDevice((desc.flags & TT_ENGINE_FLAG_USE_WARP) != 0, (desc.flags & TT_ENGINE_FLAG_DEBUG_LAYER) != 0);
    CreateSceneResources();
}

Engine::~Engine()
{
    if (context_) {
        context_->ClearState();
        context_->Flush();
    }
    magic_ = 0;
}

void Engine::CheckThread() const
{
    if (GetCurrentThreadId() != ownerThreadId_) {
        throw Error(TT_E_WRONG_THREAD, L"エンジンを作ったスレッド以外から呼び出されました");
    }
}

void Engine::CreateDevice(bool useWarp, bool debugLayer)
{
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

    DXGI_ADAPTER_DESC1 adapterDesc{};
    adapter_->GetDesc1(&adapterDesc);
    Log(TT_LOG_INFO,
        std::format(L"Direct3D 11 を初期化しました: {} / 機能レベル {} / 専用メモリ {} MB / 検査機能 {}",
            adapterDesc.Description, FeatureLevelName(featureLevel_), adapterDesc.DedicatedVideoMemory / (1024 * 1024),
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

    D3D11_DEPTH_STENCIL_DESC dsDesc{};
    dsDesc.DepthEnable = TRUE;
    dsDesc.DepthWriteMask = D3D11_DEPTH_WRITE_MASK_ALL;
    dsDesc.DepthFunc = D3D11_COMPARISON_LESS;
    CheckHr(device_->CreateDepthStencilState(&dsDesc, &depthState_), L"深度設定の作成");

    const std::vector<DemoVertex> grid = BuildGridLines(10, 1.0f);
    gridVertexBuffer_ = CreateImmutableBuffer(device_.Get(), grid, D3D11_BIND_VERTEX_BUFFER, L"格子の頂点バッファー作成");
    gridVertexCount_ = static_cast<UINT>(grid.size());

    std::vector<DemoVertex> cubeVertices;
    std::vector<uint16_t> cubeIndices;
    BuildCube(cubeVertices, cubeIndices);
    cubeVertexBuffer_ = CreateImmutableBuffer(device_.Get(), cubeVertices, D3D11_BIND_VERTEX_BUFFER, L"立方体の頂点バッファー作成");
    cubeIndexBuffer_ = CreateImmutableBuffer(device_.Get(), cubeIndices, D3D11_BIND_INDEX_BUFFER, L"立方体の索引バッファー作成");
    cubeIndexCount_ = static_cast<UINT>(cubeIndices.size());
}

IDirect3DDevice9Ex* Engine::D3D9Device()
{
    if (d3d9Device_) {
        return d3d9Device_.Get();
    }

    ComPtr<IDirect3D9Ex> d3d9;
    CheckHr(Direct3DCreate9Ex(D3D_SDK_VERSION, &d3d9), L"Direct3D 9Ex の初期化");

    // Direct3D 11 と同じ GPU でないと面を共有できない
    DXGI_ADAPTER_DESC1 adapterDesc{};
    adapter_->GetDesc1(&adapterDesc);
    UINT adapterIndex = UINT_MAX;
    for (UINT i = 0; i < d3d9->GetAdapterCount(); ++i) {
        LUID luid{};
        if (SUCCEEDED(d3d9->GetAdapterLUID(i, &luid)) && luid.LowPart == adapterDesc.AdapterLuid.LowPart
            && luid.HighPart == adapterDesc.AdapterLuid.HighPart) {
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
    DXGI_ADAPTER_DESC1 desc{};
    adapter_->GetDesc1(&desc);
    info.feature_level = static_cast<uint32_t>(featureLevel_);
    info.is_software = (desc.Flags & DXGI_ADAPTER_FLAG_SOFTWARE) ? 1u : 0u;
    info.debug_layer_enabled = debugLayerEnabled_ ? 1u : 0u;
    info.dedicated_video_memory = desc.DedicatedVideoMemory;
    wcsncpy_s(info.adapter_name, desc.Description, _TRUNCATE);
}

void Engine::QueryVideoMemory(TtVideoMemoryInfo& info) const
{
    ComPtr<IDXGIAdapter3> adapter3;
    if (FAILED(adapter_.As(&adapter3))) {
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

void Engine::DrawDemoScene(
    ID3D11RenderTargetView* rtv, ID3D11DepthStencilView* dsv, int width, int height, float timeSeconds)
{
    using namespace DirectX;

    ID3D11DeviceContext* ctx = context_.Get();
    ctx->OMSetRenderTargets(1, &rtv, dsv);
    const D3D11_VIEWPORT viewport{0.0f, 0.0f, static_cast<float>(width), static_cast<float>(height), 0.0f, 1.0f};
    ctx->RSSetViewports(1, &viewport);
    ctx->ClearRenderTargetView(rtv, kClearColor);
    ctx->ClearDepthStencilView(dsv, D3D11_CLEAR_DEPTH, 1.0f, 0);

    ctx->IASetInputLayout(inputLayout_.Get());
    ctx->VSSetShader(vertexShader_.Get(), nullptr, 0);
    ctx->VSSetConstantBuffers(0, 1, constantBuffer_.GetAddressOf());
    ctx->PSSetShader(pixelShader_.Get(), nullptr, 0);
    ctx->RSSetState(rasterizerState_.Get());
    ctx->OMSetDepthStencilState(depthState_.Get(), 0);

    // 座標系は MMD に合わせた仮の決め（左手系・Y 上）。正式な決めは docs/設計判断記録.md を参照
    const XMMATRIX view = XMMatrixLookAtLH(XMVectorSet(4.0f, 3.0f, -6.0f, 1.0f), XMVectorSet(0.0f, 0.5f, 0.0f, 1.0f),
        XMVectorSet(0.0f, 1.0f, 0.0f, 0.0f));
    const XMMATRIX proj = XMMatrixPerspectiveFovLH(
        XMConvertToRadians(45.0f), static_cast<float>(width) / static_cast<float>(height), 0.1f, 1000.0f);
    const XMMATRIX viewProj = view * proj;

    const UINT stride = sizeof(DemoVertex);
    const UINT offset = 0;

    SetConstants(viewProj);
    ctx->IASetPrimitiveTopology(D3D11_PRIMITIVE_TOPOLOGY_LINELIST);
    ctx->IASetVertexBuffers(0, 1, gridVertexBuffer_.GetAddressOf(), &stride, &offset);
    ctx->Draw(gridVertexCount_, 0);

    SetConstants(XMMatrixRotationY(timeSeconds * 0.8f) * XMMatrixTranslation(0.0f, 0.5f, 0.0f) * viewProj);
    ctx->IASetPrimitiveTopology(D3D11_PRIMITIVE_TOPOLOGY_TRIANGLELIST);
    ctx->IASetVertexBuffers(0, 1, cubeVertexBuffer_.GetAddressOf(), &stride, &offset);
    ctx->IASetIndexBuffer(cubeIndexBuffer_.Get(), DXGI_FORMAT_R16_UINT, 0);
    ctx->DrawIndexed(cubeIndexCount_, 0, 0);

    ctx->OMSetRenderTargets(0, nullptr, nullptr);
}

} // namespace tt
