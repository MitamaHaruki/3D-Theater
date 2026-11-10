#include "Resources.h"

#include <algorithm>
#include <cfloat>
#include <cmath>
#include <cstring>

namespace tt {

namespace {

constexpr int kMaxTextureSize = 16384;

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

Mesh::Mesh(Engine& engine, const TtMeshDesc& desc) : engine_(engine)
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

Mesh::~Mesh()
{
    engine_.Unregister(*this);
    magic_ = 0;
}

} // namespace tt
