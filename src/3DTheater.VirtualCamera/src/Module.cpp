// 仮想カメラの元の DLL の入口（COM の部品）。Windows の仮想カメラ（MFCreateVirtualCamera）は、登録した CLSID の部品を
// IMFActivate として作り、ActivateObject でメディアソースを受け取る。
//
// 登録（DllRegisterServer）は HKLM に書くので、管理者の権限が要る。Frame Server（Local Service）と、カメラを使う
// アプリのそれぞれが読み込むため、利用者ごとの HKCU では見えない。3D Theater は、自分で管理者として起動し直して登録する。
#include <wrl/module.h>

#include <mutex>
#include <string>

#include "MediaSource.h"

namespace tvc {

class __declspec(uuid("3FE7E0FA-DDA4-4859-9D35-C93F349735E4")) Activator
    : public RuntimeClass<RuntimeClassFlags<ClassicCom>, ChainInterfaces<IMFActivate, IMFAttributes>> {
public:
    Activator() { MFCreateAttributes(&attributes_, 1); }

    STDMETHODIMP ActivateObject(REFIID riid, void** object) override
    {
        if (!object) {
            return E_POINTER;
        }
        *object = nullptr;
        if (!attributes_) {
            return E_OUTOFMEMORY;
        }
        std::lock_guard lock(mutex_);
        if (!source_) {
            TVC_RETURN_IF_FAILED(Microsoft::WRL::MakeAndInitialize<MediaSource>(&source_, attributes_.Get()));
        }
        return source_->QueryInterface(riid, object);
    }

    STDMETHODIMP ShutdownObject() override
    {
        ComPtr<MediaSource> source;
        {
            std::lock_guard lock(mutex_);
            source = std::move(source_);
        }
        if (source) {
            source->Shutdown();
        }
        return S_OK;
    }

    STDMETHODIMP DetachObject() override
    {
        std::lock_guard lock(mutex_);
        source_.Reset();
        return S_OK;
    }

    TVC_ATTRIBUTES_METHODS(attributes_)

private:
    std::mutex mutex_;
    ComPtr<IMFAttributes> attributes_;
    ComPtr<MediaSource> source_;
};

CoCreatableClass(Activator);

} // namespace tvc

namespace {

HMODULE g_module = nullptr;

const std::wstring kClassKey = std::wstring(L"SOFTWARE\\Classes\\CLSID\\") + TVC_CLSID_STRING;

HRESULT SetString(HKEY key, const wchar_t* name, const std::wstring& value)
{
    const auto bytes = static_cast<DWORD>((value.size() + 1) * sizeof(wchar_t));
    return HRESULT_FROM_WIN32(RegSetValueExW(key, name, 0, REG_SZ, reinterpret_cast<const BYTE*>(value.c_str()), bytes));
}

} // namespace

BOOL WINAPI DllMain(HINSTANCE instance, DWORD reason, LPVOID)
{
    if (reason == DLL_PROCESS_ATTACH) {
        g_module = instance;
        DisableThreadLibraryCalls(instance);
    }
    return TRUE;
}

STDAPI DllGetClassObject(REFCLSID clsid, REFIID riid, LPVOID* object)
{
    return Microsoft::WRL::Module<Microsoft::WRL::InProc>::GetModule().GetClassObject(clsid, riid, object);
}

STDAPI DllCanUnloadNow()
{
    return Microsoft::WRL::Module<Microsoft::WRL::InProc>::GetModule().Terminate() ? S_OK : S_FALSE;
}

// HKLM\SOFTWARE\Classes\CLSID\{…}\InprocServer32 に、この DLL の場所を書く（管理者の権限が要る）
STDAPI DllRegisterServer()
{
    wchar_t path[MAX_PATH];
    const DWORD length = GetModuleFileNameW(g_module, path, MAX_PATH);
    if (length == 0 || length >= MAX_PATH) {
        return HRESULT_FROM_WIN32(GetLastError());
    }
    HKEY classKey = nullptr;
    TVC_RETURN_IF_FAILED(HRESULT_FROM_WIN32(RegCreateKeyExW(HKEY_LOCAL_MACHINE, kClassKey.c_str(), 0, nullptr, 0, KEY_WRITE, nullptr, &classKey, nullptr)));
    HRESULT hr = SetString(classKey, nullptr, L"3D Theater 仮想カメラ");
    HKEY serverKey = nullptr;
    if (SUCCEEDED(hr)) {
        hr = HRESULT_FROM_WIN32(RegCreateKeyExW(classKey, L"InprocServer32", 0, nullptr, 0, KEY_WRITE, nullptr, &serverKey, nullptr));
    }
    if (SUCCEEDED(hr)) {
        hr = SetString(serverKey, nullptr, path);
    }
    if (SUCCEEDED(hr)) {
        hr = SetString(serverKey, L"ThreadingModel", L"Both");
    }
    if (serverKey) {
        RegCloseKey(serverKey);
    }
    RegCloseKey(classKey);
    return hr;
}

STDAPI DllUnregisterServer()
{
    const LSTATUS status = RegDeleteTreeW(HKEY_LOCAL_MACHINE, kClassKey.c_str());
    return status == ERROR_SUCCESS || status == ERROR_FILE_NOT_FOUND ? S_OK : HRESULT_FROM_WIN32(status);
}
