// レイトレーシング（光線を物の三角形に当てて、何に当たるかを調べる）の下準備。
// 画面の中の反射で見つからない映り込み（画面の外・物の裏・カメラの方へ戻る向き）を、映像効果の段で光線を当てて探すのに使う。
//
// - メッシュごとに、三角形を「箱の入れ子」（BVH）にまとめる（初めて使うときに作る）。頂点が変わったら箱の大きさだけを合わせ直し
//   （作り直すより速い）、動いて箱がふくらみすぎたら作り直す。箱はモデルの座標で持ち、描く物ごとの置き方（world）は GPU で掛ける
// - 1 枚の描画で描く物の箱・三角形・頂点・材質・テクスチャを、全部の物でまとめた GPU のバッファーに入れる（変わった所だけ送り直す）。
//   テクスチャは、どれも同じ大きさ（kRayTextureSize）に縮めて、1 つのテクスチャの配列に並べる
#pragma once

#include <DirectXMath.h>

#include <memory>
#include <vector>

#include "Common.h"

namespace tt {

class Engine;
class Mesh;
class TextureImage;

// 箱の入れ子の 1 つの箱（シェーダーの RayNode と同じ並び、32 バイト）。
// count が 0 なら中の箱で、子は leftOrFirst と leftOrFirst + 1。0 より大きければ端の箱で、三角形 leftOrFirst から count 個
struct RayNode {
    float minimum[3];
    uint32_t leftOrFirst;
    float maximum[3];
    uint32_t count;
};

// 三角形（シェーダーと同じ並び）: 3 つの頂点の番号と、材質の番号（どれもメッシュの中での番号）
struct RayTriangle {
    uint32_t vertex[3];
    uint32_t material;
};

// 1 つのメッシュの、三角形の箱の入れ子
class RayBvh {
public:
    // 材質ごとの面の範囲（面の頂点番号の index_start から index_count 個）
    struct Range {
        uint32_t start;
        uint32_t count;
    };

    // 今の頂点に合わせる（初めてなら作る。頂点の版が変わっていれば箱を合わせ直し、ふくらみすぎていれば作り直す）
    void Update(const std::vector<TtMeshVertex>& vertices, const std::vector<uint32_t>& indices, const std::vector<Range>& ranges,
        uint64_t vertexVersion);

    const std::vector<RayNode>& Nodes() const noexcept { return nodes_; }
    const std::vector<RayTriangle>& Triangles() const noexcept { return triangles_; }
    // 三角形の並びが変わるたび（作り直したとき）に増える
    uint64_t BuildVersion() const noexcept { return buildVersion_; }
    // 箱が変わるたび（作り直し・合わせ直し）に増える
    uint64_t NodeVersion() const noexcept { return nodeVersion_; }

private:
    void Build(const std::vector<TtMeshVertex>& vertices, const std::vector<uint32_t>& indices, const std::vector<Range>& ranges);
    void Refit(const std::vector<TtMeshVertex>& vertices);
    // 箱の表面積の合計を、いちばん外の箱の表面積で割った値（光線が通るときに調べる箱の多さの目安）
    float Cost() const noexcept;

    std::vector<RayNode> nodes_;
    std::vector<RayTriangle> triangles_;
    bool built_ = false;
    uint64_t vertexVersion_ = 0;
    uint64_t buildVersion_ = 0;
    uint64_t nodeVersion_ = 0;
    float builtCost_ = 0.0f;
};

// テクスチャを縮めて並べる配列の 1 枚の大きさ（画素）
constexpr UINT kRayTextureSize = 256;
// 1 枚の描画で光線を当てる物の数の上限（これより後ろの物には当てない）
constexpr uint32_t kMaxRayInstances = 64;

// 1 枚の描画で光線を当てる物（エンジンに 1 つ。全部のビューで使い回す）
class RayScene {
public:
    explicit RayScene(Engine& engine) : engine_(engine) {}

    RayScene(const RayScene&) = delete;
    RayScene& operator=(const RayScene&) = delete;

    // params.items（呼び出し側で確かめ済み）を GPU へ送る。前と同じ物は送り直さない。当てる物が無ければ false
    bool Prepare(const TtFrameParams& params);
    // 物の数（Prepare の後）
    uint32_t InstanceCount() const noexcept { return static_cast<uint32_t>(instances_.size()); }
    // ピクセルシェーダーの t{first} から 6 つ（物・箱・三角形・頂点・材質・テクスチャの配列）に設定する
    void Bind(ID3D11DeviceContext* context, UINT first) const;
    // GPU の資源を手放す（GPU を作り直すとき・エンジンを破棄するとき）
    void Release() noexcept;

    // 物（シェーダーの RayInstance と同じ並び）。行列は「行ベクトル × 行列」の 1〜3 列目（位置 p は dot(float4(p, 1), 列) で移す）
    struct Instance {
        DirectX::XMFLOAT4 toObject[3]; // ワールド → モデルの座標
        DirectX::XMFLOAT4 toWorld[3];  // モデルの座標 → ワールド
        DirectX::XMFLOAT4 boundsMin;   // ワールドで囲む箱
        DirectX::XMFLOAT4 boundsMax;
        uint32_t offsets[4]; // まとめたバッファーでの、箱・三角形・頂点・材質の始まり
    };

    // 材質（シェーダーの RayMaterial と同じ並び）
    struct Material {
        DirectX::XMFLOAT4 diffuse; // a = 不透明度
        DirectX::XMFLOAT4 ambient;
        DirectX::XMFLOAT4 textureMultiply;
        DirectX::XMFLOAT4 textureAdd;
        DirectX::XMFLOAT4 toonMultiply;
        DirectX::XMFLOAT4 toonAdd;
        DirectX::XMFLOAT4 baseTint; // w = 粗さ
        DirectX::XMFLOAT4 emissive; // w = 金属らしさ
        uint32_t info[4];           // x = テクスチャの配列の何枚目（無ければ kNoTexture）、y = トゥーン、z = 印（kMaterial*）
    };
    static constexpr uint32_t kNoTexture = 0xFFFFFFFFu;
    static constexpr uint32_t kMaterialDoubleSided = 1u;
    static constexpr uint32_t kMaterialAlphaTest = 2u; // テクスチャに透明な所がある（当たった所の透明度を読む）
    static constexpr uint32_t kMaterialPbr = 4u;
    static constexpr uint32_t kMaterialCastShadow = 8u; // セルフシャドウを落とす（影を調べる光線を遮る）

private:
    // まとめたバッファー（構造体の並び。足りなくなったら大きく作り直す）
    struct Buffer {
        ComPtr<ID3D11Buffer> buffer;
        ComPtr<ID3D11ShaderResourceView> srv;
        UINT capacity = 0; // 要素の数
    };
    // メッシュごとの、送った中身の版と、まとめたバッファーでの場所
    struct Entry {
        uint64_t meshId = 0;
        uint64_t buildVersion = 0;
        uint64_t nodeVersion = 0;
        uint64_t vertexVersion = 0;
        uint64_t materialVersion = 0;
        uint32_t nodeOffset = 0;
        uint32_t nodeCount = 0;
        uint32_t triangleOffset = 0;
        uint32_t triangleCount = 0;
        uint32_t vertexOffset = 0;
        uint32_t vertexCount = 0;
        uint32_t materialOffset = 0;
        uint32_t materialCount = 0;
    };
    // 縮めたテクスチャの配列の 1 枚
    struct Slot {
        std::weak_ptr<TextureImage> image;
    };

    // 足りなければ大きく作り直す（作り直したら true。中身は空になる）
    bool EnsureBuffer(Buffer& buffer, UINT count, UINT stride, const wchar_t* what);
    void Upload(const Buffer& buffer, UINT offset, UINT count, UINT stride, const void* data);
    // テクスチャの配列での番号（無ければ並べる。配列が足りなければ大きく作り直して、並べてあった物も描き直す）
    uint32_t SlotOf(const std::shared_ptr<TextureImage>& image);
    void DrawSlot(uint32_t slot, TextureImage& image);
    void UploadMaterials(Mesh& mesh, const Entry& entry);

    Engine& engine_;
    uint32_t generation_ = 0xFFFFFFFFu;
    std::vector<Entry> entries_;
    std::vector<Instance> instances_;
    std::vector<Instance> uploadedInstances_;
    Buffer instanceBuffer_;
    Buffer nodeBuffer_;
    Buffer triangleBuffer_;
    Buffer vertexBuffer_;
    Buffer materialBuffer_;
    std::vector<Slot> slots_;
    ComPtr<ID3D11Texture2D> textures_;
    ComPtr<ID3D11ShaderResourceView> texturesSrv_;
    UINT textureCapacity_ = 0;
    bool texturesDirty_ = false; // 描き足した枚があり、縮小版（ミップマップ）を作り直す
};

} // namespace tt
