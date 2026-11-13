// 色の形の変換（BGRA → NV12）。
#pragma once

#include <windows.h>

namespace tv {

// YUV にするときの色の決まり
enum class YuvMatrix {
    Bt709, // HD の動画の決まり（動画の書き出し）
    Bt601, // カメラの映像として多くのアプリが読む決まり（仮想カメラ）
};

// BGRA（上の行から）を NV12（明るさの面のあとに、2 × 2 画素ごとの色差 U・V を交互に並べた面）にする。
// 明るさ 16〜235・色差 16〜240。幅と高さは偶数。nv12 は width × height × 3 / 2 バイト
void BgraToNv12(const BYTE* bgra, int stride, UINT32 width, UINT32 height, BYTE* nv12, YuvMatrix matrix);

} // namespace tv
