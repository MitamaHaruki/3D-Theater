// 物理の世界 1 つ（Bullet の btDiscreteDynamicsWorld と、その中の剛体・ジョイント）。
#pragma once

#include <memory>
#include <string>
#include <vector>

#include <btBulletDynamicsCommon.h>

#include "tp_physics.h"

namespace tp {

// 内部で起きた失敗。C API の入口で受け止めて TpResult とエラー説明に変換する。
class Error {
public:
    Error(TpResult result, std::wstring message) : result_(result), message_(std::move(message)) {}

    TpResult Result() const noexcept { return result_; }
    const std::wstring& Message() const noexcept { return message_; }

private:
    TpResult result_;
    std::wstring message_;
};

class World {
public:
    explicit World(const TpWorldDesc& desc);
    ~World();

    World(const World&) = delete;
    World& operator=(const World&) = delete;

    bool IsValid() const noexcept { return magic_ == kMagic; }
    int BodyCount() const noexcept { return static_cast<int>(bodies_.size()); }

    void AddBodies(const TpBodyDesc* bodies, int count);
    void AddJoints(const TpJointDesc* joints, int count);
    void SetGravity(const float gravity[3]);
    void Reset(const TpTransform* transforms, int count);
    void Step(const TpTransform* targets, int count, float timeStep, int substeps);
    void GetTransforms(TpTransform* out, int count) const;
    void ApplyImpulses(const TpImpulse* impulses, int count);
    void SetFloor(const TpFloorDesc& desc);

private:
    static constexpr uint32_t kMagic = 0x44575054; // "TPWD"

    struct Body {
        std::unique_ptr<btCollisionShape> shape;
        std::unique_ptr<btRigidBody> body;
        bool kinematic = false;
        btTransform previousTarget; // 外から置き方を与える剛体の、前の目標
    };

    void RequireCount(int count, const wchar_t* what) const;

    uint32_t magic_ = kMagic;
    std::unique_ptr<btDefaultCollisionConfiguration> configuration_;
    std::unique_ptr<btCollisionDispatcher> dispatcher_;
    std::unique_ptr<btBroadphaseInterface> broadphase_;
    std::unique_ptr<btSequentialImpulseConstraintSolver> solver_;
    std::unique_ptr<btDiscreteDynamicsWorld> world_;
    std::vector<Body> bodies_;
    std::vector<std::unique_ptr<btTypedConstraint>> joints_;
    std::unique_ptr<btCollisionShape> floorShape_; // 床（無ければ null）
    std::unique_ptr<btRigidBody> floor_;
};

} // namespace tp
