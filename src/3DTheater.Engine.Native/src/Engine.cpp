#include "Engine.h"

#include <algorithm>
#include <cstring>
#include <vector>

#include "DemoScene.h"
#include "Log.h"
#include "Resources.h"

// FxCompile が中間フォルダに書き出すシェーダーのバイト列
#include "BasicPS.h"
#include "BasicVS.h"
#include "ModelPS.h"
#include "ModelVS.h"

namespace tt {

namespace {

constexpr float kClearColor[4] = {0.13f, 0.14f, 0.16f, 1.0f};

// MMD の初期の照明の向き（光が進む向き）。照明の項目の反映は Phase 4 で行う
const DirectX::XMVECTORF32 kLightDirection = {{{-0.4082483f, -0.8164966f, 0.4082483f, 0.0f}}};

struct FrameConstants {
    DirectX::XMFLOAT4X4 worldViewProj;
};

struct ObjectConstants {
    DirectX::XMFLOAT4X4 worldViewProj;
    DirectX::XMFLOAT4X4 world;
    DirectX::XMFLOAT4 lightDirection;
};

struct MaterialConstants {
    DirectX::XMFLOAT4 diffuse;
    DirectX::XMFLOAT4 ambientAndTexture; // w が 1 ならテクスチャあり
    DirectX::XMFLOAT4 specularAndPower;
    DirectX::XMFLOAT4 textureMultiply;
    DirectX::XMFLOAT4 textureAdd;
};

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

Engine::Engine(const TtEngineDesc& desc) : ownerThreadId_(GetCurrentThreadId())
{
    CreateDevice((desc.flags & TT_ENGINE_FLAG_USE_WARP) != 0, (desc.flags & TT_ENGINE_FLAG_DEBUG_LAYER) != 0);
    CreateSceneResources();
    CreateModelResources();
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

void Engine::DrawScene(
    ID3D11RenderTargetView* rtv, ID3D11DepthStencilView* dsv, int width, int height, const TtFrameParams& params)
{
    using namespace DirectX;

    auto load = [](const float (&m)[16]) { return XMLoadFloat4x4(reinterpret_cast<const XMFLOAT4X4*>(m)); };

    ID3D11DeviceContext* ctx = context_.Get();
    ctx->OMSetRenderTargets(1, &rtv, dsv);
    const D3D11_VIEWPORT viewport{0.0f, 0.0f, static_cast<float>(width), static_cast<float>(height), 0.0f, 1.0f};
    ctx->RSSetViewports(1, &viewport);
    ctx->ClearRenderTargetView(rtv, kClearColor);
    ctx->ClearDepthStencilView(dsv, D3D11_CLEAR_DEPTH, 1.0f, 0);

    SetBasicPipeline();
    const XMMATRIX viewProj = load(params.view) * load(params.projection);
    const UINT stride = sizeof(DemoVertex);
    const UINT offset = 0;

    ctx->IASetPrimitiveTopology(D3D11_PRIMITIVE_TOPOLOGY_LINELIST);
    if (params.flags & TT_FRAME_SHOW_GRID) {
        SetConstants(load(params.grid_world) * viewProj);
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

    if (params.flags & TT_FRAME_SHOW_TEST_OBJECT) {
        const XMMATRIX worldViewProj = load(params.test_object_world) * viewProj;
        SetConstants(worldViewProj);
        ctx->IASetPrimitiveTopology(D3D11_PRIMITIVE_TOPOLOGY_TRIANGLELIST);
        ctx->IASetVertexBuffers(0, 1, cubeVertexBuffer_.GetAddressOf(), &stride, &offset);
        ctx->IASetIndexBuffer(cubeIndexBuffer_.Get(), DXGI_FORMAT_R16_UINT, 0);
        ctx->DrawIndexed(cubeIndexCount_, 0, 0);

        if (params.flags & TT_FRAME_TEST_OBJECT_SELECTED) {
            ctx->IASetPrimitiveTopology(D3D11_PRIMITIVE_TOPOLOGY_LINELIST);
            ctx->IASetVertexBuffers(0, 1, cubeEdgeVertexBuffer_.GetAddressOf(), &stride, &offset);
            ctx->Draw(cubeEdgeVertexCount_, 0);
        }
    }

    // モデル（並び順に描く。半透明の材質があるので、MMD と同じく並び順をそのまま使う）
    for (uint32_t i = 0; i < params.item_count; ++i) {
        const TtDrawItem& item = params.items[i];
        const auto& mesh = *reinterpret_cast<const Mesh*>(item.mesh);
        const XMMATRIX world = load(item.world);
        DrawMesh(mesh, world, viewProj);
        if (item.flags & TT_DRAW_SELECTED) {
            SetBasicPipeline();
            DrawBox(mesh.BoundsMin(), mesh.BoundsMax(), world, viewProj);
        }
    }

    if (params.overlay_vertex_count >= 2) {
        SetBasicPipeline();
        DrawOverlayLines(params.overlay_lines, params.overlay_vertex_count - (params.overlay_vertex_count % 2), viewProj);
    }

    ctx->OMSetBlendState(nullptr, nullptr, 0xFFFFFFFF);
    ctx->OMSetRenderTargets(0, nullptr, nullptr);
}

void Engine::SetBasicPipeline()
{
    ID3D11DeviceContext* ctx = context_.Get();
    ctx->IASetInputLayout(inputLayout_.Get());
    ctx->VSSetShader(vertexShader_.Get(), nullptr, 0);
    ctx->VSSetConstantBuffers(0, 1, constantBuffer_.GetAddressOf());
    ctx->PSSetShader(pixelShader_.Get(), nullptr, 0);
    ctx->RSSetState(rasterizerState_.Get());
    ctx->OMSetDepthStencilState(depthState_.Get(), 0);
    ctx->OMSetBlendState(nullptr, nullptr, 0xFFFFFFFF);
}

void Engine::DrawMesh(const Mesh& mesh, DirectX::FXMMATRIX world, DirectX::CXMMATRIX viewProj)
{
    using namespace DirectX;
    if (mesh.IsEmpty()) {
        return;
    }
    ID3D11DeviceContext* ctx = context_.Get();

    D3D11_MAPPED_SUBRESOURCE mapped{};
    CheckHr(ctx->Map(objectConstantBuffer_.Get(), 0, D3D11_MAP_WRITE_DISCARD, 0, &mapped), L"モデルの定数バッファーの更新");
    auto* object = static_cast<ObjectConstants*>(mapped.pData);
    XMStoreFloat4x4(&object->worldViewProj, world * viewProj);
    XMStoreFloat4x4(&object->world, world);
    XMStoreFloat4(&object->lightDirection, kLightDirection);
    ctx->Unmap(objectConstantBuffer_.Get(), 0);

    const UINT stride = sizeof(TtMeshVertex);
    const UINT offset = 0;
    ctx->IASetInputLayout(modelInputLayout_.Get());
    ctx->IASetPrimitiveTopology(D3D11_PRIMITIVE_TOPOLOGY_TRIANGLELIST);
    ID3D11Buffer* vertexBuffer = mesh.VertexBuffer();
    ctx->IASetVertexBuffers(0, 1, &vertexBuffer, &stride, &offset);
    ctx->IASetIndexBuffer(mesh.IndexBuffer(), DXGI_FORMAT_R32_UINT, 0);
    ctx->VSSetShader(modelVertexShader_.Get(), nullptr, 0);
    ID3D11Buffer* constantBuffers[] = {objectConstantBuffer_.Get(), materialConstantBuffer_.Get()};
    ctx->VSSetConstantBuffers(0, 2, constantBuffers);
    ctx->PSSetShader(modelPixelShader_.Get(), nullptr, 0);
    ctx->PSSetConstantBuffers(0, 2, constantBuffers);
    ctx->PSSetSamplers(0, 1, textureSampler_.GetAddressOf());
    ctx->OMSetDepthStencilState(modelDepthState_.Get(), 0);
    ctx->OMSetBlendState(alphaBlendState_.Get(), nullptr, 0xFFFFFFFF);

    for (const Mesh::Material& material : mesh.Materials()) {
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
        ctx->Unmap(materialConstantBuffer_.Get(), 0);

        ID3D11ShaderResourceView* texture = material.texture ? material.texture.Get() : whiteTexture_.Get();
        ctx->PSSetShaderResources(0, 1, &texture);
        ctx->RSSetState(material.doubleSided ? cullNoneState_.Get() : cullBackState_.Get());
        ctx->DrawIndexed(material.indexCount, material.indexStart, 0);
    }

    ID3D11ShaderResourceView* none = nullptr;
    ctx->PSSetShaderResources(0, 1, &none);
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

    const D3D11_INPUT_ELEMENT_DESC layout[] = {
        {"POSITION", 0, DXGI_FORMAT_R32G32B32_FLOAT, 0, 0, D3D11_INPUT_PER_VERTEX_DATA, 0},
        {"NORMAL", 0, DXGI_FORMAT_R32G32B32_FLOAT, 0, 12, D3D11_INPUT_PER_VERTEX_DATA, 0},
        {"TEXCOORD", 0, DXGI_FORMAT_R32G32_FLOAT, 0, 24, D3D11_INPUT_PER_VERTEX_DATA, 0},
    };
    CheckHr(device_->CreateInputLayout(layout, ARRAYSIZE(layout), g_ModelVS, sizeof(g_ModelVS), &modelInputLayout_),
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

    D3D11_RASTERIZER_DESC rs{};
    rs.FillMode = D3D11_FILL_SOLID;
    rs.CullMode = D3D11_CULL_BACK; // 時計回りの面が表（MMD と同じ）
    rs.DepthClipEnable = TRUE;
    CheckHr(device_->CreateRasterizerState(&rs, &cullBackState_), L"裏面を描かない設定の作成");
    rs.CullMode = D3D11_CULL_NONE;
    CheckHr(device_->CreateRasterizerState(&rs, &cullNoneState_), L"両面を描く設定の作成");

    D3D11_DEPTH_STENCIL_DESC ds{};
    ds.DepthEnable = TRUE;
    ds.DepthWriteMask = D3D11_DEPTH_WRITE_MASK_ALL;
    ds.DepthFunc = D3D11_COMPARISON_LESS_EQUAL;
    CheckHr(device_->CreateDepthStencilState(&ds, &modelDepthState_), L"モデルの深度設定の作成");

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
}

} // namespace tt
