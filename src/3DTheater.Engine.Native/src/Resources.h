// 描画に使う資源（テクスチャとメッシュ）。エンジンが作り、エンジンより先に破棄する。
#pragma once

#include <DirectXMath.h>

#include <vector>

#include "Common.h"

namespace tt {

class Engine;

class Texture {
public:
    Texture(Engine& engine, int width, int height, const void* pixels, int stride);
    ~Texture();

    Texture(const Texture&) = delete;
    Texture& operator=(const Texture&) = delete;

    bool IsValid() const noexcept { return magic_ == kMagic; }
    Engine& OwnerEngine() const noexcept { return engine_; }
    ID3D11ShaderResourceView* View() const noexcept { return srv_.Get(); }

private:
    static constexpr uint32_t kMagic = 0x58455454; // "TTEX"

    uint32_t magic_ = kMagic;
    Engine& engine_;
    ComPtr<ID3D11ShaderResourceView> srv_;
};

class Mesh {
public:
    struct Material {
        uint32_t indexStart;
        uint32_t indexCount;
        DirectX::XMFLOAT4 diffuse;
        DirectX::XMFLOAT3 specular;
        float specularPower;
        DirectX::XMFLOAT3 ambient;
        bool doubleSided;
        ComPtr<ID3D11ShaderResourceView> texture; // 無ければ空（白として描く）
    };

    Mesh(Engine& engine, const TtMeshDesc& desc);
    ~Mesh();

    Mesh(const Mesh&) = delete;
    Mesh& operator=(const Mesh&) = delete;

    bool IsValid() const noexcept { return magic_ == kMagic; }
    Engine& OwnerEngine() const noexcept { return engine_; }

    ID3D11Buffer* VertexBuffer() const noexcept { return vertexBuffer_.Get(); }
    ID3D11Buffer* IndexBuffer() const noexcept { return indexBuffer_.Get(); }
    const std::vector<Material>& Materials() const noexcept { return materials_; }
    bool IsEmpty() const noexcept { return !vertexBuffer_ || !indexBuffer_; }

    // 頂点を囲む箱（モデルの座標で）。選択中の強調に使う
    const DirectX::XMFLOAT3& BoundsMin() const noexcept { return boundsMin_; }
    const DirectX::XMFLOAT3& BoundsMax() const noexcept { return boundsMax_; }

private:
    static constexpr uint32_t kMagic = 0x48534D54; // "TMSH"

    uint32_t magic_ = kMagic;
    Engine& engine_;
    ComPtr<ID3D11Buffer> vertexBuffer_;
    ComPtr<ID3D11Buffer> indexBuffer_;
    std::vector<Material> materials_;
    DirectX::XMFLOAT3 boundsMin_{};
    DirectX::XMFLOAT3 boundsMax_{};
};

} // namespace tt
