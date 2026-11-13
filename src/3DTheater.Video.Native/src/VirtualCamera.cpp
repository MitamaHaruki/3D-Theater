// 仮想カメラを出す・しまう。MFCreateVirtualCamera は Windows 11 から（mfsensorgroup.dll）なので、
// 古い Windows でもこの DLL を読み込めるよう、関数は使うときに探す。
#include "VirtualCamera.h"

#include <mfvirtualcamera.h>

namespace tv {

namespace {

using CreateVirtualCamera = HRESULT(STDAPICALLTYPE*)(MFVirtualCameraType, MFVirtualCameraLifetime, MFVirtualCameraAccess,
    LPCWSTR, LPCWSTR, const GUID*, ULONG, IMFVirtualCamera**);

CreateVirtualCamera FindCreate()
{
    static const CreateVirtualCamera create = []() -> CreateVirtualCamera {
        HMODULE module = LoadLibraryExW(L"mfsensorgroup.dll", nullptr, LOAD_LIBRARY_SEARCH_SYSTEM32);
        return module ? reinterpret_cast<CreateVirtualCamera>(GetProcAddress(module, "MFCreateVirtualCamera")) : nullptr;
    }();
    return create;
}

} // namespace

bool VirtualCamera::IsSupported()
{
    return FindCreate() != nullptr;
}

VirtualCamera::VirtualCamera(const wchar_t* friendlyName, const wchar_t* clsid)
{
    const CreateVirtualCamera create = FindCreate();
    if (!create) {
        throw Error(TV_E_NOT_SUPPORTED, L"この Windows では仮想カメラを作れません（Windows 11 が要ります）");
    }
    // この利用者だけに・このアプリが動いている間だけ見えるカメラ（管理者の権限は要らない。カメラの元の登録には要る）
    CheckHr(create(MFVirtualCameraType_SoftwareCameraSource, MFVirtualCameraLifetime_Session, MFVirtualCameraAccess_CurrentUser,
                friendlyName, clsid, nullptr, 0, &camera_),
        L"仮想カメラの作成");
    const HRESULT hr = camera_->Start(nullptr);
    if (FAILED(hr)) {
        camera_->Shutdown();
        camera_.Reset();
        CheckHr(hr, hr == E_ACCESSDENIED
                ? L"仮想カメラを出す（Windows の設定の「プライバシーとセキュリティ」→「カメラ」で、デスクトップ アプリのカメラの使用を許可してください）"
                : L"仮想カメラを出す（カメラの元が登録されていない可能性があります）");
    }
}

VirtualCamera::~VirtualCamera()
{
    magic_ = 0;
    if (camera_) {
        camera_->Remove();
        camera_->Shutdown();
        camera_.Reset();
    }
}

} // namespace tv
