#include "RayTracing.h"

#include <algorithm>
#include <cfloat>
#include <cmath>
#include <cstring>
#include <numeric>

#include "Engine.h"
#include "Resources.h"

namespace tt {

namespace {

// 箱を分ける候補の数（軸ごと）と、端の箱に入れる三角形の数の上限
constexpr uint32_t kBins = 12;
constexpr uint32_t kMaxLeafTriangles = 4;
// 合わせ直した箱がふくらみ、調べる箱の多さの目安が作ったときのこの倍を超えたら作り直す
constexpr float kRebuildCostRatio = 1.6f;
// テクスチャの配列の枚数の、初めの数と上限（Direct3D 11 の配列の上限）
constexpr UINT kInitialTextureCapacity = 16;
constexpr UINT kMaxTextureCapacity = D3D11_REQ_TEXTURE2D_ARRAY_AXIS_DIMENSION;

struct Bounds {
    float lo[3] = {FLT_MAX, FLT_MAX, FLT_MAX};
    float hi[3] = {-FLT_MAX, -FLT_MAX, -FLT_MAX};

    void Grow(const float* p) noexcept
    {
        for (int k = 0; k < 3; ++k) {
            lo[k] = std::min(lo[k], p[k]);
            hi[k] = std::max(hi[k], p[k]);
        }
    }
    void Grow(const Bounds& other) noexcept
    {
        for (int k = 0; k < 3; ++k) {
            lo[k] = std::min(lo[k], other.lo[k]);
            hi[k] = std::max(hi[k], other.hi[k]);
        }
    }
    void Grow(const RayNode& node) noexcept
    {
        Grow(node.minimum);
        Grow(node.maximum);
    }
    bool IsEmpty() const noexcept { return lo[0] > hi[0]; }
    float Area() const noexcept
    {
        if (IsEmpty()) {
            return 0.0f;
        }
        const float x = hi[0] - lo[0];
        const float y = hi[1] - lo[1];
        const float z = hi[2] - lo[2];
        return 2.0f * (x * y + y * z + z * x);
    }
    void Store(RayNode& node) const noexcept
    {
        for (int k = 0; k < 3; ++k) {
            // 三角形が無い箱は、どの光線も通らない向きにしておく
            node.minimum[k] = IsEmpty() ? FLT_MAX : lo[k];
            node.maximum[k] = IsEmpty() ? -FLT_MAX : hi[k];
        }
    }
};

Bounds TriangleBounds(const std::vector<TtMeshVertex>& vertices, const RayTriangle& triangle) noexcept
{
    Bounds bounds;
    for (uint32_t v : triangle.vertex) {
        bounds.Grow(vertices[v].position);
    }
    return bounds;
}

uint32_t BinOf(float value, float low, float scale) noexcept
{
    const float bin = (value - low) * scale;
    return bin <= 0.0f ? 0u : std::min(kBins - 1, static_cast<uint32_t>(bin));
}

} // namespace

// ---- 三角形の箱の入れ子 ----

void RayBvh::Update(const std::vector<TtMeshVertex>& vertices, const std::vector<uint32_t>& indices, const std::vector<Range>& ranges,
    uint64_t vertexVersion)
{
    if (!built_) {
        Build(vertices, indices, ranges);
        vertexVersion_ = vertexVersion;
        return;
    }
    if (vertexVersion_ == vertexVersion) {
        return;
    }
    vertexVersion_ = vertexVersion;
    Refit(vertices);
    if (Cost() > builtCost_ * kRebuildCostRatio) {
        Build(vertices, indices, ranges); // 動いて箱がふくらみすぎた（光線が調べる箱が増えて遅くなる）
    }
}

void RayBvh::Build(const std::vector<TtMeshVertex>& vertices, const std::vector<uint32_t>& indices, const std::vector<Range>& ranges)
{
    // 材質の範囲にある三角形（描かない三角形には当てない）
    std::vector<RayTriangle> source;
    for (uint32_t m = 0; m < ranges.size(); ++m) {
        const uint64_t end = std::min<uint64_t>(static_cast<uint64_t>(ranges[m].start) + ranges[m].count, indices.size());
        for (uint64_t i = ranges[m].start; i + 3 <= end; i += 3) {
            source.push_back({{indices[i], indices[i + 1], indices[i + 2]}, m});
        }
    }
    const uint32_t count = static_cast<uint32_t>(source.size());
    std::vector<Bounds> triangleBounds(count);
    std::vector<float> centroids(static_cast<size_t>(count) * 3);
    for (uint32_t t = 0; t < count; ++t) {
        triangleBounds[t] = TriangleBounds(vertices, source[t]);
        for (int k = 0; k < 3; ++k) {
            centroids[t * 3 + k] = (triangleBounds[t].lo[k] + triangleBounds[t].hi[k]) * 0.5f;
        }
    }
    std::vector<uint32_t> order(count);
    std::iota(order.begin(), order.end(), 0u);

    nodes_.clear();
    nodes_.reserve(static_cast<size_t>(count) * 2 / kMaxLeafTriangles + 1);
    nodes_.push_back(RayNode{});
    struct Task {
        uint32_t node;
        uint32_t first;
        uint32_t count;
    };
    std::vector<Task> tasks{{0, 0, count}};
    while (!tasks.empty()) {
        const Task task = tasks.back();
        tasks.pop_back();
        Bounds bounds;
        Bounds centroidBounds;
        for (uint32_t i = task.first; i < task.first + task.count; ++i) {
            bounds.Grow(triangleBounds[order[i]]);
            centroidBounds.Grow(&centroids[order[i] * 3]);
        }
        bounds.Store(nodes_[task.node]);
        if (task.count <= kMaxLeafTriangles) {
            nodes_[task.node].leftOrFirst = task.first;
            nodes_[task.node].count = task.count;
            continue;
        }

        // 分け方: 軸ごとに三角形の中心を等しい幅の区画へ振り分け、区画の境目で分けたときの
        // 「箱の表面積 × 三角形の数」の合計（光線が通るときの手間の目安）がいちばん小さい所で分ける
        float bestCost = FLT_MAX;
        int bestAxis = -1;
        uint32_t bestSplit = 0;
        for (int axis = 0; axis < 3; ++axis) {
            const float extent = centroidBounds.hi[axis] - centroidBounds.lo[axis];
            if (!(extent > 1e-12f)) {
                continue;
            }
            const float scale = static_cast<float>(kBins) / extent;
            Bounds binBounds[kBins];
            uint32_t binCount[kBins]{};
            for (uint32_t i = task.first; i < task.first + task.count; ++i) {
                const uint32_t bin = BinOf(centroids[order[i] * 3 + axis], centroidBounds.lo[axis], scale);
                ++binCount[bin];
                binBounds[bin].Grow(triangleBounds[order[i]]);
            }
            float leftArea[kBins - 1];
            uint32_t leftCount[kBins - 1];
            Bounds sweep;
            uint32_t sum = 0;
            for (uint32_t b = 0; b + 1 < kBins; ++b) {
                sweep.Grow(binBounds[b]);
                sum += binCount[b];
                leftArea[b] = sweep.Area();
                leftCount[b] = sum;
            }
            sweep = Bounds{};
            sum = 0;
            for (uint32_t b = kBins - 1; b > 0; --b) {
                sweep.Grow(binBounds[b]);
                sum += binCount[b];
                const float cost = leftArea[b - 1] * static_cast<float>(leftCount[b - 1]) + sweep.Area() * static_cast<float>(sum);
                if (leftCount[b - 1] > 0 && sum > 0 && cost < bestCost) {
                    bestCost = cost;
                    bestAxis = axis;
                    bestSplit = b;
                }
            }
        }

        uint32_t middle = task.first + task.count / 2; // 中心が全部同じ所にあるときは、半分ずつに分ける
        if (bestAxis >= 0) {
            const float scale = static_cast<float>(kBins) / (centroidBounds.hi[bestAxis] - centroidBounds.lo[bestAxis]);
            const float low = centroidBounds.lo[bestAxis];
            auto* split = std::partition(order.data() + task.first, order.data() + task.first + task.count,
                [&](uint32_t t) { return BinOf(centroids[t * 3 + bestAxis], low, scale) < bestSplit; });
            const auto at = static_cast<uint32_t>(split - order.data());
            if (at > task.first && at < task.first + task.count) {
                middle = at;
            }
        }
        const auto left = static_cast<uint32_t>(nodes_.size());
        nodes_.push_back(RayNode{});
        nodes_.push_back(RayNode{});
        nodes_[task.node].leftOrFirst = left;
        nodes_[task.node].count = 0;
        tasks.push_back({left + 1, middle, task.first + task.count - middle});
        tasks.push_back({left, task.first, middle - task.first});
    }

    triangles_.resize(count);
    for (uint32_t i = 0; i < count; ++i) {
        triangles_[i] = source[order[i]];
    }
    built_ = true;
    ++buildVersion_;
    ++nodeVersion_;
    builtCost_ = Cost();
}

void RayBvh::Refit(const std::vector<TtMeshVertex>& vertices)
{
    // 子は親より後ろに並んでいるので、後ろから合わせれば、子の箱はいつも先に決まっている
    for (size_t i = nodes_.size(); i-- > 0;) {
        RayNode& node = nodes_[i];
        Bounds bounds;
        if (node.count > 0) {
            for (uint32_t t = node.leftOrFirst; t < node.leftOrFirst + node.count; ++t) {
                bounds.Grow(TriangleBounds(vertices, triangles_[t]));
            }
        } else if (node.leftOrFirst + 1 < nodes_.size()) {
            for (uint32_t child = node.leftOrFirst; child <= node.leftOrFirst + 1; ++child) {
                if (nodes_[child].minimum[0] <= nodes_[child].maximum[0]) {
                    bounds.Grow(nodes_[child]);
                }
            }
        }
        bounds.Store(node);
    }
    ++nodeVersion_;
}

float RayBvh::Cost() const noexcept
{
    if (nodes_.empty()) {
        return 0.0f;
    }
    Bounds root;
    if (nodes_[0].minimum[0] > nodes_[0].maximum[0]) {
        return 0.0f;
    }
    root.Grow(nodes_[0]);
    const float rootArea = root.Area();
    if (!(rootArea > 0.0f)) {
        return 0.0f;
    }
    double sum = 0.0;
    for (const RayNode& node : nodes_) {
        if (node.minimum[0] <= node.maximum[0]) {
            Bounds bounds;
            bounds.Grow(node);
            sum += bounds.Area();
        }
    }
    return static_cast<float>(sum / rootArea);
}

// ---- 1 枚の描画で光線を当てる物 ----

bool RayScene::Prepare(const TtFrameParams& params)
{
    using namespace DirectX;
    if (generation_ != engine_.DeviceGeneration()) {
        Release(); // GPU を作り直した（古いデバイスの資源は使えない）
        generation_ = engine_.DeviceGeneration();
    }

    // 光線を当てる物。同じメッシュを複数の物で使っていても、メッシュの中身は 1 回だけ送る
    struct Use {
        uint32_t entry;
        XMFLOAT4X4 world;
    };
    std::vector<Use> uses;
    std::vector<Mesh*> meshes;
    std::vector<Entry> layout;
    for (uint32_t i = 0; i < params.item_count && uses.size() < kMaxRayInstances; ++i) {
        auto* mesh = reinterpret_cast<Mesh*>(params.items[i].mesh);
        if (mesh->IsEmpty()) {
            continue;
        }
        const RayBvh& bvh = mesh->RayStructure();
        if (bvh.Triangles().empty()) {
            continue;
        }
        Use use{};
        std::memcpy(&use.world, params.items[i].world, sizeof(use.world));
        const float determinant = XMVectorGetX(XMMatrixDeterminant(XMLoadFloat4x4(&use.world)));
        if (!std::isfinite(determinant) || std::abs(determinant) < 1e-12f) {
            continue; // 潰れた置き方（逆に戻せない）
        }
        const auto found = std::find(meshes.begin(), meshes.end(), mesh);
        use.entry = static_cast<uint32_t>(found - meshes.begin());
        if (found == meshes.end()) {
            meshes.push_back(mesh);
            Entry entry{};
            entry.meshId = mesh->Id();
            entry.nodeCount = static_cast<uint32_t>(bvh.Nodes().size());
            entry.triangleCount = static_cast<uint32_t>(bvh.Triangles().size());
            entry.vertexCount = static_cast<uint32_t>(mesh->Vertices().size());
            entry.materialCount = static_cast<uint32_t>(mesh->Materials().size());
            layout.push_back(entry);
        }
        uses.push_back(use);
    }
    instances_.clear();
    if (uses.empty()) {
        return false;
    }

    // まとめたバッファーでの場所。並びか数が前と違えば、全部を送り直す
    uint64_t nodeTotal = 0;
    uint64_t triangleTotal = 0;
    uint64_t vertexTotal = 0;
    uint64_t materialTotal = 0;
    for (Entry& entry : layout) {
        entry.nodeOffset = static_cast<uint32_t>(nodeTotal);
        entry.triangleOffset = static_cast<uint32_t>(triangleTotal);
        entry.vertexOffset = static_cast<uint32_t>(vertexTotal);
        entry.materialOffset = static_cast<uint32_t>(materialTotal);
        nodeTotal += entry.nodeCount;
        triangleTotal += entry.triangleCount;
        vertexTotal += entry.vertexCount;
        materialTotal += entry.materialCount;
    }
    constexpr uint64_t kMaxElements = 1u << 24;
    if (nodeTotal > kMaxElements * 2 || triangleTotal > kMaxElements || vertexTotal > kMaxElements || materialTotal > kMaxElements) {
        throw Error(TT_E_OUT_OF_MEMORY, L"レイトレーシングに使う三角形・頂点が多すぎます");
    }
    bool full = layout.size() != entries_.size();
    for (size_t j = 0; !full && j < layout.size(); ++j) {
        const Entry& a = layout[j];
        const Entry& b = entries_[j];
        full = a.meshId != b.meshId || a.nodeCount != b.nodeCount || a.triangleCount != b.triangleCount || a.vertexCount != b.vertexCount
            || a.materialCount != b.materialCount;
    }
    // 1 つでも作り直したら、全部を送り直す
    const bool newNodes = EnsureBuffer(nodeBuffer_, static_cast<UINT>(nodeTotal), sizeof(RayNode), L"レイトレーシングの箱のバッファーの作成");
    const bool newTriangles =
        EnsureBuffer(triangleBuffer_, static_cast<UINT>(triangleTotal), sizeof(RayTriangle), L"レイトレーシングの三角形のバッファーの作成");
    const bool newVertices =
        EnsureBuffer(vertexBuffer_, static_cast<UINT>(vertexTotal), sizeof(TtMeshVertex), L"レイトレーシングの頂点のバッファーの作成");
    const bool newMaterials =
        EnsureBuffer(materialBuffer_, static_cast<UINT>(materialTotal), sizeof(Material), L"レイトレーシングの材質のバッファーの作成");
    full = full || newNodes || newTriangles || newVertices || newMaterials;

    for (size_t j = 0; j < layout.size(); ++j) {
        Mesh& mesh = *meshes[j];
        Entry& entry = layout[j];
        const RayBvh& bvh = mesh.RayStructure();
        entry.buildVersion = bvh.BuildVersion();
        entry.nodeVersion = bvh.NodeVersion();
        entry.vertexVersion = mesh.VertexVersion();
        entry.materialVersion = mesh.MaterialVersion();
        const Entry* old = full ? nullptr : &entries_[j];
        if (!old || old->nodeVersion != entry.nodeVersion || old->buildVersion != entry.buildVersion) {
            Upload(nodeBuffer_, entry.nodeOffset, entry.nodeCount, sizeof(RayNode), bvh.Nodes().data());
        }
        if (!old || old->buildVersion != entry.buildVersion) {
            Upload(triangleBuffer_, entry.triangleOffset, entry.triangleCount, sizeof(RayTriangle), bvh.Triangles().data());
        }
        if (!old || old->vertexVersion != entry.vertexVersion) {
            Upload(vertexBuffer_, entry.vertexOffset, entry.vertexCount, sizeof(TtMeshVertex), mesh.Vertices().data());
        }
        if (!old || old->materialVersion != entry.materialVersion) {
            UploadMaterials(mesh, entry);
        }
    }
    entries_ = std::move(layout);

    // 物ごとの置き方と、ワールドで囲む箱
    for (const Use& use : uses) {
        const Entry& entry = entries_[use.entry];
        const XMMATRIX world = XMLoadFloat4x4(&use.world);
        XMFLOAT4X4 toObject;
        XMStoreFloat4x4(&toObject, XMMatrixInverse(nullptr, world));
        Instance instance{};
        for (int c = 0; c < 3; ++c) {
            instance.toObject[c] = {toObject.m[0][c], toObject.m[1][c], toObject.m[2][c], toObject.m[3][c]};
            instance.toWorld[c] = {use.world.m[0][c], use.world.m[1][c], use.world.m[2][c], use.world.m[3][c]};
        }
        const RayNode& root = meshes[use.entry]->RayStructure().Nodes()[0];
        XMVECTOR lo = XMVectorReplicate(FLT_MAX);
        XMVECTOR hi = XMVectorReplicate(-FLT_MAX);
        for (int corner = 0; corner < 8; ++corner) {
            const XMVECTOR p = XMVector3TransformCoord(XMVectorSet((corner & 1) ? root.maximum[0] : root.minimum[0],
                (corner & 2) ? root.maximum[1] : root.minimum[1], (corner & 4) ? root.maximum[2] : root.minimum[2], 1.0f), world);
            lo = XMVectorMin(lo, p);
            hi = XMVectorMax(hi, p);
        }
        XMStoreFloat4(&instance.boundsMin, lo);
        XMStoreFloat4(&instance.boundsMax, hi);
        instance.offsets[0] = entry.nodeOffset;
        instance.offsets[1] = entry.triangleOffset;
        instance.offsets[2] = entry.vertexOffset;
        instance.offsets[3] = entry.materialOffset;
        instances_.push_back(instance);
    }
    const bool newInstances = EnsureBuffer(instanceBuffer_, kMaxRayInstances, sizeof(Instance), L"レイトレーシングの物のバッファーの作成");
    if (newInstances || instances_.size() != uploadedInstances_.size()
        || std::memcmp(instances_.data(), uploadedInstances_.data(), instances_.size() * sizeof(Instance)) != 0) {
        Upload(instanceBuffer_, 0, static_cast<UINT>(instances_.size()), sizeof(Instance), instances_.data());
        uploadedInstances_ = instances_;
    }

    if (texturesDirty_ && texturesSrv_) {
        engine_.Context()->GenerateMips(texturesSrv_.Get());
    }
    texturesDirty_ = false;
    return true;
}

void RayScene::Bind(ID3D11DeviceContext* context, UINT first) const
{
    ID3D11ShaderResourceView* const views[6] = {instanceBuffer_.srv.Get(), nodeBuffer_.srv.Get(), triangleBuffer_.srv.Get(),
        vertexBuffer_.srv.Get(), materialBuffer_.srv.Get(), texturesSrv_.Get()};
    context->PSSetShaderResources(first, 6, views);
}

void RayScene::Release() noexcept
{
    entries_.clear();
    instances_.clear();
    uploadedInstances_.clear();
    instanceBuffer_ = Buffer{};
    nodeBuffer_ = Buffer{};
    triangleBuffer_ = Buffer{};
    vertexBuffer_ = Buffer{};
    materialBuffer_ = Buffer{};
    slots_.clear();
    textures_.Reset();
    texturesSrv_.Reset();
    textureCapacity_ = 0;
    texturesDirty_ = false;
}

bool RayScene::EnsureBuffer(Buffer& buffer, UINT count, UINT stride, const wchar_t* what)
{
    count = std::max(count, 1u);
    if (buffer.buffer && buffer.capacity >= count) {
        return false;
    }
    // 少しずつ増えるときに毎回作り直さないよう、余裕を持たせる
    const UINT capacity = std::max(count, buffer.capacity + buffer.capacity / 2);
    buffer = Buffer{};
    D3D11_BUFFER_DESC desc{};
    desc.ByteWidth = capacity * stride;
    desc.Usage = D3D11_USAGE_DEFAULT;
    desc.BindFlags = D3D11_BIND_SHADER_RESOURCE;
    desc.MiscFlags = D3D11_RESOURCE_MISC_BUFFER_STRUCTURED;
    desc.StructureByteStride = stride;
    CheckHr(engine_.Device()->CreateBuffer(&desc, nullptr, &buffer.buffer), what);
    CheckHr(engine_.Device()->CreateShaderResourceView(buffer.buffer.Get(), nullptr, &buffer.srv), what);
    buffer.capacity = capacity;
    return true;
}

void RayScene::Upload(const Buffer& buffer, UINT offset, UINT count, UINT stride, const void* data)
{
    if (count == 0) {
        return;
    }
    const D3D11_BOX box{offset * stride, 0, 0, (offset + count) * stride, 1, 1};
    engine_.Context()->UpdateSubresource(buffer.buffer.Get(), 0, &box, data, 0, 0);
}

void RayScene::UploadMaterials(Mesh& mesh, const Entry& entry)
{
    std::vector<Material> data;
    data.reserve(entry.materialCount);
    for (const Mesh::Material& m : mesh.Materials()) {
        Material r{};
        r.diffuse = m.diffuse;
        r.ambient = {m.ambient.x, m.ambient.y, m.ambient.z, 0.0f};
        r.textureMultiply = m.textureMultiply;
        r.textureAdd = m.textureAdd;
        r.toonMultiply = m.toonMultiply;
        r.toonAdd = m.toonAdd;
        r.baseTint = {m.baseTint.x, m.baseTint.y, m.baseTint.z, m.roughness};
        r.emissive = {m.emissive.x, m.emissive.y, m.emissive.z, m.metallic};
        r.info[0] = m.texture ? SlotOf(m.texture) : kNoTexture;
        r.info[1] = m.toon ? SlotOf(m.toon) : kNoTexture;
        r.info[2] = (m.doubleSided ? kMaterialDoubleSided : 0u) | (m.texture && m.texture->HasTransparency() ? kMaterialAlphaTest : 0u)
            | (m.shading == TT_SHADING_PBR ? kMaterialPbr : 0u) | (m.castShadow ? kMaterialCastShadow : 0u);
        data.push_back(r);
    }
    Upload(materialBuffer_, entry.materialOffset, static_cast<UINT>(data.size()), sizeof(Material), data.data());
}

uint32_t RayScene::SlotOf(const std::shared_ptr<TextureImage>& image)
{
    uint32_t free = kNoTexture;
    for (uint32_t i = 0; i < slots_.size(); ++i) {
        const std::shared_ptr<TextureImage> live = slots_[i].image.lock();
        if (live == image) {
            return i;
        }
        if (!live && free == kNoTexture) {
            free = i; // 使っていたメッシュが無くなった枚は、使い回す
        }
    }
    if (free == kNoTexture) {
        if (slots_.size() >= kMaxTextureCapacity) {
            return kNoTexture; // 並べきれない（白として扱う）
        }
        free = static_cast<uint32_t>(slots_.size());
        slots_.push_back(Slot{});
    }
    slots_[free].image = image;
    if (free < textureCapacity_) {
        DrawSlot(free, *image);
    } else {
        // 配列が足りない: 大きく作り直して、並べてあった枚も描き直す（番号は変わらない）
        UINT capacity = std::max(textureCapacity_ * 2, kInitialTextureCapacity);
        while (capacity <= free) {
            capacity *= 2;
        }
        capacity = std::min(capacity, kMaxTextureCapacity);
        textures_.Reset();
        texturesSrv_.Reset();
        textureCapacity_ = 0;
        D3D11_TEXTURE2D_DESC desc{};
        desc.Width = kRayTextureSize;
        desc.Height = kRayTextureSize;
        desc.MipLevels = 0;
        desc.ArraySize = capacity;
        desc.Format = DXGI_FORMAT_B8G8R8A8_UNORM;
        desc.SampleDesc.Count = 1;
        desc.Usage = D3D11_USAGE_DEFAULT;
        desc.BindFlags = D3D11_BIND_SHADER_RESOURCE | D3D11_BIND_RENDER_TARGET;
        desc.MiscFlags = D3D11_RESOURCE_MISC_GENERATE_MIPS;
        CheckHr(engine_.Device()->CreateTexture2D(&desc, nullptr, &textures_), L"レイトレーシングのテクスチャの配列の作成");
        CheckHr(engine_.Device()->CreateShaderResourceView(textures_.Get(), nullptr, &texturesSrv_),
            L"レイトレーシングのテクスチャの配列のビューの作成");
        textureCapacity_ = capacity;
        for (uint32_t i = 0; i < slots_.size(); ++i) {
            if (const std::shared_ptr<TextureImage> live = slots_[i].image.lock()) {
                DrawSlot(i, *live);
            }
        }
    }
    texturesDirty_ = true;
    return free;
}

void RayScene::DrawSlot(uint32_t slot, TextureImage& image)
{
    image.EnsureCreated(engine_);
    D3D11_RENDER_TARGET_VIEW_DESC desc{};
    desc.Format = DXGI_FORMAT_B8G8R8A8_UNORM;
    desc.ViewDimension = D3D11_RTV_DIMENSION_TEXTURE2DARRAY;
    desc.Texture2DArray.MipSlice = 0;
    desc.Texture2DArray.FirstArraySlice = slot;
    desc.Texture2DArray.ArraySize = 1;
    ComPtr<ID3D11RenderTargetView> rtv;
    CheckHr(engine_.Device()->CreateRenderTargetView(textures_.Get(), &desc, &rtv), L"レイトレーシングのテクスチャの描画先の作成");
    engine_.DrawTextureInto(image.View(), rtv.Get(), kRayTextureSize, kRayTextureSize);
}

} // namespace tt
