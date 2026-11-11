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
#define TT_API_VERSION_MINOR 20
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
typedef struct TtEnvironment TtEnvironment; // 環境の画像（API 0.20）
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
    TT_FRAME_SELF_SHADOW = 1u << 7,          // セルフシャドウ（照明から見た奥行きの画像で、モデルに落ちる影を描く。API 0.7）
    TT_FRAME_GROUND_SHADOW = 1u << 8,        // 地面の影（高さ 0 の床へ照明の向きに押しつぶして描く。API 0.7）
    TT_FRAME_GRID_BEHIND = 1u << 9,          // 格子と座標軸を、奥行きに関係なくいちばん奥に描く（モデルの後ろ。固定視点用。API 0.8）
    TT_FRAME_CLEAR_COLOR = 1u << 10,         // 背景を clear_color で消す（a = 0 で透明。書き出し用。API 0.9）。無ければ編集画面の背景色
    TT_FRAME_FLOOR_REFLECTION = 1u << 11,    // 床の映り込み（高さ 0 の床に、モデルを鏡のように映す。reflection_*。API 0.15）
    TT_FRAME_ENVIRONMENT_BACKGROUND = 1u << 12, // 環境の画像を背景に描く（environment があるとき。平行投影では描かない。API 0.20）
};

// 映像効果（TtFrameParams.post_flags。API 0.11）。どれかを使うと、場面を背景なしで描いてから効果をかけ、背景を後ろに置く。
// 背景・格子・座標軸・選択の枠・重ね描きの線には、色調補正とブルームはかからない（周辺減光は背景にもかかる）
enum {
    TT_POST_COLOR = 1u << 0,    // 色調補正（exposure・contrast・saturation・temperature）
    TT_POST_BLOOM = 1u << 1,    // ブルーム（明るい所が周りへにじむ光。bloom_*）
    TT_POST_VIGNETTE = 1u << 2, // 周辺減光（画面の四隅を暗くする。vignette）
    // ここから API 0.12（イラスト風）。線画・塗り・影の色は背景にかからない。紙の質感は背景にもかかる
    TT_POST_LINES = 1u << 3,       // 線画（奥行きの段差と面の折れ目に、手前の物の側へ線を引く。line_*）
    TT_POST_POSTERIZE = 1u << 4,   // 塗り（明るさを posterize_levels 段に減らす。色合いは保つ）
    TT_POST_SHADOW_TINT = 1u << 5, // 影の色（暗い所ほど shadow_tint の色合いに寄せる）
    TT_POST_PAPER = 1u << 6,       // 紙の質感（細かいむらを重ねる。paper_*）
    // ここから API 0.16
    TT_POST_DOF = 1u << 7, // 被写界深度（ピントの距離の前後をぼかす。dof_*。背景もぼかす。格子・重ね描きの線はぼかさない）
    // ここから API 0.17
    TT_POST_AO = 1u << 8, // 環境光の遮り（奥行きから、物の隙間や接する所を暗くする。ao_*。被写界深度・ブルームより先にかける）
    // ここから API 0.18
    TT_POST_FOG = 1u << 9, // 霧（遠くの物ほど fog_color に溶けこませる。fog_*。背景が不透明なら背景にも。平行投影では使わない）
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
    TT_MATERIAL_GROUND_SHADOW = 1u << 2, // 地面の影を落とす（API 0.7）
    TT_MATERIAL_CAST_SHADOW = 1u << 3,   // セルフシャドウを落とす（API 0.7）
    TT_MATERIAL_RECEIVE_SHADOW = 1u << 4, // セルフシャドウを受ける（API 0.7）。受ける材質は、影の中をトゥーンの影の色で描く（MMD と同じ）
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
// 材質の色の計算（TtMaterialState.shading。API 0.13）
enum {
    TT_SHADING_TOON = 0, // MMD と同じトゥーンの色の計算（作ったときはこれ）
    TT_SHADING_PBR = 1,  // 拡張材質: 金属らしさ・粗さで光を返す。ベースカラー = 拡散色 × テクスチャ × base_tint。スフィア・トゥーンは使わない
};

// 材質の今の値（材質モーフ・拡張材質の結果）。API 0.13 で後ろに拡張材質の値を足したので、大きさが変わった
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
    // ここから API 0.13（拡張材質）
    uint32_t shading;   // TT_SHADING_*（知らない値はトゥーン）
    float metallic;     // 金属らしさ（0〜1。PBR のとき）
    float roughness;    // 粗さ（0.04〜1 に収める。PBR のとき）
    float base_tint[4]; // ベースカラーに掛ける色合い（PBR のとき。a は使わない）
    float emissive[4];  // 発光（rgb = 色 × 強さ。0 以上。どちらの色の計算にも足す。a は使わない）
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

// 足す照明の種類（TtLight.type。API 0.14）
enum {
    TT_LIGHT_DIRECTIONAL = 0, // 平行光（direction の向きへ進む光）
    TT_LIGHT_POINT = 1,       // 点光源（position から全方向。range で 0 になる）
    TT_LIGHT_SPOT = 2,        // スポットライト（position から direction の向きへの円すい。range で 0 になる）
};

#define TT_MAX_LIGHTS 8

// 足す照明（メインの照明 light_direction・light_color とは別）。トゥーンの材質には、光の当たる側を
// 材質の色 × 照明の色で明るくする（トゥーンのある材質は境目をくっきり）。PBR の材質には照り返しも含めて足す。影は落とさない。
// 数でない値を含む照明は使わない
typedef struct TtLight {
    uint32_t type;       // TT_LIGHT_*（知らない値の照明は使わない）
    float position[3];   // ワールドの位置（平行光は使わない）
    float direction[3];  // 光が進む向き（点光源は使わない。長さは問わない）
    float range;         // 届く距離（点光源・スポット。0.01 以上。ここで 0 になるよう、なめらかに弱める）
    float color[3];      // 色 × 明るさ（0 以上）
    float spot_angle;    // スポットの円すいの広がり（全体の角度、度。1〜179）
    float spot_softness; // スポットの縁のぼかし（0〜1）
    uint32_t reserved[3];
} TtLight;

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
    // ここから API 0.7
    float ground_shadow_color[4]; // 地面の影の色（a は不透明度）
    uint32_t shadow_map_size;     // セルフシャドウの奥行きの画像の 1 辺（0 なら 2048。256〜8192）
    uint32_t reserved4;
    // ここから API 0.9
    // 背景の色（TT_FRAME_CLEAR_COLOR のとき）。透明（a < 1）で描くと、読み出した色は「透明度をかけ合わせた値」になる
    // （半透明の材質を重ねた結果。PNG に書くときは色を透明度で割り戻すこと）
    float clear_color[4];
    // ここから API 0.11（映像効果。post_flags が 0 なら何もしない。範囲の外の値は範囲に収め、数でない値は「変えない」にする）
    uint32_t post_flags;   // TT_POST_*
    float exposure;        // 明るさ（段。0 で変えない。+1 で 2 倍。-5〜5）
    float contrast;        // コントラスト（1 で変えない。0〜3）
    float saturation;      // 彩度（1 で変えない。0 で白黒。0〜3）
    float temperature;     // 色温度（0 で変えない。-1 で青く、+1 で黄色く）
    float bloom_intensity; // ブルームの強さ（0〜8）
    float bloom_threshold; // 光り始める明るさ（0〜1。r・g・b のいちばん大きい値で比べる）
    float bloom_spread;    // 広がり（0〜1。大きいほど遠くまで広がる）
    float vignette;        // 周辺減光の強さ（0〜1。四隅の暗さ）
    uint32_t reserved5;
    // ここから API 0.12（イラスト風。範囲の外の値は範囲に収める）
    float line_color[3];         // 線画の色
    float line_width;            // 線画の太さ（画素。0.5〜8）
    float line_strength;         // 線画の濃さ（0〜1）
    float line_detail;           // 細かさ（0〜1。大きいほど、小さな段差・ゆるい折れ目にも線を引く）
    uint32_t posterize_levels;   // 塗りの段数（2〜16。黒と白を含む）
    float shadow_tint_strength;  // 影の色の強さ（0〜1）
    float shadow_tint[3];        // 影の色（明るさは問わない。色合いだけを使う）
    float paper_strength;        // 紙の質感の強さ（0〜1）
    float paper_grain;           // 紙の粒の大きさ（画素。0.5〜64）
    uint32_t reserved6;
    // ここから API 0.14（足す照明。最大 TT_MAX_LIGHTS 個。それより多い分は使わない）
    const TtLight* lights;  // light_count が 0 なら NULL でよい
    uint32_t light_count;
    uint32_t reserved7;
    // ここから API 0.15（床の映り込み。TT_FRAME_FLOOR_REFLECTION のとき）。モデルを高さ 0 の床で上下に映した姿を、
    // 床より下に半透明で描く（照明・影も映した向きで当てるので、鏡に映ったのと同じ見え方）。カメラが床より下なら描かない
    float reflection_strength; // 濃さ（0〜1。床のすぐ下での不透明度）
    float reflection_fade;     // 消えるまでの距離（床からの深さ。0.01 以上。ここで 0 になる）
    uint32_t reserved8[2];
    // ここから API 0.16（被写界深度。TT_POST_DOF のとき）。距離はカメラから見る向きの距離
    float dof_focus_distance; // ピントの距離
    float dof_focus_range;    // ピントの合う幅（前後の合計。0 以上）
    float dof_max_blur;       // ぼけの最大の大きさ（円の半径、画素。0〜64）
    float dof_transition;     // ピントの合う幅の外で、ぼけが最大になるまでの距離（0.01 以上）
    // ここから API 0.17（環境光の遮り。TT_POST_AO のとき）
    float ao_strength; // 強さ（0〜2。0 なら何もしない）
    float ao_radius;   // 広がり（ワールドの長さ。これより離れた物は遮らない。0.01 以上）
    // ここから API 0.18（霧。TT_POST_FOG のとき）。距離はカメラからのまっすぐの距離。
    // 霧の量 = 1 - exp(-濃さ × 霧の中を通る長さ)。fog_height が正なら、濃さを高さで薄くする（高さ 0 で fog_density、
    // fog_height 上がるごとに 1/e。地面より下は fog_density のまま）
    float fog_color[3]; // 霧の色（0〜1）
    float fog_start;    // 霧が始まる距離（0 以上）
    float fog_density;  // 濃さ（0 以上。0 なら何もしない）
    float fog_height;   // 高さの尺度（0 なら高さに関係なく同じ濃さ）
    // ここから API 0.19（柔らかな影。セルフシャドウと地面の影）。照明を大きさのある光源として、影を落とす物と受ける面が
    // 離れているほど、影の境目を広くぼかす（接している所はくっきり）。値は光源の見かけの半径の tan（0〜0.5）。
    // 0 なら今までどおり（セルフシャドウは周りの 9 か所を比べるだけ、地面の影は床へ押しつぶして描く）。0 より大きいときは、
    // 地面の影も「地面影」の材質で照明から見た奥行きの画像を作り、床に描いた 1 枚の四角形で読む（押しつぶす描き方ではぼかせないため）
    float shadow_softness;
    uint32_t reserved11;
    // ここから API 0.20（環境の画像。全方向の景色。PBR の材質の環境光と映り込みに使い、TT_FRAME_ENVIRONMENT_BACKGROUND なら
    // 背景にも描く。トゥーンの材質には使わない。NULL なら今までどおり、照明の色から作る空と地面の 2 色）
    TtEnvironment* environment;
    float environment_intensity; // 明るさ（0 以上）
    float environment_rotation;  // 景色を Y 軸のまわりに回す角度（ラジアン。XMMatrixRotationY と同じ向き）
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

// 環境の画像を作る（API 0.20）。rgb_pixels は光の強さそのまま（直線の値。1 より明るくてよい）の RGB を float 3 つずつ、
// 上の行から隙間なく（幅 × 高さ × 3 個）。全方向の景色を横 360 度・縦 180 度に広げた画像で、真ん中が +Z、右へ 4 分の 1 が +X、
// 上の端が真上。大きさは 1〜16384。数でない値・負の値は 0 にする
TT_API TtResult TT_CALL tt_environment_create(
    TtEngine* engine, int32_t width, int32_t height, const float* rgb_pixels, TtEnvironment** out_environment);
TT_API TtResult TT_CALL tt_environment_destroy(TtEnvironment* environment); // NULL なら何もせず TT_OK

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
// ここから API 0.10
// なめらかさ（MSAA。1 画素あたりの点の数）を 1・2・4・8 から選ぶ（作ったときは 1）。GPU が使えない数のときは、使える中で
// いちばん近い少ない数にする（out_actual に実際の数。NULL でよい）。中の描画先（色・奥行き）を作り直すが、
// tt_view_get_d3d9_surface の面とスワップチェーンは変わらない。GPU が使えない間は覚えておき、作り直すときに使う
TT_API TtResult TT_CALL tt_view_set_sample_count(TtView* view, uint32_t sample_count, uint32_t* out_actual);
// ウィンドウ用ビューは、前の表示が終わっていなければ待たずに TT_S_NOT_READY を返す（UI を止めないため）。
// 画面外ビューは描画を頼むだけで、GPU の完了は待たない（tt_engine_wait_for_gpu か tt_view_read_pixels で待つ）
TT_API TtResult TT_CALL tt_view_render(TtView* view, const TtFrameParams* params);

// 画面外ビューの内容を BGRA 8 ビットで読み出す。テスト・保存用で、毎フレーム呼ぶものではない
TT_API TtResult TT_CALL tt_view_read_pixels(TtView* view, void* buffer, int32_t stride, int32_t buffer_size);

#ifdef __cplusplus
}
#endif
