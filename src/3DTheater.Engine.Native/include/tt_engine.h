// 3D Theater ネイティブエンジンの公開 C API。
// C# からは P/Invoke で呼び出す。ここに無い内部の型やクラスを外から直接使わないこと。
//
// 決まり（詳しくは docs/設計判断記録.md）
// - 文字列は UTF-16（wchar_t）。
// - エンジンとビューは「作ったスレッド」からだけ呼ぶ。別スレッドからは TT_E_WRONG_THREAD を返す。
// - ビューはエンジンより先に破棄する。ビューが残っているとエンジンの破棄は TT_E_INVALID_STATE になる。
// - 構造体の先頭 struct_size には sizeof(構造体) を入れる。将来の項目追加に備えた確認用。
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
#define TT_API_VERSION_MINOR 1
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

typedef struct TtFrameParams {
    uint32_t struct_size;
    float time_seconds; // Phase 0 の確認用の立方体の回転に使う
} TtFrameParams;

TT_API uint32_t TT_CALL tt_get_api_version(void);
TT_API void TT_CALL tt_set_log_callback(TtLogCallback callback, void* user_data);

// 呼び出したスレッドで最後に起きたエラーの説明をコピーする。戻り値は説明の文字数（終端を含まない）。
TT_API int32_t TT_CALL tt_get_last_error_message(wchar_t* buffer, int32_t capacity);

TT_API TtResult TT_CALL tt_engine_create(const TtEngineDesc* desc, TtEngine** out_engine);
TT_API TtResult TT_CALL tt_engine_destroy(TtEngine* engine); // NULL なら何もせず TT_OK
TT_API TtResult TT_CALL tt_engine_get_info(TtEngine* engine, TtEngineInfo* out_info);
TT_API TtResult TT_CALL tt_engine_query_video_memory(TtEngine* engine, TtVideoMemoryInfo* out_info);

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
// ウィンドウ用ビューは、前の表示が終わっていなければ待たずに TT_S_NOT_READY を返す（UI を止めないため）
TT_API TtResult TT_CALL tt_view_render(TtView* view, const TtFrameParams* params);

// 画面外ビューの内容を BGRA 8 ビットで読み出す。テスト・保存用で、毎フレーム呼ぶものではない
TT_API TtResult TT_CALL tt_view_read_pixels(TtView* view, void* buffer, int32_t stride, int32_t buffer_size);

#ifdef __cplusplus
}
#endif
