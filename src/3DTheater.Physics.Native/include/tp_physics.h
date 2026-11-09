// 3D Theater の物理演算（Bullet Physics を包んだもの）の公開 C API。C# からは P/Invoke で呼び出す。
//
// 決まり（詳しくは docs/設計判断記録.md）
// - 座標は MMD と同じ左手系・Y が上。そのまま Bullet に渡す（Bullet の計算は座標系の向きに依らない）。
// - 回転は四元数（x, y, z, w）。System.Numerics の Quaternion と同じ。
// - 1 つの世界（TpWorld）は、同時には 1 つのスレッドからだけ使う（どのスレッドでもよい）。
// - 剛体は追加した順に 0, 1, 2… の番号で呼ぶ。姿勢の受け渡しは、全剛体の分を番号順に並べた配列で行う。
// - 構造体の先頭 struct_size には sizeof(構造体) を入れる。
#pragma once

#include <stdint.h>
#include <wchar.h>

#if defined(TP_BUILDING_DLL)
#define TP_API __declspec(dllexport)
#else
#define TP_API __declspec(dllimport)
#endif

#define TP_CALL __cdecl

#ifdef __cplusplus
extern "C" {
#endif

#define TP_API_VERSION_MAJOR 0
#define TP_API_VERSION_MINOR 1
#define TP_API_VERSION ((TP_API_VERSION_MAJOR << 16) | TP_API_VERSION_MINOR)

typedef int32_t TpResult;

enum {
    TP_OK = 0,
    TP_E_INVALID_ARG = -1,
    TP_E_INVALID_STATE = -2,
    TP_E_OUT_OF_MEMORY = -5,
    TP_E_UNKNOWN = -100,
};

typedef struct TpWorld TpWorld;

// 置き方。rotation は四元数（x, y, z, w）
typedef struct TpTransform {
    float position[3];
    float rotation[4];
} TpTransform;

enum {
    TP_SHAPE_SPHERE = 0,  // size[0] = 半径
    TP_SHAPE_BOX = 1,     // size = 各軸の半分の長さ
    TP_SHAPE_CAPSULE = 2, // size[0] = 半径、size[1] = 円柱の部分の高さ（Y 軸の向き）
};

enum {
    TP_BODY_KINEMATIC = 0, // 外から置き方を与える（ボーンに追従）。ほかの剛体を押すが、押されない
    TP_BODY_DYNAMIC = 1,   // 重力とジョイントで動く
};

typedef struct TpBodyDesc {
    uint32_t shape;
    uint32_t kind;
    float size[3];
    float mass; // 動く剛体だけで使う。0 以下は小さな値にする
    TpTransform transform;
    float linear_damping;
    float angular_damping;
    float restitution;
    float friction;
    uint32_t group; // 自分のグループのビット（1 << 番号）
    uint32_t mask;  // ぶつかる相手のグループのビット
} TpBodyDesc;

// 6 自由度のばね付きジョイント（MMD の標準）。transform はジョイントの置き方（世界の座標）。
// 制限は下限 > 上限なら自由、等しければ固定。ばねの強さが 0 の軸はばねを使わない
typedef struct TpJointDesc {
    int32_t body_a;
    int32_t body_b;
    TpTransform transform;
    float linear_lower[3];
    float linear_upper[3];
    float angular_lower[3]; // ラジアン
    float angular_upper[3];
    float linear_stiffness[3];
    float angular_stiffness[3];
} TpJointDesc;

typedef struct TpWorldDesc {
    uint32_t struct_size;
    float gravity[3];
} TpWorldDesc;

TP_API uint32_t TP_CALL tp_get_api_version(void);

// 呼び出したスレッドで最後に起きたエラーの説明をコピーする。戻り値は説明の文字数（終端を含まない）。
TP_API int32_t TP_CALL tp_get_last_error_message(wchar_t* buffer, int32_t capacity);

TP_API TpResult TP_CALL tp_world_create(const TpWorldDesc* desc, TpWorld** out_world);
TP_API TpResult TP_CALL tp_world_destroy(TpWorld* world); // NULL なら何もせず TP_OK

// 剛体を足す（番号は今の数の続きから）。ジョイントより先に足す
TP_API TpResult TP_CALL tp_world_add_bodies(TpWorld* world, const TpBodyDesc* bodies, int32_t count);
// ジョイントを足す。剛体の今の置き方を基準にするので、剛体を初期の置き方で足した直後に呼ぶ
TP_API TpResult TP_CALL tp_world_add_joints(TpWorld* world, const TpJointDesc* joints, int32_t count);
TP_API TpResult TP_CALL tp_world_set_gravity(TpWorld* world, const float gravity[3]);
TP_API int32_t TP_CALL tp_world_body_count(TpWorld* world);

// 全剛体を与えた置き方へ動かし、速さを 0 にする（フレームを飛ばしたときなど）。count は剛体の数と同じ
TP_API TpResult TP_CALL tp_world_reset(TpWorld* world, const TpTransform* transforms, int32_t count);
// time_step 秒進める。substeps 回に分けて計算し、外から置き方を与える剛体は、前の目標から targets[i] まで
// 少しずつ動かす（動く剛体の分の targets は使わない）。count は剛体の数と同じ
TP_API TpResult TP_CALL tp_world_step(
    TpWorld* world, const TpTransform* targets, int32_t count, float time_step, int32_t substeps);
// 全剛体の今の置き方を読み出す
TP_API TpResult TP_CALL tp_world_get_transforms(TpWorld* world, TpTransform* out_transforms, int32_t count);

#ifdef __cplusplus
}
#endif
