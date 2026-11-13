// 3D Theater の仮想カメラの決まり（カメラの元の DLL と、3D Theater のアプリの間の受け渡し）。
//
// - 仮想カメラの元（3DTheater.VirtualCamera.dll）は、Windows のカメラの仕組み（Frame Server。Local Service の権限で、
//   利用者とは別のセッション）の中で動く。アプリとは名前付きパイプ（TVC_PIPE_NAME）でつなぐ。アプリがパイプを作り、DLL がつなぎに行く。
// - DLL は「この大きさの次の絵がほしい」と頼み（TvcRequest）、アプリは絵の見出し（TvcFrameHeader）と NV12 の中身を返す。
//   NV12 は明るさ 16〜235。色の決まりは、高さが TVC_HD_MIN_HEIGHT 以上なら BT.709、それより小さければ BT.601
//   （Windows がカメラの映像を色に戻すときは、形式に書いた決まりを見ずに、この決まりで戻すため。確かめて分かった）。
// - 数は little endian。C# の側（Theater3D.Media.VirtualCameraProtocol）も同じ値を使う。
#pragma once

#include <stdint.h>

#define TVC_PIPE_NAME L"\\\\.\\pipe\\3DTheater.VirtualCamera.1"
#define TVC_CLSID_STRING L"{3FE7E0FA-DDA4-4859-9D35-C93F349735E4}"
#define TVC_FRIENDLY_NAME L"3D Theater"

#define TVC_REQUEST_MAGIC 0x51435654u // "TVCQ"
#define TVC_FRAME_MAGIC 0x46435654u   // "TVCF"
#define TVC_PROTOCOL_VERSION 1u

// アプリにつながらないとき・大きさを聞けないときの大きさ
#define TVC_DEFAULT_WIDTH 1920u
#define TVC_DEFAULT_HEIGHT 1080u
#define TVC_MIN_SIZE 16u
#define TVC_MAX_SIZE 4096u
#define TVC_FRAME_RATE 30u
#define TVC_HD_MIN_HEIGHT 577u // これ以上の高さは BT.709

// 見出しの印
#define TVC_FLAG_NO_FRAME 1u // 頼まれた大きさの絵がまだ無い（中身なし）

#pragma pack(push, 4)

// DLL → アプリ（24 バイト）
typedef struct TvcRequest {
    uint32_t magic;         // TVC_REQUEST_MAGIC
    uint32_t version;       // TVC_PROTOCOL_VERSION
    uint32_t width;         // ほしい絵の大きさ。0 なら大きさを聞くだけ（アプリの今の配信用の画面の大きさが返る）
    uint32_t height;
    uint64_t last_sequence; // 前に受け取った絵の番号（アプリは、これより新しい絵を少し待ってから返す）
} TvcRequest;

// アプリ → DLL（32 バイト）。続けて payload_size バイトの NV12 が来る
typedef struct TvcFrameHeader {
    uint32_t magic;        // TVC_FRAME_MAGIC
    uint32_t version;      // TVC_PROTOCOL_VERSION
    uint32_t width;        // 絵の大きさ（大きさを聞かれたときは、アプリの今の大きさ）
    uint32_t height;
    uint32_t flags;        // TVC_FLAG_*
    uint32_t payload_size; // 続く NV12 の大きさ（width × height × 3 / 2。絵が無ければ 0）
    uint64_t sequence;     // 絵の番号（新しい絵ほど大きい）
} TvcFrameHeader;

#pragma pack(pop)

#ifdef __cplusplus
static_assert(sizeof(TvcRequest) == 24, "TvcRequest は 24 バイト");
static_assert(sizeof(TvcFrameHeader) == 32, "TvcFrameHeader は 32 バイト");
#endif
