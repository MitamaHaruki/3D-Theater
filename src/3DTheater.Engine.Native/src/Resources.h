// 描画に使う資源（テクスチャとメッシュ）。エンジンが作り、エンジンより先に破棄する。
// GPU が使えなくなったときに作り直せるよう、GPU へ送った中身を CPU 側にも持っておく。
#pragma once

#include <DirectXMath.h>

#include <memory>
#include <vector>

#include "Engine.h"

namespace tt {

// テクスチャの画素と、GPU に載せた形。テクスチャの持ち手（Texture）とメッシュの材質が一緒に持つ
// （持ち手を先に破棄しても、メッシュが使っている間は残る）。
class TextureImage {
public:
    TextureImage(int width, int height, const void* pixels, int stride);

    ID3D11ShaderResourceView* View() const noexcept { return srv_.Get(); }

    // engine の今のデバイスで GPU に載せる（載っていれば何もしない）
    void EnsureCreated(Engine& engine);
    void Release() noexcept { srv_.Reset(); }

private:
    int width_;
    int height_;
    std::vector<uint8_t> pixels_; // BGRA 8 ビット、行の間に隙間なし
    ComPtr<ID3D11ShaderResourceView> srv_;
    uint32_t generation_ = 0;
};

class Texture final : public DeviceObject {
public:
    Texture(Engine& engine, int width, int height, const void* pixels, int stride);
    ~Texture() override;

    Texture(const Texture&) = delete;
    Texture& operator=(const Texture&) = delete;

    bool IsValid() const noexcept { return magic_ == kMagic; }
    Engine& OwnerEngine() const noexcept { return engine_; }
    const std::shared_ptr<TextureImage>& Image() const noexcept { return image_; }

    void ReleaseDeviceResources() noexcept override { image_->Release(); }
    void RestoreDeviceResources() override { image_->EnsureCreated(engine_); }

private:
    static constexpr uint32_t kMagic = 0x58455454; // "TTEX"

    uint32_t magic_ = kMagic;
    Engine& engine_;
    std::shared_ptr<TextureImage> image_;
};

class Mesh final : public DeviceObject {
public:
    struct Material {
        uint32_t indexStart;
        uint32_t indexCount;
        DirectX::XMFLOAT4 diffuse;
        DirectX::XMFLOAT3 specular;
        float specularPower;
        DirectX::XMFLOAT3 ambient;
        bool doubleSided;
        bool edge;
        bool groundShadow;
        bool castShadow;
        bool receiveShadow;
        std::shared_ptr<TextureImage> texture; // 無ければ空（白として描く）
        std::shared_ptr<TextureImage> toon;    // 無ければ陰影を付けない
        std::shared_ptr<TextureImage> sphere;
        uint32_t sphereMode = TT_SPHERE_NONE;
        float edgeSize = 0.0f;
        DirectX::XMFLOAT4 edgeColor{0.0f, 0.0f, 0.0f, 1.0f};
        DirectX::XMFLOAT4 textureMultiply{1.0f, 1.0f, 1.0f, 1.0f};
        DirectX::XMFLOAT4 textureAdd{0.0f, 0.0f, 0.0f, 0.0f};
        DirectX::XMFLOAT4 sphereMultiply{1.0f, 1.0f, 1.0f, 1.0f};
        DirectX::XMFLOAT4 sphereAdd{0.0f, 0.0f, 0.0f, 0.0f};
        DirectX::XMFLOAT4 toonMultiply{1.0f, 1.0f, 1.0f, 1.0f};
        DirectX::XMFLOAT4 toonAdd{0.0f, 0.0f, 0.0f, 0.0f};
        // 拡張材質（作ったときは MMD と同じトゥーン・発光なし）
        uint32_t shading = TT_SHADING_TOON;
        float metallic = 0.0f;
        float roughness = 0.5f;
        DirectX::XMFLOAT4 baseTint{1.0f, 1.0f, 1.0f, 1.0f};
        DirectX::XMFLOAT4 emissive{0.0f, 0.0f, 0.0f, 0.0f};
    };

    Mesh(Engine& engine, const TtMeshDesc& desc);
    ~Mesh() override;

    Mesh(const Mesh&) = delete;
    Mesh& operator=(const Mesh&) = delete;

    bool IsValid() const noexcept { return magic_ == kMagic; }
    Engine& OwnerEngine() const noexcept { return engine_; }

    ID3D11Buffer* VertexBuffer() const noexcept { return vertexBuffer_.Get(); }
    // 頂点ごとの輪郭線の太さの倍率（float 1 つずつ。変形しないので作ったときのまま）
    ID3D11Buffer* EdgeScaleBuffer() const noexcept { return edgeScaleBuffer_.Get(); }
    ID3D11Buffer* IndexBuffer() const noexcept { return indexBuffer_.Get(); }
    bool HasEdges() const noexcept { return hasEdges_; }
    bool CastsShadow() const noexcept { return castsShadow_; }
    bool HasGroundShadow() const noexcept { return hasGroundShadow_; }
    const std::vector<Material>& Materials() const noexcept { return materials_; }
    bool IsEmpty() const noexcept { return !vertexBuffer_ || !indexBuffer_; }

    // 頂点を囲む箱（モデルの座標で）。選択中の強調に使う
    const DirectX::XMFLOAT3& BoundsMin() const noexcept { return boundsMin_; }
    const DirectX::XMFLOAT3& BoundsMax() const noexcept { return boundsMax_; }

    // 変形した後の頂点で置き換える。数が作ったときと違えば TT_E_INVALID_ARG。
    // GPU が使えない間も CPU 側の中身は置き換える（作り直したときに今の形で戻る）
    void UpdateVertices(const TtMeshVertex* vertices, uint32_t count, const float* boundsMin, const float* boundsMax);
    void UpdateMaterials(const TtMaterialState* states, uint32_t count);

    void ReleaseDeviceResources() noexcept override;
    void RestoreDeviceResources() override;

private:
    static constexpr uint32_t kMagic = 0x48534D54; // "TMSH"

    void CreateBuffers();

    uint32_t magic_ = kMagic;
    Engine& engine_;
    std::vector<TtMeshVertex> vertices_;
    std::vector<float> edgeScales_;
    std::vector<uint32_t> indices_;
    ComPtr<ID3D11Buffer> vertexBuffer_;
    ComPtr<ID3D11Buffer> edgeScaleBuffer_;
    ComPtr<ID3D11Buffer> indexBuffer_;
    std::vector<Material> materials_;
    bool hasEdges_ = false;
    bool castsShadow_ = false;
    bool hasGroundShadow_ = false;
    DirectX::XMFLOAT3 boundsMin_{};
    DirectX::XMFLOAT3 boundsMax_{};
};

} // namespace tt
