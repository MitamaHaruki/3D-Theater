#include "Resources.h"

#include <DirectXPackedVector.h>

#include <algorithm>
#include <atomic>
#include <cfloat>
#include <cmath>
#include <cstring>

namespace tt {

namespace {

constexpr int kMaxTextureSize = 16384;

// メッシュの番号（作るたびに 1 つずつ増やす）
std::atomic<uint64_t> g_nextMeshId{1};

} // namespace

TextureImage::TextureImage(int width, int height, const void* pixels, int stride) : width_(width), height_(height)
{
    if (width < 1 || height < 1 || width > kMaxTextureSize || height > kMaxTextureSize) {
        throw Error(TT_E_INVALID_ARG, std::format(L"テクスチャの大きさ（{} x {}）が範囲外です", width, height));
    }
    if (!pixels || stride < width * 4) {
        throw Error(TT_E_INVALID_ARG, L"テクスチャの画素が正しくありません");
    }
    const size_t rowBytes = static_cast<size_t>(width) * 4;
    pixels_.resize(rowBytes * static_cast<size_t>(height));
    const auto* source = static_cast<const uint8_t*>(pixels);
    for (int y = 0; y < height; ++y) {
        std::memcpy(pixels_.data() + rowBytes * y, source + static_cast<size_t>(stride) * y, rowBytes);
    }
    for (size_t i = 3; i < pixels_.size(); i += 4) {
        if (pixels_[i] < 255) {
            hasTransparency_ = true;
            break;
        }
    }
}

void TextureImage::EnsureCreated(Engine& engine)
{
    if (srv_ && generation_ == engine.DeviceGeneration()) {
        return;
    }
    srv_.Reset();

    // 縮小して表示したときにちらつかないよう、縮小版（ミップマップ）を GPU で作る
    D3D11_TEXTURE2D_DESC desc{};
    desc.Width = static_cast<UINT>(width_);
    desc.Height = static_cast<UINT>(height_);
    desc.MipLevels = 0;
    desc.ArraySize = 1;
    desc.Format = DXGI_FORMAT_B8G8R8A8_UNORM;
    desc.SampleDesc.Count = 1;
    desc.Usage = D3D11_USAGE_DEFAULT;
    desc.BindFlags = D3D11_BIND_SHADER_RESOURCE | D3D11_BIND_RENDER_TARGET;
    desc.MiscFlags = D3D11_RESOURCE_MISC_GENERATE_MIPS;
    ComPtr<ID3D11Texture2D> texture;
    CheckHr(engine.Device()->CreateTexture2D(&desc, nullptr, &texture), L"テクスチャの作成");
    engine.Context()->UpdateSubresource(texture.Get(), 0, nullptr, pixels_.data(), static_cast<UINT>(width_) * 4, 0);
    CheckHr(engine.Device()->CreateShaderResourceView(texture.Get(), nullptr, &srv_), L"テクスチャのビューの作成");
    engine.Context()->GenerateMips(srv_.Get());
    generation_ = engine.DeviceGeneration();
}

Environment::Environment(Engine& engine, int width, int height, const float* rgb)
    : engine_(engine), width_(width), height_(height)
{
    using namespace DirectX;
    if (width < 1 || height < 1 || width > kMaxTextureSize || height > kMaxTextureSize) {
        throw Error(TT_E_INVALID_ARG, std::format(L"環境の画像の大きさ（{} x {}）が範囲外です", width, height));
    }
    if (!rgb) {
        throw Error(TT_E_INVALID_ARG, L"環境の画像の画素が NULL です");
    }
    // 画素を 16 ビットの浮動小数点にしながら、球面調和関数の係数を足し合わせる（点ごとの向きと、受け持つ立体角の重み）
    constexpr double kPi = 3.14159265358979323846;
    pixels_.resize(static_cast<size_t>(width) * height * 4);
    double sh[3][9] = {};
    for (int y = 0; y < height; ++y) {
        const double theta = (y + 0.5) / height * kPi;
        const double sinTheta = std::sin(theta);
        const double cosTheta = std::cos(theta);
        const double solidAngle = (2.0 * kPi / width) * (kPi / height) * sinTheta;
        for (int x = 0; x < width; ++x) {
            const double phi = ((x + 0.5) / width - 0.5) * 2.0 * kPi;
            const double dx = std::sin(phi) * sinTheta;
            const double dy = cosTheta;
            const double dz = std::cos(phi) * sinTheta;
            const double basis[9] = {0.282095, 0.488603 * dy, 0.488603 * dz, 0.488603 * dx, 1.092548 * dx * dy, 1.092548 * dy * dz,
                0.315392 * (3.0 * dy * dy - 1.0), 1.092548 * dx * dz, 0.546274 * (dx * dx - dz * dz)};
            const size_t i = static_cast<size_t>(y) * width + x;
            for (int c = 0; c < 3; ++c) {
                float value = rgb[i * 3 + c];
                value = std::isfinite(value) ? std::clamp(value, 0.0f, 65000.0f) : 0.0f;
                pixels_[i * 4 + c] = PackedVector::XMConvertFloatToHalf(value);
                for (int k = 0; k < 9; ++k) {
                    sh[c][k] += value * basis[k] * solidAngle;
                }
            }
            pixels_[i * 4 + 3] = PackedVector::XMConvertFloatToHalf(1.0f);
        }
    }
    // 照度へ（帯 0 は π、帯 1 は 2π/3、帯 2 は π/4 を掛ける）。さらに π で割り、拡散の明るさにする
    constexpr double kBand[9] = {1.0, 2.0 / 3.0, 2.0 / 3.0, 2.0 / 3.0, 0.25, 0.25, 0.25, 0.25, 0.25};
    for (int k = 0; k < 9; ++k) {
        irradiance_[k] = {static_cast<float>(sh[0][k] * kBand[k]), static_cast<float>(sh[1][k] * kBand[k]),
            static_cast<float>(sh[2][k] * kBand[k]), 0.0f};
    }
    mipCount_ = static_cast<UINT>(std::floor(std::log2(std::max(width, height)))) + 1;
    engine.ThrowIfDeviceLost();
    EnsureCreated();
    engine_.Register(*this);
}

Environment::~Environment()
{
    engine_.Unregister(*this);
    magic_ = 0;
}

void Environment::EnsureCreated()
{
    if (srv_ && generation_ == engine_.DeviceGeneration()) {
        return;
    }
    srv_.Reset();
    D3D11_TEXTURE2D_DESC desc{};
    desc.Width = static_cast<UINT>(width_);
    desc.Height = static_cast<UINT>(height_);
    desc.MipLevels = 0;
    desc.ArraySize = 1;
    desc.Format = DXGI_FORMAT_R16G16B16A16_FLOAT;
    desc.SampleDesc.Count = 1;
    desc.Usage = D3D11_USAGE_DEFAULT;
    desc.BindFlags = D3D11_BIND_SHADER_RESOURCE | D3D11_BIND_RENDER_TARGET;
    desc.MiscFlags = D3D11_RESOURCE_MISC_GENERATE_MIPS;
    ComPtr<ID3D11Texture2D> texture;
    CheckHr(engine_.Device()->CreateTexture2D(&desc, nullptr, &texture), L"環境の画像の作成");
    engine_.Context()->UpdateSubresource(texture.Get(), 0, nullptr, pixels_.data(), static_cast<UINT>(width_) * 8, 0);
    CheckHr(engine_.Device()->CreateShaderResourceView(texture.Get(), nullptr, &srv_), L"環境の画像のビューの作成");
    engine_.Context()->GenerateMips(srv_.Get());
    generation_ = engine_.DeviceGeneration();
}

Texture::Texture(Engine& engine, int width, int height, const void* pixels, int stride)
    : engine_(engine), image_(std::make_shared<TextureImage>(width, height, pixels, stride))
{
    engine.ThrowIfDeviceLost();
    image_->EnsureCreated(engine);
    engine_.Register(*this);
}

Texture::~Texture()
{
    engine_.Unregister(*this);
    magic_ = 0;
}

Mesh::Mesh(Engine& engine, const TtMeshDesc& desc) : engine_(engine), id_(g_nextMeshId++)
{
    if ((desc.vertex_count > 0 && !desc.vertices) || (desc.index_count > 0 && !desc.indices)
        || (desc.material_count > 0 && !desc.materials)) {
        throw Error(TT_E_INVALID_ARG, L"メッシュの頂点・面・材質の配列が NULL です");
    }
    if (desc.index_count % 3 != 0) {
        throw Error(TT_E_INVALID_ARG, L"面の頂点番号の数が 3 の倍数ではありません");
    }
    for (uint32_t i = 0; i < desc.index_count; ++i) {
        if (desc.indices[i] >= desc.vertex_count) {
            throw Error(TT_E_INVALID_ARG,
                std::format(L"面の頂点番号（{} 番目が {}）が頂点の数（{}）を超えています", i, desc.indices[i], desc.vertex_count));
        }
    }
    engine.ThrowIfDeviceLost();

    auto image = [&](TtTexture* handle, uint32_t index, const wchar_t* what) -> std::shared_ptr<TextureImage> {
        if (!handle) {
            return {};
        }
        auto* texture = reinterpret_cast<Texture*>(handle);
        if (!texture->IsValid() || &texture->OwnerEngine() != &engine) {
            throw Error(TT_E_INVALID_ARG, std::format(L"材質 {} 番の{}が正しくありません", index, what));
        }
        return texture->Image();
    };

    materials_.reserve(desc.material_count);
    for (uint32_t i = 0; i < desc.material_count; ++i) {
        const TtMeshMaterial& m = desc.materials[i];
        if (static_cast<uint64_t>(m.index_start) + m.index_count > desc.index_count) {
            throw Error(TT_E_INVALID_ARG, std::format(L"材質 {} 番の面の範囲が、面の頂点番号の数を超えています", i));
        }
        if (m.sphere_mode > TT_SPHERE_ADD) {
            throw Error(TT_E_INVALID_ARG, std::format(L"材質 {} 番のスフィアの使い方（{}）が正しくありません", i, m.sphere_mode));
        }
        Material material{};
        material.indexStart = m.index_start;
        material.indexCount = m.index_count;
        material.diffuse = {m.diffuse[0], m.diffuse[1], m.diffuse[2], m.diffuse[3]};
        material.specular = {m.specular[0], m.specular[1], m.specular[2]};
        material.specularPower = m.specular_power;
        material.ambient = {m.ambient[0], m.ambient[1], m.ambient[2]};
        material.doubleSided = (m.flags & TT_MATERIAL_DOUBLE_SIDED) != 0;
        material.edge = (m.flags & TT_MATERIAL_EDGE) != 0;
        material.groundShadow = (m.flags & TT_MATERIAL_GROUND_SHADOW) != 0;
        material.castShadow = (m.flags & TT_MATERIAL_CAST_SHADOW) != 0;
        material.receiveShadow = (m.flags & TT_MATERIAL_RECEIVE_SHADOW) != 0;
        material.shadowOnly = (m.flags & TT_MATERIAL_SHADOW_ONLY) != 0;
        material.texture = image(m.texture, i, L"テクスチャ");
        material.toon = image(m.toon_texture, i, L"トゥーンのテクスチャ");
        material.sphere = image(m.sphere_texture, i, L"スフィアのテクスチャ");
        material.sphereMode = material.sphere ? m.sphere_mode : TT_SPHERE_NONE;
        material.edgeSize = std::isfinite(m.edge_size) ? std::max(0.0f, m.edge_size) : 0.0f;
        material.edgeColor = {m.edge_color[0], m.edge_color[1], m.edge_color[2], m.edge_color[3]};
        hasEdges_ |= material.edge && material.indexCount > 0;
        castsShadow_ |= material.castShadow && material.indexCount > 0;
        hasGroundShadow_ |= material.groundShadow && material.indexCount > 0;
        materials_.push_back(std::move(material));
    }

    edgeScales_.assign(desc.vertex_count, 1.0f);
    if (desc.edge_scales) {
        for (uint32_t i = 0; i < desc.vertex_count; ++i) {
            const float scale = desc.edge_scales[i];
            edgeScales_[i] = std::isfinite(scale) ? std::max(0.0f, scale) : 1.0f;
        }
    }
    vertices_.assign(desc.vertices, desc.vertices + desc.vertex_count);
    indices_.assign(desc.indices, desc.indices + desc.index_count);
    CreateBuffers();

    DirectX::XMFLOAT3 minimum{FLT_MAX, FLT_MAX, FLT_MAX};
    DirectX::XMFLOAT3 maximum{-FLT_MAX, -FLT_MAX, -FLT_MAX};
    for (const TtMeshVertex& vertex : vertices_) {
        const float* p = vertex.position;
        minimum = {std::min(minimum.x, p[0]), std::min(minimum.y, p[1]), std::min(minimum.z, p[2])};
        maximum = {std::max(maximum.x, p[0]), std::max(maximum.y, p[1]), std::max(maximum.z, p[2])};
    }
    if (!vertices_.empty()) {
        boundsMin_ = minimum;
        boundsMax_ = maximum;
    }
    engine_.Register(*this);
}

void Mesh::CreateBuffers()
{
    if (vertices_.empty() || indices_.empty()) {
        return;
    }
    D3D11_BUFFER_DESC vb{};
    vb.ByteWidth = static_cast<UINT>(vertices_.size() * sizeof(TtMeshVertex));
    vb.Usage = D3D11_USAGE_DEFAULT; // 変形後の頂点を毎回書き込む
    vb.BindFlags = D3D11_BIND_VERTEX_BUFFER;
    D3D11_SUBRESOURCE_DATA vbData{vertices_.data(), 0, 0};
    CheckHr(engine_.Device()->CreateBuffer(&vb, &vbData, &vertexBuffer_), L"メッシュの頂点バッファーの作成");

    D3D11_BUFFER_DESC eb{};
    eb.ByteWidth = static_cast<UINT>(edgeScales_.size() * sizeof(float));
    eb.Usage = D3D11_USAGE_IMMUTABLE;
    eb.BindFlags = D3D11_BIND_VERTEX_BUFFER;
    D3D11_SUBRESOURCE_DATA ebData{edgeScales_.data(), 0, 0};
    CheckHr(engine_.Device()->CreateBuffer(&eb, &ebData, &edgeScaleBuffer_), L"メッシュの輪郭線の倍率のバッファーの作成");

    D3D11_BUFFER_DESC ib{};
    ib.ByteWidth = static_cast<UINT>(indices_.size() * sizeof(uint32_t));
    ib.Usage = D3D11_USAGE_IMMUTABLE;
    ib.BindFlags = D3D11_BIND_INDEX_BUFFER;
    D3D11_SUBRESOURCE_DATA ibData{indices_.data(), 0, 0};
    CheckHr(engine_.Device()->CreateBuffer(&ib, &ibData, &indexBuffer_), L"メッシュの索引バッファーの作成");
}

void Mesh::UpdateVertices(const TtMeshVertex* vertices, uint32_t count, const float* boundsMin, const float* boundsMax)
{
    if (count != vertices_.size() || (count > 0 && !vertices)) {
        throw Error(TT_E_INVALID_ARG, std::format(L"頂点の数（{}）が、メッシュを作ったときの数（{}）と違います", count, vertices_.size()));
    }
    std::memcpy(vertices_.data(), vertices, static_cast<size_t>(count) * sizeof(TtMeshVertex));
    ++vertexVersion_;
    if (vertexBuffer_ && !engine_.IsDeviceLost()) {
        engine_.Context()->UpdateSubresource(vertexBuffer_.Get(), 0, nullptr, vertices_.data(), 0, 0);
    }
    if (boundsMin && boundsMax) {
        boundsMin_ = {boundsMin[0], boundsMin[1], boundsMin[2]};
        boundsMax_ = {boundsMax[0], boundsMax[1], boundsMax[2]};
    }
}

void Mesh::UpdateMaterials(const TtMaterialState* states, uint32_t count)
{
    if (count != materials_.size() || (count > 0 && !states)) {
        throw Error(TT_E_INVALID_ARG, std::format(L"材質の数（{}）が、メッシュを作ったときの数（{}）と違います", count, materials_.size()));
    }
    ++materialVersion_;
    for (uint32_t i = 0; i < count; ++i) {
        const TtMaterialState& s = states[i];
        Material& m = materials_[i];
        m.diffuse = {s.diffuse[0], s.diffuse[1], s.diffuse[2], s.diffuse[3]};
        m.specular = {s.specular[0], s.specular[1], s.specular[2]};
        m.specularPower = s.specular_power;
        m.ambient = {s.ambient[0], s.ambient[1], s.ambient[2]};
        auto load4 = [](const float (&v)[4]) { return DirectX::XMFLOAT4{v[0], v[1], v[2], v[3]}; };
        m.textureMultiply = load4(s.texture_multiply);
        m.textureAdd = load4(s.texture_add);
        m.sphereMultiply = load4(s.sphere_multiply);
        m.sphereAdd = load4(s.sphere_add);
        m.toonMultiply = load4(s.toon_multiply);
        m.toonAdd = load4(s.toon_add);
        m.edgeColor = load4(s.edge_color);
        m.edgeSize = std::isfinite(s.edge_size) ? std::max(0.0f, s.edge_size) : 0.0f;
        // 拡張材質: 範囲の外は収め、数でない値は初期値にする
        auto fit = [](float v, float lo, float hi, float fallback) { return std::isfinite(v) ? std::clamp(v, lo, hi) : fallback; };
        m.shading = s.shading == TT_SHADING_PBR ? TT_SHADING_PBR : TT_SHADING_TOON;
        m.metallic = fit(s.metallic, 0.0f, 1.0f, 0.0f);
        m.roughness = fit(s.roughness, 0.04f, 1.0f, 0.5f);
        m.baseTint = {fit(s.base_tint[0], 0.0f, 4.0f, 1.0f), fit(s.base_tint[1], 0.0f, 4.0f, 1.0f), fit(s.base_tint[2], 0.0f, 4.0f, 1.0f), 1.0f};
        m.emissive = {fit(s.emissive[0], 0.0f, 64.0f, 0.0f), fit(s.emissive[1], 0.0f, 64.0f, 0.0f), fit(s.emissive[2], 0.0f, 64.0f, 0.0f), 0.0f};
    }
}

void Mesh::ReleaseDeviceResources() noexcept
{
    vertexBuffer_.Reset();
    edgeScaleBuffer_.Reset();
    indexBuffer_.Reset();
    for (Material& material : materials_) {
        // 持ち手を先に破棄したテクスチャも、ここで手放す
        for (const auto* image : {&material.texture, &material.toon, &material.sphere}) {
            if (*image) {
                (*image)->Release();
            }
        }
    }
}

void Mesh::RestoreDeviceResources()
{
    CreateBuffers();
    for (Material& material : materials_) {
        // 同じテクスチャを複数の材質で使っていても 1 回だけ作る
        for (const auto* image : {&material.texture, &material.toon, &material.sphere}) {
            if (*image) {
                (*image)->EnsureCreated(engine_);
            }
        }
    }
}

const RayBvh& Mesh::RayStructure()
{
    if (!rayBvh_) {
        rayBvh_ = std::make_unique<RayBvh>();
    }
    std::vector<RayBvh::Range> ranges;
    ranges.reserve(materials_.size());
    for (const Material& material : materials_) {
        ranges.push_back({material.indexStart, material.indexCount});
    }
    rayBvh_->Update(vertices_, indices_, ranges, vertexVersion_);
    return *rayBvh_;
}

Mesh::~Mesh()
{
    engine_.Unregister(*this);
    magic_ = 0;
}

} // namespace tt
