/*
 * ThumbnailHandler.c — Native COM thumbnail handler for Windows Cloud Files.
 *
 * Loaded into dllhost.exe via com:SurrogateServer in the MSIX manifest.
 * Native C (no CLR) for fast cold-start in the COM Surrogate process.
 *
 * Implements IInitializeWithItem + IInitializeWithFile + IThumbnailProvider.
 * Explorer uses IInitializeWithItem for cloud file placeholders.
 * Connects to Service.exe over named pipe to fetch PNG thumbnails via JMAP.
 *
 * CLSID: {B8C4F3E2-1A5D-4B9C-C6F7-2E4D8A9B3F1C}
 *
 * Compile with MinGW:
 *   x86_64-w64-mingw32-gcc -shared -O2 -o FileNodeClient.ThumbnailExtension.dll \
 *       ThumbnailHandler.c ThumbnailHandler.def -lole32 -lwindowscodecs -lgdi32 -luser32 -luuid
 */

#define COBJMACROS
#define WIN32_LEAN_AND_MEAN
#define UNICODE
#define _UNICODE

#include <windows.h>
#include <wincodec.h>
#include <shlwapi.h>
#include <thumbcache.h>
#include <objbase.h>
#include <shobjidl.h>

/* ================================================================== */
/*  GUIDs                                                             */
/* ================================================================== */

/* Thumbnail CLSID: {B8C4F3E2-1A5D-4B9C-C6F7-2E4D8A9B3F1C} */
static const GUID CLSID_ThumbnailHandler = {
    0xB8C4F3E2, 0x1A5D, 0x4B9C,
    { 0xC6, 0xF7, 0x2E, 0x4D, 0x8A, 0x9B, 0x3F, 0x1C }
};

/* IInitializeWithFile {B7D14566-0509-4CCE-A71F-0A554233BD9B} */
static const GUID IID_IInitializeWithFile_local = {
    0xB7D14566, 0x0509, 0x4CCE,
    { 0xA7, 0x1F, 0x0A, 0x55, 0x42, 0x33, 0xBD, 0x9B }
};

/* IInitializeWithItem {7F73BE3F-FB79-493C-A6C7-7EE14E245841} */
static const GUID IID_IInitializeWithItem_local = {
    0x7F73BE3F, 0xFB79, 0x493C,
    { 0xA6, 0xC7, 0x7E, 0xE1, 0x4E, 0x24, 0x58, 0x41 }
};

/* IThumbnailProvider {E357FCCD-A995-4576-B01F-234630154E96} */
static const GUID IID_IThumbnailProvider_local = {
    0xE357FCCD, 0xA995, 0x4576,
    { 0xB0, 0x1F, 0x23, 0x46, 0x30, 0x15, 0x4E, 0x96 }
};

static HMODULE g_hModule;
static LONG g_cRef;  /* global DLL reference count */

/* ================================================================== */
/*  ThumbnailHandler — IThumbnailProvider via named pipe               */
/* ================================================================== */

/* IInitializeWithItem vtable (not in MinGW headers) */
typedef struct IInitializeWithItemVtbl_local {
    /* IUnknown */
    HRESULT (STDMETHODCALLTYPE *QueryInterface)(void*, REFIID, void**);
    ULONG   (STDMETHODCALLTYPE *AddRef)(void*);
    ULONG   (STDMETHODCALLTYPE *Release)(void*);
    /* IInitializeWithItem */
    HRESULT (STDMETHODCALLTYPE *Initialize)(void*, IShellItem*, DWORD);
} IInitializeWithItemVtbl_local;

typedef struct ThumbnailHandler {
    IInitializeWithFileVtbl       *lpVtblInit;
    IThumbnailProviderVtbl        *lpVtblThumb;
    IInitializeWithItemVtbl_local *lpVtblItem;
    LONG cRef;
    WCHAR filePath[MAX_PATH];
} ThumbnailHandler;

#define HANDLER_FROM_ITHUMB(p) \
    ((ThumbnailHandler*)((BYTE*)(p) - offsetof(ThumbnailHandler, lpVtblThumb)))
#define HANDLER_FROM_IITEM(p) \
    ((ThumbnailHandler*)((BYTE*)(p) - offsetof(ThumbnailHandler, lpVtblItem)))

/* Forward declarations */
static HRESULT STDMETHODCALLTYPE Init_QueryInterface(IInitializeWithFile*, REFIID, void**);
static ULONG   STDMETHODCALLTYPE Init_AddRef(IInitializeWithFile*);
static ULONG   STDMETHODCALLTYPE Init_Release(IInitializeWithFile*);
static HRESULT STDMETHODCALLTYPE Init_Initialize(IInitializeWithFile*, LPCWSTR, DWORD);

static HRESULT STDMETHODCALLTYPE Thumb_QueryInterface(IThumbnailProvider*, REFIID, void**);
static ULONG   STDMETHODCALLTYPE Thumb_AddRef(IThumbnailProvider*);
static ULONG   STDMETHODCALLTYPE Thumb_Release(IThumbnailProvider*);
static HRESULT STDMETHODCALLTYPE Thumb_GetThumbnail(IThumbnailProvider*, UINT, HBITMAP*, WTS_ALPHATYPE*);

static HRESULT STDMETHODCALLTYPE Item_QueryInterface(void*, REFIID, void**);
static ULONG   STDMETHODCALLTYPE Item_AddRef(void*);
static ULONG   STDMETHODCALLTYPE Item_Release(void*);
static HRESULT STDMETHODCALLTYPE Item_Initialize(void*, IShellItem*, DWORD);

static IInitializeWithFileVtbl g_InitVtbl = {
    Init_QueryInterface, Init_AddRef, Init_Release, Init_Initialize
};

static IThumbnailProviderVtbl g_ThumbVtbl = {
    Thumb_QueryInterface, Thumb_AddRef, Thumb_Release, Thumb_GetThumbnail
};

static IInitializeWithItemVtbl_local g_ItemVtbl = {
    Item_QueryInterface, Item_AddRef, Item_Release, Item_Initialize
};

static HRESULT STDMETHODCALLTYPE Init_QueryInterface(
    IInitializeWithFile *This, REFIID riid, void **ppv)
{
    ThumbnailHandler *self = (ThumbnailHandler*)This;

    if (IsEqualIID(riid, &IID_IUnknown) ||
        IsEqualIID(riid, &IID_IInitializeWithFile_local))
    {
        *ppv = &self->lpVtblInit;
        Init_AddRef(This);
        return S_OK;
    }
    if (IsEqualIID(riid, &IID_IInitializeWithItem_local))
    {
        *ppv = &self->lpVtblItem;
        InterlockedIncrement(&self->cRef);
        return S_OK;
    }
    if (IsEqualIID(riid, &IID_IThumbnailProvider_local))
    {
        *ppv = &self->lpVtblThumb;
        InterlockedIncrement(&self->cRef);
        return S_OK;
    }
    *ppv = NULL;
    return E_NOINTERFACE;
}

static ULONG STDMETHODCALLTYPE Init_AddRef(IInitializeWithFile *This)
{
    ThumbnailHandler *self = (ThumbnailHandler*)This;
    return InterlockedIncrement(&self->cRef);
}

static ULONG STDMETHODCALLTYPE Init_Release(IInitializeWithFile *This)
{
    ThumbnailHandler *self = (ThumbnailHandler*)This;
    LONG c = InterlockedDecrement(&self->cRef);
    if (c == 0) {
        HeapFree(GetProcessHeap(), 0, self);
        InterlockedDecrement(&g_cRef);
    }
    return c;
}

static HRESULT STDMETHODCALLTYPE Init_Initialize(
    IInitializeWithFile *This, LPCWSTR pszFilePath, DWORD grfMode)
{
    ThumbnailHandler *self = (ThumbnailHandler*)This;
    (void)grfMode;
    if (!pszFilePath) return E_INVALIDARG;
    wcsncpy_s(self->filePath, MAX_PATH, pszFilePath, _TRUNCATE);
    return S_OK;
}

/* IInitializeWithItem implementation */
static HRESULT STDMETHODCALLTYPE Item_QueryInterface(void *This, REFIID riid, void **ppv)
{
    ThumbnailHandler *self = HANDLER_FROM_IITEM(This);
    return Init_QueryInterface((IInitializeWithFile*)self, riid, ppv);
}

static ULONG STDMETHODCALLTYPE Item_AddRef(void *This)
{
    ThumbnailHandler *self = HANDLER_FROM_IITEM(This);
    return InterlockedIncrement(&self->cRef);
}

static ULONG STDMETHODCALLTYPE Item_Release(void *This)
{
    ThumbnailHandler *self = HANDLER_FROM_IITEM(This);
    return Init_Release((IInitializeWithFile*)self);
}

static HRESULT STDMETHODCALLTYPE Item_Initialize(void *This, IShellItem *psi, DWORD grfMode)
{
    ThumbnailHandler *self = HANDLER_FROM_IITEM(This);
    LPWSTR pszPath = NULL;
    HRESULT hr;
    (void)grfMode;

    if (!psi) return E_INVALIDARG;

    hr = IShellItem_GetDisplayName(psi, SIGDN_FILESYSPATH, &pszPath);
    if (SUCCEEDED(hr) && pszPath) {
        wcsncpy_s(self->filePath, MAX_PATH, pszPath, _TRUNCATE);
        CoTaskMemFree(pszPath);
        return S_OK;
    }

    return hr;
}

static HRESULT STDMETHODCALLTYPE Thumb_QueryInterface(
    IThumbnailProvider *This, REFIID riid, void **ppv)
{
    ThumbnailHandler *self = HANDLER_FROM_ITHUMB(This);
    return Init_QueryInterface((IInitializeWithFile*)self, riid, ppv);
}

static ULONG STDMETHODCALLTYPE Thumb_AddRef(IThumbnailProvider *This)
{
    ThumbnailHandler *self = HANDLER_FROM_ITHUMB(This);
    return InterlockedIncrement(&self->cRef);
}

static ULONG STDMETHODCALLTYPE Thumb_Release(IThumbnailProvider *This)
{
    ThumbnailHandler *self = HANDLER_FROM_ITHUMB(This);
    return Init_Release((IInitializeWithFile*)self);
}

/* Read exactly 'count' bytes from a pipe handle. Returns TRUE on success. */
static BOOL ReadExact(HANDLE hPipe, void *buf, DWORD count)
{
    BYTE *p = (BYTE*)buf;
    while (count > 0) {
        DWORD read = 0;
        if (!ReadFile(hPipe, p, count, &read, NULL) || read == 0)
            return FALSE;
        p += read;
        count -= read;
    }
    return TRUE;
}

/* Connect to Service.exe pipe, send request, receive PNG bytes. */
static BYTE* RequestThumbnail(const WCHAR *filePath, UINT cx, DWORD *pngSize)
{
    HANDLE hPipe;
    int utf8Len;
    char *utf8Path = NULL;
    BYTE *pngBuf = NULL;
    DWORD pathLenLE, cxLE, respLen;

    *pngSize = 0;

    hPipe = CreateFileW(
        L"\\\\.\\pipe\\FileNodeClient.Thumbnails",
        GENERIC_READ | GENERIC_WRITE,
        0, NULL, OPEN_EXISTING, 0, NULL);

    if (hPipe == INVALID_HANDLE_VALUE)
        return NULL;

    utf8Len = WideCharToMultiByte(CP_UTF8, 0, filePath, -1, NULL, 0, NULL, NULL);
    if (utf8Len <= 0) goto cleanup;
    utf8Len--;

    utf8Path = (char*)HeapAlloc(GetProcessHeap(), 0, utf8Len);
    if (!utf8Path) goto cleanup;
    WideCharToMultiByte(CP_UTF8, 0, filePath, -1, utf8Path, utf8Len + 1, NULL, NULL);

    pathLenLE = (DWORD)utf8Len;
    if (!WriteFile(hPipe, &pathLenLE, 4, NULL, NULL)) goto cleanup;
    if (!WriteFile(hPipe, utf8Path, utf8Len, NULL, NULL)) goto cleanup;
    cxLE = cx;
    if (!WriteFile(hPipe, &cxLE, 4, NULL, NULL)) goto cleanup;
    FlushFileBuffers(hPipe);

    if (!ReadExact(hPipe, &respLen, 4)) goto cleanup;
    if (respLen == 0 || respLen > 10 * 1024 * 1024) goto cleanup;

    pngBuf = (BYTE*)HeapAlloc(GetProcessHeap(), 0, respLen);
    if (!pngBuf) goto cleanup;
    if (!ReadExact(hPipe, pngBuf, respLen)) {
        HeapFree(GetProcessHeap(), 0, pngBuf);
        pngBuf = NULL;
        goto cleanup;
    }
    *pngSize = respLen;

cleanup:
    if (utf8Path) HeapFree(GetProcessHeap(), 0, utf8Path);
    CloseHandle(hPipe);
    return pngBuf;
}

/* Decode PNG bytes to HBITMAP using WIC */
static HRESULT DecodePngToHBitmap(const BYTE *pngData, DWORD pngSize, UINT cx, HBITMAP *phbmp)
{
    IWICImagingFactory *pFactory = NULL;
    IWICStream *pStream = NULL;
    IWICBitmapDecoder *pDecoder = NULL;
    IWICBitmapFrameDecode *pFrame = NULL;
    IWICFormatConverter *pConverter = NULL;
    IWICBitmapScaler *pScaler = NULL;
    UINT origW, origH, thumbW, thumbH;
    BITMAPINFO bmi;
    void *pvBits = NULL;
    HDC hdc = NULL;
    HRESULT hr;

    hr = CoCreateInstance(
        &CLSID_WICImagingFactory, NULL, CLSCTX_INPROC_SERVER,
        &IID_IWICImagingFactory, (void**)&pFactory);
    if (FAILED(hr)) return hr;

    hr = IWICImagingFactory_CreateStream(pFactory, &pStream);
    if (FAILED(hr)) goto done;

    hr = IWICStream_InitializeFromMemory(pStream, (BYTE*)pngData, pngSize);
    if (FAILED(hr)) goto done;

    hr = IWICImagingFactory_CreateDecoderFromStream(
        pFactory, (IStream*)pStream,
        NULL, WICDecodeMetadataCacheOnDemand, &pDecoder);
    if (FAILED(hr)) goto done;

    hr = IWICBitmapDecoder_GetFrame(pDecoder, 0, &pFrame);
    if (FAILED(hr)) goto done;

    IWICBitmapFrameDecode_GetSize(pFrame, &origW, &origH);

    if (origW == 0 || origH == 0) { hr = E_FAIL; goto done; }
    if (origW >= origH) {
        thumbW = cx;
        thumbH = (UINT)((UINT64)origH * cx / origW);
    } else {
        thumbH = cx;
        thumbW = (UINT)((UINT64)origW * cx / origH);
    }
    if (thumbW == 0) thumbW = 1;
    if (thumbH == 0) thumbH = 1;

    hr = IWICImagingFactory_CreateBitmapScaler(pFactory, &pScaler);
    if (FAILED(hr)) goto done;

    hr = IWICBitmapScaler_Initialize(
        pScaler, (IWICBitmapSource*)pFrame,
        thumbW, thumbH, WICBitmapInterpolationModeFant);
    if (FAILED(hr)) goto done;

    hr = IWICImagingFactory_CreateFormatConverter(pFactory, &pConverter);
    if (FAILED(hr)) goto done;

    hr = IWICFormatConverter_Initialize(
        pConverter, (IWICBitmapSource*)pScaler,
        &GUID_WICPixelFormat32bppBGRA,
        WICBitmapDitherTypeNone, NULL, 0.0,
        WICBitmapPaletteTypeCustom);
    if (FAILED(hr)) goto done;

    ZeroMemory(&bmi, sizeof(bmi));
    bmi.bmiHeader.biSize = sizeof(BITMAPINFOHEADER);
    bmi.bmiHeader.biWidth = (LONG)thumbW;
    bmi.bmiHeader.biHeight = -(LONG)thumbH;
    bmi.bmiHeader.biPlanes = 1;
    bmi.bmiHeader.biBitCount = 32;
    bmi.bmiHeader.biCompression = BI_RGB;

    hdc = GetDC(NULL);
    *phbmp = CreateDIBSection(hdc, &bmi, DIB_RGB_COLORS, &pvBits, NULL, 0);
    ReleaseDC(NULL, hdc);

    if (!*phbmp) { hr = E_OUTOFMEMORY; goto done; }

    hr = IWICFormatConverter_CopyPixels(
        pConverter, NULL,
        thumbW * 4, thumbW * thumbH * 4,
        (BYTE*)pvBits);

    if (FAILED(hr)) {
        DeleteObject(*phbmp);
        *phbmp = NULL;
    }

done:
    if (pConverter) IWICFormatConverter_Release(pConverter);
    if (pScaler)    IWICBitmapScaler_Release(pScaler);
    if (pFrame)     IWICBitmapFrameDecode_Release(pFrame);
    if (pDecoder)   IWICBitmapDecoder_Release(pDecoder);
    if (pStream)    IWICStream_Release(pStream);
    if (pFactory)   IWICImagingFactory_Release(pFactory);
    return hr;
}

static HRESULT STDMETHODCALLTYPE Thumb_GetThumbnail(
    IThumbnailProvider *This, UINT cx, HBITMAP *phbmp, WTS_ALPHATYPE *pdwAlpha)
{
    ThumbnailHandler *self = HANDLER_FROM_ITHUMB(This);
    DWORD pngSize;
    BYTE *pngData;
    HRESULT hr;

    *phbmp = NULL;
    *pdwAlpha = WTSAT_ARGB;

    if (self->filePath[0] == L'\0')
        return E_FAIL;

    pngData = RequestThumbnail(self->filePath, cx, &pngSize);
    if (!pngData || pngSize == 0)
        return E_FAIL;

    hr = DecodePngToHBitmap(pngData, pngSize, cx, phbmp);
    HeapFree(GetProcessHeap(), 0, pngData);
    return hr;
}

/* ================================================================== */
/*  Class Factory                                                      */
/* ================================================================== */

typedef struct ClassFactory {
    IClassFactoryVtbl *lpVtbl;
    LONG cRef;
} ClassFactory;

static HRESULT STDMETHODCALLTYPE CF_QueryInterface(
    IClassFactory *This, REFIID riid, void **ppv)
{
    if (IsEqualIID(riid, &IID_IUnknown) ||
        IsEqualIID(riid, &IID_IClassFactory))
    {
        *ppv = This;
        IClassFactory_AddRef(This);
        return S_OK;
    }
    *ppv = NULL;
    return E_NOINTERFACE;
}

static ULONG STDMETHODCALLTYPE CF_AddRef(IClassFactory *This)
{
    ClassFactory *self = (ClassFactory*)This;
    return InterlockedIncrement(&self->cRef);
}

static ULONG STDMETHODCALLTYPE CF_Release(IClassFactory *This)
{
    ClassFactory *self = (ClassFactory*)This;
    LONG c = InterlockedDecrement(&self->cRef);
    if (c == 0)
        HeapFree(GetProcessHeap(), 0, self);
    return c;
}

static HRESULT STDMETHODCALLTYPE CF_CreateInstance(
    IClassFactory *This, IUnknown *pOuter, REFIID riid, void **ppv)
{
    ThumbnailHandler *handler;
    HRESULT hr;
    (void)This;

    *ppv = NULL;
    if (pOuter) return CLASS_E_NOAGGREGATION;

    handler = (ThumbnailHandler*)HeapAlloc(
        GetProcessHeap(), HEAP_ZERO_MEMORY, sizeof(ThumbnailHandler));
    if (!handler) return E_OUTOFMEMORY;

    handler->lpVtblInit = &g_InitVtbl;
    handler->lpVtblThumb = &g_ThumbVtbl;
    handler->lpVtblItem = &g_ItemVtbl;
    handler->cRef = 1;
    InterlockedIncrement(&g_cRef);

    hr = Init_QueryInterface((IInitializeWithFile*)handler, riid, ppv);
    Init_Release((IInitializeWithFile*)handler);
    return hr;
}

static HRESULT STDMETHODCALLTYPE CF_LockServer(IClassFactory *This, BOOL fLock)
{
    (void)This;
    if (fLock)
        InterlockedIncrement(&g_cRef);
    else
        InterlockedDecrement(&g_cRef);
    return S_OK;
}

static IClassFactoryVtbl g_CFVtbl = {
    CF_QueryInterface, CF_AddRef, CF_Release, CF_CreateInstance, CF_LockServer
};

/* ================================================================== */
/*  DLL exports                                                       */
/* ================================================================== */

BOOL WINAPI DllMain(HINSTANCE hinstDLL, DWORD fdwReason, LPVOID lpvReserved)
{
    (void)lpvReserved;
    if (fdwReason == DLL_PROCESS_ATTACH) {
        g_hModule = hinstDLL;
        DisableThreadLibraryCalls(hinstDLL);
    }
    return TRUE;
}

STDAPI DllGetClassObject(REFCLSID rclsid, REFIID riid, void **ppv)
{
    ClassFactory *cf;
    HRESULT hr;

    *ppv = NULL;

    if (!IsEqualCLSID(rclsid, &CLSID_ThumbnailHandler))
        return CLASS_E_CLASSNOTAVAILABLE;

    cf = (ClassFactory*)HeapAlloc(
        GetProcessHeap(), HEAP_ZERO_MEMORY, sizeof(ClassFactory));
    if (!cf) return E_OUTOFMEMORY;

    cf->lpVtbl = &g_CFVtbl;
    cf->cRef = 1;

    hr = CF_QueryInterface((IClassFactory*)cf, riid, ppv);
    CF_Release((IClassFactory*)cf);
    return hr;
}

STDAPI DllCanUnloadNow(void)
{
    return g_cRef == 0 ? S_OK : S_FALSE;
}
