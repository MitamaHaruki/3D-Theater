// 3D Theater の動画の書き出し（Windows の Media Foundation を包んだもの）の公開 C API。C# からは P/Invoke で呼び出す。
//
// 決まり（詳しくは docs/設計判断記録.md）
// - MP4（映像は H.264、音声は AAC）を書く。符号化は Windows に入っているもの（GPU のものがあれば GPU）を使う。
// - 1 つの書き手（TvWriter）は、作ってから壊すまで同じスレッドから呼ぶ（COM を MTA で使う）。
// - 映像は BGRA 8 ビット・上の行から（透明度は使わない）。中で BT.709（明るさ 16〜235）の YUV に変換し、その決まりを動画に書き込む。
// - 音声は 16 ビットの PCM（チャンネルを交互に並べたもの）。
// - 構造体の先頭 struct_size には sizeof(構造体) を入れる。
#pragma once

#include <stdint.h>
#include <wchar.h>

#if defined(TV_BUILDING_DLL)
#define TV_API __declspec(dllexport)
#else
#define TV_API __declspec(dllimport)
#endif

#define TV_CALL __cdecl

#ifdef __cplusplus
extern "C" {
#endif

#define TV_API_VERSION_MAJOR 0
#define TV_API_VERSION_MINOR 3
#define TV_API_VERSION ((TV_API_VERSION_MAJOR << 16) | TV_API_VERSION_MINOR)

typedef int32_t TvResult;

enum {
    TV_OK = 0,
    TV_E_INVALID_ARG = -1,
    TV_E_INVALID_STATE = -2,
    TV_E_NOT_SUPPORTED = -4, // この大きさ・形式の符号化ができない
    TV_E_OUT_OF_MEMORY = -5,
    TV_E_MEDIA = -7,         // Media Foundation が失敗した（説明は tv_get_last_error_message）
    TV_E_END_OF_STREAM = -8, // 動画のファイルの終わり（繰り返さないとき）
    TV_E_UNKNOWN = -100,
};

typedef struct TvWriter TvWriter;

typedef struct TvWriterDesc {
    uint32_t struct_size;
    uint32_t width;             // 偶数（H.264 の決まり）
    uint32_t height;            // 偶数
    uint32_t frame_rate;        // 1 秒あたりのフレーム数
    uint32_t video_bitrate;     // 映像の 1 秒あたりのビット数
    uint32_t audio_sample_rate; // 0 なら音声なし。44100 か 48000
    uint32_t audio_channels;    // 1 か 2
    uint32_t audio_bitrate;     // 96000・128000・160000・192000 のどれか
    const wchar_t* path;        // 書き出し先（拡張子 .mp4）
} TvWriterDesc;

// カメラ 1 つ（tv_camera_list）。id は開くときに渡す目印
typedef struct TvCameraInfo {
    uint32_t struct_size;
    wchar_t name[256];
    wchar_t id[1024];
} TvCameraInfo;

// 映像の読み手（カメラか動画のファイル）。作ったスレッドで読み、壊すこと
typedef struct TvCapture TvCapture;

// 仮想カメラ（Windows 11）。作ったスレッドで壊すこと
typedef struct TvVirtualCamera TvVirtualCamera;

// NV12 にするときの色の決まり（tv_convert_bgra_to_nv12）
enum {
    TV_YUV_BT709 = 0, // HD の動画の決まり
    TV_YUV_BT601 = 1, // カメラの映像として多くのアプリが読む決まり
};

typedef struct TvCaptureInfo {
    uint32_t struct_size;
    uint32_t width;
    uint32_t height;
    uint32_t is_file;  // 動画のファイルなら 1
    double frame_rate;
} TvCaptureInfo;

// ファイルの中身（tv_probe_file）
typedef struct TvProbeInfo {
    uint32_t struct_size;
    uint32_t width;
    uint32_t height;
    uint32_t has_audio;
    uint32_t audio_sample_rate;
    uint32_t audio_channels;
    double frame_rate;
    double duration_seconds;
} TvProbeInfo;

TV_API uint32_t TV_CALL tv_get_api_version(void);

// 呼び出したスレッドで最後に起きたエラーの説明をコピーする。戻り値は説明の文字数（終端を含まない）。
TV_API int32_t TV_CALL tv_get_last_error_message(wchar_t* buffer, int32_t capacity);

// 書き始める（ファイルを作る）
TV_API TvResult TV_CALL tv_writer_create(const TvWriterDesc* desc, TvWriter** out_writer);
// 映像の 1 フレーム（width × height、BGRA・上の行から）。frame_index は 0 から順に
TV_API TvResult TV_CALL tv_writer_write_video(TvWriter* writer, const void* bgra, int32_t stride, int64_t frame_index);
// 音声の続き（frame_count はチャンネルをまとめた 1 組の数）。前に書いた分の続きの時刻に置く
TV_API TvResult TV_CALL tv_writer_write_audio(TvWriter* writer, const int16_t* samples, int32_t frame_count);
// 書き終える（ファイルを閉じる）。この後は tv_writer_destroy だけを呼ぶ
TV_API TvResult TV_CALL tv_writer_finish(TvWriter* writer);
// 片付ける。tv_writer_finish の前に呼ぶと、書きかけのファイルが残る（呼び出し側で消すこと）。NULL なら何もせず TV_OK
TV_API TvResult TV_CALL tv_writer_destroy(TvWriter* writer);

// 動画のファイルの中身を調べる（確かめる用）。first_frame が NULL でなければ、最初のフレームを BGRA・上の行から書き込む
// （width × height × 4 バイト以上の場所を渡すこと）
TV_API TvResult TV_CALL tv_probe_file(
    const wchar_t* path, TvProbeInfo* out_info, void* first_frame, int32_t stride, int32_t buffer_size);

// カメラの一覧を items に書く（capacity 個まで）。全部の数を返す（負ならエラー）
TV_API int32_t TV_CALL tv_camera_list(TvCameraInfo* items, int32_t capacity);
// カメラを開く。大きさとフレームの速さは、カメラが出せる形のうち、いちばん近いもの（tv_capture_get_info で分かる）
TV_API TvResult TV_CALL tv_capture_open_camera(const wchar_t* id, int32_t width, int32_t height, int32_t frame_rate, TvCapture** out_capture);
// 動画のファイルを開く（確かめ用。loop が 0 でなければ、終わりで先頭に戻る）
TV_API TvResult TV_CALL tv_capture_open_file(const wchar_t* path, int32_t loop, TvCapture** out_capture);
TV_API TvResult TV_CALL tv_capture_get_info(TvCapture* capture, TvCaptureInfo* out_info);
// 次のフレームを待って、BGRA・上の行から書き込む。ファイルの終わり（繰り返さないとき）は TV_E_END_OF_STREAM
TV_API TvResult TV_CALL tv_capture_read(TvCapture* capture, void* bgra, int32_t stride, int32_t buffer_size, int64_t* out_timestamp);
// 待っている tv_capture_read をやめさせる（ほかのスレッドから呼んでよい。この後の読み出しは失敗する。壊すのは作ったスレッドで）
TV_API TvResult TV_CALL tv_capture_cancel(TvCapture* capture);
TV_API TvResult TV_CALL tv_capture_destroy(TvCapture* capture);

// ===== API 0.3 =====

// BGRA（上の行から）を NV12（明るさ 16〜235・色差 16〜240）にする。幅と高さは偶数。nv12 には width × height × 3 / 2 バイト以上を渡す。
// matrix は TV_YUV_*
TV_API TvResult TV_CALL tv_convert_bgra_to_nv12(
    const void* bgra, int32_t stride, int32_t width, int32_t height, void* nv12, int32_t nv12_size, int32_t matrix);

// この Windows で仮想カメラを作れるか（Windows 11 以降なら 1）
TV_API int32_t TV_CALL tv_virtual_camera_supported(void);
// 今の利用者だけに見える仮想カメラを作って出す（壊すまで、ほかのアプリのカメラの一覧に出る。管理者の権限は要らない）。
// clsid は、HKLM に登録したカメラの元（COM の部品）の "{...}"
TV_API TvResult TV_CALL tv_virtual_camera_create(const wchar_t* friendly_name, const wchar_t* clsid, TvVirtualCamera** out_camera);
// 仮想カメラをしまう（ほかのアプリの一覧から消える）。NULL なら何もせず TV_OK
TV_API TvResult TV_CALL tv_virtual_camera_destroy(TvVirtualCamera* camera);

// 確かめ用: COM の部品の DLL（dll_path）から、カメラの元（clsid の IMFActivate）を直接作って読む（登録は要らない）
TV_API TvResult TV_CALL tv_capture_open_activate(const wchar_t* dll_path, const wchar_t* clsid, TvCapture** out_capture);

#ifdef __cplusplus
}
#endif
