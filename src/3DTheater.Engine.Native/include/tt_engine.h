// 3D Theater ネイティブエンジンの公開 C API。
// C# からは P/Invoke で呼び出す。ここに無い内部の型やクラスを外から直接使わないこと。
//
// 決まり（詳しくは docs/設計判断記録.md）
// - 文字列は UTF-16（wchar_t）。
// - エンジンとビューは「作ったスレッド」からだけ呼ぶ。別スレッドからは TT_E_WRONG_THREAD を返す。
// - ビュー・テクスチャ・メッシュはエンジンより先に破棄する。残っているとエンジンの破棄は TT_E_INVALID_STATE になる。
// - 構造体の先頭 struct_size には sizeof(構造体) を入れる。将来の項目追加に備えた確認用。
// - GPU が使えなくなると（取り外し・ドライバーの更新・応答なしなど）、GPU を使う操作が TT_E_DEVICE_LOST を返す。
//   tt_engine_recover で作り直すと、ビュー・テクスチャ・メッシュの持ち手はそのまま使い続けられる
//   （中身はエンジンが CPU 側に持っている分から作り直す）。D3DImage 用の面は別の物になるので取り直すこと。
#pragma once

#include <stdint.h>

#if defined(TT_BUILDING_DLL)
#define TT_API __declspec(dllexport)
#else
#define TT_API __declspec(dllimport)
#endif

#define TT_CALL __cdecl

#ifdef __cplusplus
extern "C" {
#endif

// 上位 16 ビットが大きな版、下位 16 ビットが小さな版。大きな版が違う DLL とは組み合わせない。
#define TT_API_VERSION_MAJOR 0
#define TT_API_VERSION_MINOR 6
#define TT_API_VERSION ((TT_API_VERSION_MAJOR << 16) | TT_API_VERSION_MINOR)

// 0 以上は成功、負の値は失敗。
typedef int32_t TtResult;

enum {
    TT_OK = 0,
    TT_S_NOT_READY = 1, // 成功の一種。表示側の準備ができていないため今回は描かなかった（次のフレームで再び呼ぶ）
    TT_E_INVALID_ARG = -1,
    TT_E_INVALID_STATE = -2,
    TT_E_WRONG_THREAD = -3,
    TT_E_NOT_SUPPORTED = -4,
    TT_E_OUT_OF_MEMORY = -5,
    TT_E_DEVICE_LOST = -6,
    TT_E_GRAPHICS = -7,
    TT_E_UNKNOWN = -100,
};

enum {
    TT_LOG_DEBUG = 0,
    TT_LOG_INFO = 1,
    TT_LOG_WARNING = 2,
    TT_LOG_ERROR = 3,
};

// どのスレッドから呼ばれるかは決まっていない。受け取る側で排他すること。
typedef void(TT_CALL* TtLogCallback)(int32_t level, const wchar_t* message, void* user_data);

typedef struct TtEngine TtEngine;
typedef struct TtView TtView;
typedef struct TtTexture TtTexture;
typedef struct TtMesh TtMesh;

enum {
    TT_ENGINE_FLAG_DEBUG_LAYER = 1u << 0, // Direct3D の検査機能。入っていなければ警告を出して無しで続ける
    TT_ENGINE_FLAG_USE_WARP = 1u << 1,    // GPU を使わないソフトウェア描画（テスト・診断用）
};

typedef struct TtEngineDesc {
    uint32_t struct_size;
    uint32_t flags;
} TtEngineDesc;

typedef struct TtEngineInfo {
    uint32_t struct_size;
    uint32_t feature_level; // 例: 0xb100 = 11_1
    uint32_t is_software;
    uint32_t debug_layer_enabled;
    uint64_t dedicated_video_memory; // バイト
    wchar_t adapter_name[128];
} TtEngineInfo;

typedef struct TtVideoMemoryInfo {
    uint32_t struct_size;
    uint32_t reserved;
    uint64_t usage;  // バイト
    uint64_t budget; // バイト
} TtVideoMemoryInfo;

enum {
    // WPF の D3DImage へ渡すため、Direct3D 9Ex と共有できる描画先を作る
    TT_VIEW_FLAG_SHARE_D3D9 = 1u << 0,
};

enum {
    TT_FRAME_SHOW_GRID = 1u << 0,
    TT_FRAME_SHOW_AXIS_X = 1u << 1,
    TT_FRAME_SHOW_AXIS_Y = 1u << 2,
    TT_FRAME_SHOW_AXIS_Z = 1u << 3,
    TT_FRAME_SHOW_TEST_OBJECT = 1u << 4,
    TT_FRAME_TEST_OBJECT_SELECTED = 1u << 5, // 確認用の立方体の輪郭を強調する
    TT_FRAME_USE_LIGHT = 1u << 6,            // light_direction・light_color を使う（無ければ MMD の初期の照明）
};

// ---- モデルの形（メッシュ）と材質 ----

// 頂点。法線は正規化しておく。UV は左上が (0,0)
typedef struct TtMeshVertex {
    float position[3];
    float normal[3];
    float uv[2];
} TtMeshVertex;

enum {
    TT_MATERIAL_DOUBLE_SIDED = 1u << 0, // 裏面も描く（無ければ時計回りの面を表として裏面を描かない）
    TT_MATERIAL_EDGE = 1u << 1,         // 輪郭線を描く（API 0.6）
};

enum {
    TT_SPHERE_NONE = 0,
    TT_SPHERE_MULTIPLY = 1, // 色に掛ける（.sph）
    TT_SPHERE_ADD = 2,      // 色に足す（.spa）
};

// 材質。面の頂点番号の index_start から index_count 個を、この材質で描く。材質は並び順に描く（MMD と同じ）。
// 色の計算は MMD と同じ: (拡散色 × 照明の色 + 環境色) × テクスチャ → スフィア → × トゥーン → + 反射光
typedef struct TtMeshMaterial {
    uint32_t index_start;
    uint32_t index_count;
    float diffuse[4]; // a は不透明度
    float specular[3];
    float specular_power;
    float ambient[3];
    uint32_t flags;
    // テクスチャは NULL なら無し（基本のテクスチャは白）。メッシュが中身を参照するので、作った後にテクスチャを破棄してよい
    TtTexture* texture;
    TtTexture* toon_texture;   // 影の色の段階。上が明るい側、下が暗い側（API 0.6）。無ければ陰影を付けない（MMD と同じ）
    TtTexture* sphere_texture; // スフィアマップ（API 0.6）
    uint32_t sphere_mode;      // TT_SPHERE_*
    float edge_size;           // 輪郭線の太さ（TtFrameParams の edge_pixel_scale を掛けたピクセル）
    float edge_color[4];
} TtMeshMaterial;

typedef struct TtMeshDesc {
    uint32_t struct_size;
    uint32_t vertex_count;
    const TtMeshVertex* vertices;
    const uint32_t* indices; // 3 つで 1 枚の三角形。すべて vertex_count 未満であること
    uint32_t index_count;
    uint32_t material_count;
    const TtMeshMaterial* materials;
    const float* edge_scales; // 頂点ごとの輪郭線の太さの倍率（vertex_count 個）。NULL なら全部 1（API 0.6）
} TtMeshDesc;

// 材質の今の値（材質モーフで変わった後の値）。テクスチャ・スフィア・トゥーンの色は「元の色 × multiply + add」で使う
typedef struct TtMaterialState {
    float diffuse[4];
    float specular[3];
    float specular_power;
    float ambient[3];
    uint32_t reserved;
    float texture_multiply[4];
    float texture_add[4];
    float sphere_multiply[4]; // ここから API 0.6
    float sphere_add[4];
    float toon_multiply[4];
    float toon_add[4];
    float edge_color[4];
    float edge_size;
    uint32_t reserved2[3];
} TtMaterialState;

// 重ね描きする線の頂点（2 つで 1 本）。座標はワールド
typedef struct TtLineVertex {
    float position[3];
    float color[4];
} TtLineVertex;

enum {
    TT_DRAW_SELECTED = 1u << 0, // 選択中。外接する箱の線を強調色で描く
};

typedef struct TtDrawItem {
    TtMesh* mesh;
    float world[16];
    uint32_t flags;
    uint32_t reserved;
} TtDrawItem;

// 行列はすべて「行ベクトル × 行列」の並び（DirectXMath・System.Numerics と同じ）で、16 個の float を行ごとに並べる。
// 座標は左手系・Y が上。
typedef struct TtFrameParams {
    uint32_t struct_size;
    uint32_t flags;
    float view[16];
    float projection[16];
    float grid_world[16];        // XZ 平面の格子をどこへ置くか（固定視点では画面に平行な面へ回す）
    float test_object_world[16]; // 原点中心・1 辺 1 の確認用の立方体の置き方
    const TtDrawItem* items;     // 描くモデル（並び順に描く）。item_count が 0 なら NULL でよい
    uint32_t item_count;
    uint32_t reserved;
    const TtLineVertex* overlay_lines; // 最後に、奥行きに関係なく手前へ重ねて描く線（ボーンの表示など）
    uint32_t overlay_vertex_count;     // 2 の倍数
    uint32_t reserved2;
    // ここから API 0.6
    float light_direction[3]; // 光が進む向き（ワールド）。TT_FRAME_USE_LIGHT のときだけ使う。長さは問わない
    float edge_pixel_scale;   // 材質の輪郭線の太さ 1 を何ピクセルにするか。0 なら輪郭線を描かない
    float light_color[3];     // 照明の色（MMD の初期値は 154/255）。TT_FRAME_USE_LIGHT のときだけ使う
    uint32_t reserved3;
} TtFrameParams;

TT_API uint32_t TT_CALL tt_get_api_version(void);
TT_API void TT_CALL tt_set_log_callback(TtLogCallback callback, void* user_data);

// 呼び出したスレッドで最後に起きたエラーの説明をコピーする。戻り値は説明の文字数（終端を含まない）。
TT_API int32_t TT_CALL tt_get_last_error_message(wchar_t* buffer, int32_t capacity);

TT_API TtResult TT_CALL tt_engine_create(const TtEngineDesc* desc, TtEngine** out_engine);
TT_API TtResult TT_CALL tt_engine_destroy(TtEngine* engine); // NULL なら何もせず TT_OK
TT_API TtResult TT_CALL tt_engine_get_info(TtEngine* engine, TtEngineInfo* out_info);
TT_API TtResult TT_CALL tt_engine_query_video_memory(TtEngine* engine, TtVideoMemoryInfo* out_info);
// それまでに頼んだ描画を GPU が終えるまで待つ。共有ビューを全部描いてから 1 回だけ呼び、WPF へ渡す
TT_API TtResult TT_CALL tt_engine_wait_for_gpu(TtEngine* engine);

// GPU が使える状態なら TT_OK、使えなくなっていれば TT_E_DEVICE_LOST
TT_API TtResult TT_CALL tt_engine_check_device(TtEngine* engine);
// デバイスを作り直し、このエンジンで作ったビュー・テクスチャ・メッシュの GPU の資源も全部作り直す。
// 失敗したら使えない状態のまま残る（少し待ってからもう一度呼べる）。使える状態で呼んでも作り直す
TT_API TtResult TT_CALL tt_engine_recover(TtEngine* engine);
// 試験・診断用。GPU が使えなくなったことにする（tt_engine_recover まで TT_E_DEVICE_LOST を返す）
TT_API TtResult TT_CALL tt_engine_simulate_device_lost(TtEngine* engine);

// テクスチャ。画素は BGRA 8 ビット（透明度はかけ合わせていない値）、上の行から。縮小表示用の段階（ミップマップ）も作る
TT_API TtResult TT_CALL tt_texture_create(
    TtEngine* engine, int32_t width, int32_t height, const void* bgra_pixels, int32_t stride, TtTexture** out_texture);
TT_API TtResult TT_CALL tt_texture_destroy(TtTexture* texture); // NULL なら何もせず TT_OK

// メッシュ。頂点番号と材質の範囲は作るときに確かめ、範囲外なら TT_E_INVALID_ARG
TT_API TtResult TT_CALL tt_mesh_create(TtEngine* engine, const TtMeshDesc* desc, TtMesh** out_mesh);
TT_API TtResult TT_CALL tt_mesh_destroy(TtMesh* mesh); // NULL なら何もせず TT_OK
// 変形した後の頂点で置き換える（数は作ったときと同じ）。bounds_min / bounds_max は選択の箱に使う囲み（NULL なら変えない）。
// GPU が使えない間も TT_OK（中身を覚えておき、作り直したときに使う）
TT_API TtResult TT_CALL tt_mesh_update_vertices(
    TtMesh* mesh, const TtMeshVertex* vertices, uint32_t vertex_count, const float* bounds_min, const float* bounds_max);
// 材質の今の値を置き換える（数は作ったときと同じ）
TT_API TtResult TT_CALL tt_mesh_update_materials(TtMesh* mesh, const TtMaterialState* states, uint32_t material_count);

// parent_hwnd の子ウィンドウを作り、そこへ直接描画するビュー（WPF の HwndHost 用）
TT_API TtResult TT_CALL tt_view_create_for_window(
    TtEngine* engine, void* parent_hwnd, int32_t width, int32_t height, TtView** out_view);
// 画面に出さない描画先を持つビュー。TT_VIEW_FLAG_SHARE_D3D9 で WPF の D3DImage 用になる
TT_API TtResult TT_CALL tt_view_create_offscreen(
    TtEngine* engine, int32_t width, int32_t height, uint32_t flags, TtView** out_view);
TT_API TtResult TT_CALL tt_view_destroy(TtView* view); // NULL なら何もせず TT_OK

TT_API TtResult TT_CALL tt_view_get_window(TtView* view, void** out_hwnd);
// 大きさを変えると別の面になる。tt_view_resize の後に取り直すこと
TT_API TtResult TT_CALL tt_view_get_d3d9_surface(TtView* view, void** out_surface);

// 幅か高さが 0 のときは描画を止める（最小化など）。tt_view_render は何もせず TT_OK
TT_API TtResult TT_CALL tt_view_resize(TtView* view, int32_t width, int32_t height);
// ウィンドウ用ビューは、前の表示が終わっていなければ待たずに TT_S_NOT_READY を返す（UI を止めないため）。
// 画面外ビューは描画を頼むだけで、GPU の完了は待たない（tt_engine_wait_for_gpu か tt_view_read_pixels で待つ）
TT_API TtResult TT_CALL tt_view_render(TtView* view, const TtFrameParams* params);

// 画面外ビューの内容を BGRA 8 ビットで読み出す。テスト・保存用で、毎フレーム呼ぶものではない
TT_API TtResult TT_CALL tt_view_read_pixels(TtView* view, void* buffer, int32_t stride, int32_t buffer_size);

#ifdef __cplusplus
}
#endif
