// 仮想カメラの元（Media Foundation のメディアソース）の共通の部分。
#pragma once

#include <windows.h>

// Media Foundation（GUID_NULL を含む）を先に読む。ks.h は、後から読む cguid.h とぶつかる GUID_NULL の書き方をするため
#include <mfapi.h>
#include <mferror.h>
#include <mfidl.h>

#include <ks.h>
#include <ksmedia.h>
// IKsControl（カメラの設定の口）。ks.h ではドライバー向け（_NTDDK_）の中にしか無いので、アプリ向けの宣言を使う
#include <devicetopology.h>

#include <wrl/client.h>
#include <wrl/implements.h>

#include "tvc_protocol.h"

namespace tvc {

using Microsoft::WRL::ChainInterfaces;
using Microsoft::WRL::ComPtr;
using Microsoft::WRL::RuntimeClass;
using Microsoft::WRL::RuntimeClassFlags;
using Microsoft::WRL::ClassicCom;

// 1 フレームの長さ（100 ナノ秒単位）
constexpr LONGLONG kFrameDuration = 10'000'000LL / TVC_FRAME_RATE;

// NV12 の 1 枚の大きさ（明るさの面と、その半分の色差の面）
constexpr size_t Nv12Bytes(uint32_t width, uint32_t height) noexcept
{
    return static_cast<size_t>(width) * height * 3 / 2;
}

// 大きさが使えるか（NV12 は幅と高さが偶数）
constexpr bool IsValidSize(uint32_t width, uint32_t height) noexcept
{
    return width >= TVC_MIN_SIZE && height >= TVC_MIN_SIZE && width <= TVC_MAX_SIZE && height <= TVC_MAX_SIZE
        && width % 2 == 0 && height % 2 == 0;
}

// 黒（明るさ 16・色差 128）で塗る
void FillBlack(BYTE* nv12, uint32_t width, uint32_t height) noexcept;

// デバッガーで見る記録（Frame Server の中で動くので、ファイルには書かない）
void Trace(const wchar_t* format, ...) noexcept;

} // namespace tvc

#define TVC_RETURN_IF_FAILED(expression) \
    do { \
        const HRESULT tvcHr = (expression); \
        if (FAILED(tvcHr)) { \
            return tvcHr; \
        } \
    } while (false)

// IMFAttributes の全部の関数を、持っている属性の入れ物（store）へ回す
#define TVC_ATTRIBUTES_METHODS(store) \
    STDMETHODIMP GetItem(REFGUID k, PROPVARIANT* v) override { return store->GetItem(k, v); } \
    STDMETHODIMP GetItemType(REFGUID k, MF_ATTRIBUTE_TYPE* t) override { return store->GetItemType(k, t); } \
    STDMETHODIMP CompareItem(REFGUID k, REFPROPVARIANT v, BOOL* r) override { return store->CompareItem(k, v, r); } \
    STDMETHODIMP Compare(IMFAttributes* t, MF_ATTRIBUTES_MATCH_TYPE m, BOOL* r) override { return store->Compare(t, m, r); } \
    STDMETHODIMP GetUINT32(REFGUID k, UINT32* v) override { return store->GetUINT32(k, v); } \
    STDMETHODIMP GetUINT64(REFGUID k, UINT64* v) override { return store->GetUINT64(k, v); } \
    STDMETHODIMP GetDouble(REFGUID k, double* v) override { return store->GetDouble(k, v); } \
    STDMETHODIMP GetGUID(REFGUID k, GUID* v) override { return store->GetGUID(k, v); } \
    STDMETHODIMP GetStringLength(REFGUID k, UINT32* l) override { return store->GetStringLength(k, l); } \
    STDMETHODIMP GetString(REFGUID k, LPWSTR v, UINT32 s, UINT32* l) override { return store->GetString(k, v, s, l); } \
    STDMETHODIMP GetAllocatedString(REFGUID k, LPWSTR* v, UINT32* l) override { return store->GetAllocatedString(k, v, l); } \
    STDMETHODIMP GetBlobSize(REFGUID k, UINT32* s) override { return store->GetBlobSize(k, s); } \
    STDMETHODIMP GetBlob(REFGUID k, UINT8* b, UINT32 s, UINT32* r) override { return store->GetBlob(k, b, s, r); } \
    STDMETHODIMP GetAllocatedBlob(REFGUID k, UINT8** b, UINT32* s) override { return store->GetAllocatedBlob(k, b, s); } \
    STDMETHODIMP GetUnknown(REFGUID k, REFIID i, LPVOID* p) override { return store->GetUnknown(k, i, p); } \
    STDMETHODIMP SetItem(REFGUID k, REFPROPVARIANT v) override { return store->SetItem(k, v); } \
    STDMETHODIMP DeleteItem(REFGUID k) override { return store->DeleteItem(k); } \
    STDMETHODIMP DeleteAllItems() override { return store->DeleteAllItems(); } \
    STDMETHODIMP SetUINT32(REFGUID k, UINT32 v) override { return store->SetUINT32(k, v); } \
    STDMETHODIMP SetUINT64(REFGUID k, UINT64 v) override { return store->SetUINT64(k, v); } \
    STDMETHODIMP SetDouble(REFGUID k, double v) override { return store->SetDouble(k, v); } \
    STDMETHODIMP SetGUID(REFGUID k, REFGUID v) override { return store->SetGUID(k, v); } \
    STDMETHODIMP SetString(REFGUID k, LPCWSTR v) override { return store->SetString(k, v); } \
    STDMETHODIMP SetBlob(REFGUID k, const UINT8* b, UINT32 s) override { return store->SetBlob(k, b, s); } \
    STDMETHODIMP SetUnknown(REFGUID k, IUnknown* p) override { return store->SetUnknown(k, p); } \
    STDMETHODIMP LockStore() override { return store->LockStore(); } \
    STDMETHODIMP UnlockStore() override { return store->UnlockStore(); } \
    STDMETHODIMP GetCount(UINT32* c) override { return store->GetCount(c); } \
    STDMETHODIMP GetItemByIndex(UINT32 i, GUID* k, PROPVARIANT* v) override { return store->GetItemByIndex(i, k, v); } \
    STDMETHODIMP CopyAllItems(IMFAttributes* d) override { return store->CopyAllItems(d); }

// カメラの設定（KS のプロパティ）には何も答えない（仮想カメラには明るさなどの設定が無い）
#define TVC_KSCONTROL_METHODS \
    STDMETHODIMP KsProperty(PKSPROPERTY, ULONG, LPVOID, ULONG, ULONG*) override { return HRESULT_FROM_WIN32(ERROR_SET_NOT_FOUND); } \
    STDMETHODIMP KsMethod(PKSMETHOD, ULONG, LPVOID, ULONG, ULONG*) override { return HRESULT_FROM_WIN32(ERROR_SET_NOT_FOUND); } \
    STDMETHODIMP KsEvent(PKSEVENT, ULONG, LPVOID, ULONG, ULONG*) override { return HRESULT_FROM_WIN32(ERROR_SET_NOT_FOUND); }
