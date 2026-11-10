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
#define TV_API_VERSION_MINOR 1
#define TV_API_VERSION ((TV_API_VERSION_MAJOR << 16) | TV_API_VERSION_MINOR)

typedef int32_t TvResult;

enum {
    TV_OK = 0,
    TV_E_INVALID_ARG = -1,
    TV_E_INVALID_STATE = -2,
    TV_E_NOT_SUPPORTED = -4, // この大きさ・形式の符号化ができない
    TV_E_OUT_OF_MEMORY = -5,
    TV_E_MEDIA = -7,         // Media Foundation が失敗した（説明は tv_get_last_error_message）
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

#ifdef __cplusplus
}
#endif
