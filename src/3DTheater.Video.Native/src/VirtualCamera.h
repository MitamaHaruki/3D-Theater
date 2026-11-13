// 仮想カメラ（Windows 11 の MFCreateVirtualCamera）を出す・しまう。
#pragma once

#include "Writer.h"

struct IMFVirtualCamera;

namespace tv {

class VirtualCamera {
public:
    // この Windows で仮想カメラを作れるか（Windows 11 以降。mfsensorgroup.dll に MFCreateVirtualCamera があるか）
    static bool IsSupported();

    // 今の利用者だけに見える仮想カメラを作って出す。壊すまで、ほかのアプリのカメラの一覧に出る
    VirtualCamera(const wchar_t* friendlyName, const wchar_t* clsid);
    ~VirtualCamera();

    VirtualCamera(const VirtualCamera&) = delete;
    VirtualCamera& operator=(const VirtualCamera&) = delete;

    bool IsValid() const noexcept { return magic_ == kMagic; }

private:
    static constexpr uint32_t kMagic = 0x4D414356; // "VCAM"
    uint32_t magic_ = kMagic;
    MediaStartup startup_;
    ComPtr<IMFVirtualCamera> camera_;
};

} // namespace tv
