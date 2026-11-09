#include "World.h"

#include <algorithm>
#include <cmath>
#include <format>

namespace tp {

namespace {

constexpr float kTiny = 1e-3f;

bool IsFinite(const float* values, int count) noexcept
{
    for (int i = 0; i < count; ++i) {
        if (!std::isfinite(values[i])) {
            return false;
        }
    }
    return true;
}

btVector3 ToVector(const float (&v)[3]) noexcept
{
    return btVector3(v[0], v[1], v[2]);
}

btTransform ToTransform(const TpTransform& t)
{
    if (!IsFinite(t.position, 3) || !IsFinite(t.rotation, 4)) {
        throw Error(TP_E_INVALID_ARG, L"置き方に数でない値があります");
    }
    btQuaternion rotation(t.rotation[0], t.rotation[1], t.rotation[2], t.rotation[3]);
    const btScalar length = rotation.length();
    rotation = length > btScalar(1e-6) ? rotation / length : btQuaternion::getIdentity();
    return btTransform(rotation, btVector3(t.position[0], t.position[1], t.position[2]));
}

TpTransform FromTransform(const btTransform& t) noexcept
{
    const btVector3& p = t.getOrigin();
    const btQuaternion q = t.getRotation();
    return TpTransform{{static_cast<float>(p.x()), static_cast<float>(p.y()), static_cast<float>(p.z())},
        {static_cast<float>(q.x()), static_cast<float>(q.y()), static_cast<float>(q.z()), static_cast<float>(q.w())}};
}

btTransform Interpolate(const btTransform& from, const btTransform& to, btScalar t)
{
    const btVector3 position = from.getOrigin().lerp(to.getOrigin(), t);
    const btQuaternion rotation = from.getRotation().slerp(to.getRotation(), t);
    return btTransform(rotation, position);
}

std::unique_ptr<btCollisionShape> CreateShape(const TpBodyDesc& desc)
{
    const float x = std::max(desc.size[0], kTiny);
    const float y = std::max(desc.size[1], kTiny);
    const float z = std::max(desc.size[2], kTiny);
    switch (desc.shape) {
    case TP_SHAPE_SPHERE:
        return std::make_unique<btSphereShape>(x);
    case TP_SHAPE_BOX:
        return std::make_unique<btBoxShape>(btVector3(x, y, z));
    case TP_SHAPE_CAPSULE:
        return std::make_unique<btCapsuleShape>(x, std::max(desc.size[1], 0.0f));
    default:
        throw Error(TP_E_INVALID_ARG, std::format(L"剛体の形（{}）が正しくありません", desc.shape));
    }
}

} // namespace

World::World(const TpWorldDesc& desc)
{
    configuration_ = std::make_unique<btDefaultCollisionConfiguration>();
    dispatcher_ = std::make_unique<btCollisionDispatcher>(configuration_.get());
    broadphase_ = std::make_unique<btDbvtBroadphase>();
    solver_ = std::make_unique<btSequentialImpulseConstraintSolver>();
    world_ = std::make_unique<btDiscreteDynamicsWorld>(dispatcher_.get(), broadphase_.get(), solver_.get(), configuration_.get());
    SetGravity(desc.gravity);
}

World::~World()
{
    // ジョイント → 剛体 → 世界の順に外す（世界が先に消えると、外すときに古い世界を触ってしまう）
    for (auto& joint : joints_) {
        world_->removeConstraint(joint.get());
    }
    joints_.clear();
    for (Body& body : bodies_) {
        world_->removeRigidBody(body.body.get());
    }
    bodies_.clear();
    world_.reset();
    magic_ = 0;
}

void World::SetGravity(const float gravity[3])
{
    if (!gravity || !IsFinite(gravity, 3)) {
        throw Error(TP_E_INVALID_ARG, L"重力が正しくありません");
    }
    world_->setGravity(btVector3(gravity[0], gravity[1], gravity[2]));
}

void World::RequireCount(int count, const wchar_t* what) const
{
    if (count != BodyCount()) {
        throw Error(TP_E_INVALID_ARG, std::format(L"{}の数（{}）が剛体の数（{}）と違います", what, count, BodyCount()));
    }
}

void World::AddBodies(const TpBodyDesc* descs, int count)
{
    if (count < 0 || (count > 0 && !descs)) {
        throw Error(TP_E_INVALID_ARG, L"剛体の一覧が正しくありません");
    }
    bodies_.reserve(bodies_.size() + static_cast<size_t>(count));
    for (int i = 0; i < count; ++i) {
        const TpBodyDesc& desc = descs[i];
        if (desc.kind != TP_BODY_KINEMATIC && desc.kind != TP_BODY_DYNAMIC) {
            throw Error(TP_E_INVALID_ARG, std::format(L"剛体 {} 番の種類（{}）が正しくありません", BodyCount(), desc.kind));
        }
        if (!IsFinite(desc.size, 3) || !std::isfinite(desc.mass) || !std::isfinite(desc.linear_damping)
            || !std::isfinite(desc.angular_damping) || !std::isfinite(desc.restitution) || !std::isfinite(desc.friction)) {
            throw Error(TP_E_INVALID_ARG, std::format(L"剛体 {} 番に数でない値があります", BodyCount()));
        }
        Body body;
        body.shape = CreateShape(desc);
        body.kinematic = desc.kind == TP_BODY_KINEMATIC;
        const btTransform transform = ToTransform(desc.transform);

        const btScalar mass = body.kinematic ? btScalar(0) : std::max(desc.mass, kTiny);
        btVector3 inertia(0, 0, 0);
        if (mass > 0) {
            body.shape->calculateLocalInertia(mass, inertia);
        }
        btRigidBody::btRigidBodyConstructionInfo info(mass, nullptr, body.shape.get(), inertia);
        info.m_startWorldTransform = transform;
        info.m_linearDamping = std::clamp(desc.linear_damping, 0.0f, 1.0f);
        info.m_angularDamping = std::clamp(desc.angular_damping, 0.0f, 1.0f);
        info.m_restitution = desc.restitution;
        info.m_friction = desc.friction;
        info.m_additionalDamping = true; // MMD（saba）と同じ
        body.body = std::make_unique<btRigidBody>(info);
        body.body->setSleepingThresholds(0.01f, SIMD_RADS_PER_DEG * 0.1f);
        body.body->setActivationState(DISABLE_DEACTIVATION); // 止まって眠ったまま動かなくなるのを防ぐ
        if (body.kinematic) {
            body.body->setCollisionFlags(body.body->getCollisionFlags() | btCollisionObject::CF_KINEMATIC_OBJECT);
        }
        body.previousTarget = transform;
        world_->addRigidBody(body.body.get(), static_cast<int>(desc.group & 0xFFFF), static_cast<int>(desc.mask & 0xFFFF));
        bodies_.push_back(std::move(body));
    }
}

void World::AddJoints(const TpJointDesc* descs, int count)
{
    if (count < 0 || (count > 0 && !descs)) {
        throw Error(TP_E_INVALID_ARG, L"ジョイントの一覧が正しくありません");
    }
    for (int i = 0; i < count; ++i) {
        const TpJointDesc& desc = descs[i];
        if (desc.body_a < 0 || desc.body_a >= BodyCount() || desc.body_b < 0 || desc.body_b >= BodyCount()
            || desc.body_a == desc.body_b) {
            throw Error(TP_E_INVALID_ARG,
                std::format(L"ジョイントがつなぐ剛体（{} と {}）が正しくありません", desc.body_a, desc.body_b));
        }
        const float* values[] = {desc.linear_lower, desc.linear_upper, desc.angular_lower, desc.angular_upper,
            desc.linear_stiffness, desc.angular_stiffness};
        for (const float* v : values) {
            if (!IsFinite(v, 3)) {
                throw Error(TP_E_INVALID_ARG, L"ジョイントに数でない値があります");
            }
        }
        btRigidBody& a = *bodies_[static_cast<size_t>(desc.body_a)].body;
        btRigidBody& b = *bodies_[static_cast<size_t>(desc.body_b)].body;
        const btTransform joint = ToTransform(desc.transform);
        const btTransform frameA = a.getWorldTransform().inverse() * joint;
        const btTransform frameB = b.getWorldTransform().inverse() * joint;
        auto constraint = std::make_unique<btGeneric6DofSpringConstraint>(a, b, frameA, frameB, true);
        constraint->setLinearLowerLimit(ToVector(desc.linear_lower));
        constraint->setLinearUpperLimit(ToVector(desc.linear_upper));
        constraint->setAngularLowerLimit(ToVector(desc.angular_lower));
        constraint->setAngularUpperLimit(ToVector(desc.angular_upper));
        for (int axis = 0; axis < 3; ++axis) {
            if (desc.linear_stiffness[axis] != 0) {
                constraint->enableSpring(axis, true);
                constraint->setStiffness(axis, desc.linear_stiffness[axis]);
            }
            if (desc.angular_stiffness[axis] != 0) {
                constraint->enableSpring(axis + 3, true);
                constraint->setStiffness(axis + 3, desc.angular_stiffness[axis]);
            }
        }
        world_->addConstraint(constraint.get());
        joints_.push_back(std::move(constraint));
    }
}

void World::Reset(const TpTransform* transforms, int count)
{
    RequireCount(count, L"置き方");
    if (count > 0 && !transforms) {
        throw Error(TP_E_INVALID_ARG, L"置き方の配列が NULL です");
    }
    for (int i = 0; i < count; ++i) {
        Body& body = bodies_[static_cast<size_t>(i)];
        const btTransform t = ToTransform(transforms[i]);
        btRigidBody& rb = *body.body;
        rb.setWorldTransform(t);
        rb.setInterpolationWorldTransform(t);
        rb.setLinearVelocity(btVector3(0, 0, 0));
        rb.setAngularVelocity(btVector3(0, 0, 0));
        rb.setInterpolationLinearVelocity(btVector3(0, 0, 0));
        rb.setInterpolationAngularVelocity(btVector3(0, 0, 0));
        rb.clearForces();
        body.previousTarget = t;
        // 前の当たりの記録を消す（飛んだ先で古い接触が残らないように）
        world_->getBroadphase()->getOverlappingPairCache()->cleanProxyFromPairs(rb.getBroadphaseHandle(), dispatcher_.get());
    }
    world_->updateAabbs();
}

void World::Step(const TpTransform* targets, int count, float timeStep, int substeps)
{
    RequireCount(count, L"目標の置き方");
    if (count > 0 && !targets) {
        throw Error(TP_E_INVALID_ARG, L"目標の置き方の配列が NULL です");
    }
    if (!std::isfinite(timeStep) || timeStep < 0 || timeStep > 60) {
        throw Error(TP_E_INVALID_ARG, L"進める時間が正しくありません");
    }
    if (substeps < 1 || substeps > 100000) {
        throw Error(TP_E_INVALID_ARG, L"分ける回数が正しくありません");
    }
    if (timeStep == 0) {
        return;
    }

    std::vector<btTransform> goals(static_cast<size_t>(count));
    for (int i = 0; i < count; ++i) {
        if (bodies_[static_cast<size_t>(i)].kinematic) {
            goals[static_cast<size_t>(i)] = ToTransform(targets[i]);
        }
    }
    const btScalar h = timeStep / static_cast<btScalar>(substeps);
    for (int s = 1; s <= substeps; ++s) {
        const btScalar t = static_cast<btScalar>(s) / static_cast<btScalar>(substeps);
        for (int i = 0; i < count; ++i) {
            Body& body = bodies_[static_cast<size_t>(i)];
            if (body.kinematic) {
                // 外から与える置き方は、前の目標から少しずつ動かす（速さもそこから決まる）
                body.body->setWorldTransform(Interpolate(body.previousTarget, goals[static_cast<size_t>(i)], t));
            }
        }
        world_->stepSimulation(h, 0); // 0 = 渡した時間でちょうど 1 回進める
    }
    for (int i = 0; i < count; ++i) {
        Body& body = bodies_[static_cast<size_t>(i)];
        if (body.kinematic) {
            body.previousTarget = goals[static_cast<size_t>(i)];
        }
    }
}

void World::GetTransforms(TpTransform* out, int count) const
{
    RequireCount(count, L"読み出し先");
    if (count > 0 && !out) {
        throw Error(TP_E_INVALID_ARG, L"読み出し先が NULL です");
    }
    for (int i = 0; i < count; ++i) {
        out[i] = FromTransform(bodies_[static_cast<size_t>(i)].body->getWorldTransform());
    }
}

} // namespace tp
