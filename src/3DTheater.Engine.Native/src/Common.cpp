#include "Common.h"

namespace tt {

bool IsDeviceLost(HRESULT hr) noexcept
{
    return hr == DXGI_ERROR_DEVICE_REMOVED || hr == DXGI_ERROR_DEVICE_RESET || hr == DXGI_ERROR_DEVICE_HUNG
        || hr == DXGI_ERROR_DRIVER_INTERNAL_ERROR || hr == D3DERR_DEVICELOST;
}

void ThrowHr(HRESULT hr, const wchar_t* what)
{
    TtResult result = TT_E_GRAPHICS;
    if (IsDeviceLost(hr)) {
        result = TT_E_DEVICE_LOST;
    } else if (hr == E_OUTOFMEMORY) {
        result = TT_E_OUT_OF_MEMORY;
    }
    throw Error(result, std::format(L"{} に失敗しました (HRESULT 0x{:08X})", what, static_cast<uint32_t>(hr)));
}

} // namespace tt
