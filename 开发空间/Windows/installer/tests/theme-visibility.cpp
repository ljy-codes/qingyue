// Runs only on a newly created, never activated desktop. No installer is run.
#define UNICODE
#define _UNICODE
#define WIN32_LEAN_AND_MEAN
#include <windows.h>
#include <commctrl.h>
#include <cstdio>
#include <string>

static int failures = 0;
static int checks = 0;
static void Check(const char* name, bool passed) {
    ++checks;
    std::printf("%s: %s\n", passed ? "PASS" : "FAIL", name);
    if (!passed) ++failures;
}
static INT_PTR CALLBACK DialogProc(HWND, UINT, WPARAM, LPARAM) { return FALSE; }
using Plugin = void (__cdecl*)(HWND, int, wchar_t*, void*, void*);
struct Stack { Stack* next; wchar_t text[256]; };

static void Progress(Plugin progress, HWND root, const wchar_t* percent, const wchar_t* stage) {
    auto description = static_cast<Stack*>(GlobalAlloc(GPTR, sizeof(Stack)));
    auto value = static_cast<Stack*>(GlobalAlloc(GPTR, sizeof(Stack)));
    if (!description || !value) ExitProcess(2);
    lstrcpynW(description->text, stage, 256);
    lstrcpynW(value->text, percent, 256);
    value->next = description;
    Stack* top = value;
    progress(root, 256, nullptr, &top, nullptr);
    Check("plugin consumes progress arguments", top == nullptr);
}

static bool Within(HWND child, HWND container) {
    RECT inner = {}, outer = {};
    GetWindowRect(child, &inner);
    GetWindowRect(container, &outer);
    return inner.right > inner.left && inner.bottom > inner.top &&
        inner.left >= outer.left && inner.top >= outer.top &&
        inner.right <= outer.right && inner.bottom <= outer.bottom;
}
static bool OccludedBySibling(HWND page) {
    RECT target = {};
    GetWindowRect(page, &target);
    for (HWND sibling = GetWindow(page, GW_HWNDPREV); sibling;
         sibling = GetWindow(sibling, GW_HWNDPREV)) {
        if (!IsWindowVisible(sibling)) continue;
        RECT rect = {}, overlap = {};
        GetWindowRect(sibling, &rect);
        if (IntersectRect(&overlap, &rect, &target)) {
            std::printf("Occluder control ID=%d\n", GetDlgCtrlID(sibling));
            return true;
        }
    }
    return false;
}

// WM_PRINTCLIENT draws the real progress control into memory; UIA visibility
// alone cannot detect this class of regression. No screen capture is used.
static bool BarPaints(HWND bar) {
    RECT rect = {};
    GetClientRect(bar, &rect);
    HDC dc = CreateCompatibleDC(nullptr);
    BITMAPINFO info = {};
    info.bmiHeader.biSize = sizeof(BITMAPINFOHEADER);
    info.bmiHeader.biWidth = rect.right;
    info.bmiHeader.biHeight = -rect.bottom;
    info.bmiHeader.biPlanes = 1;
    info.bmiHeader.biBitCount = 32;
    info.bmiHeader.biCompression = BI_RGB;
    void* bits = nullptr;
    HBITMAP bitmap = CreateDIBSection(dc, &info, DIB_RGB_COLORS, &bits, nullptr, 0);
    if (!bitmap || !bits) {
        if (bitmap) DeleteObject(bitmap);
        DeleteDC(dc);
        return false;
    }
    HGDIOBJ old = SelectObject(dc, bitmap);
    RECT fill = rect;
    FillRect(dc, &fill, static_cast<HBRUSH>(GetStockObject(WHITE_BRUSH)));
    SendMessageW(bar, WM_PRINTCLIENT, reinterpret_cast<WPARAM>(dc), PRF_CLIENT);
    GdiFlush();
    const COLORREF left = GetPixel(dc, rect.right / 4, rect.bottom / 2);
    const COLORREF right = GetPixel(dc, rect.right * 9 / 10, rect.bottom / 2);
    SelectObject(dc, old);
    DeleteObject(bitmap);
    DeleteDC(dc);
    return left != CLR_INVALID && right != CLR_INVALID && left != right;
}

int wmain(int argc, wchar_t** argv) {
    if (argc != 3) return 2;
    // Must precede creation of any window. Never call SwitchDesktop.
    HDESK original = GetThreadDesktop(GetCurrentThreadId());
    wchar_t name[96] = {};
    swprintf_s(name, L"QingyueInstallerTest-Theme-%lu", GetCurrentProcessId());
    HDESK desktop = CreateDesktopW(name, nullptr, nullptr, 0, GENERIC_ALL, nullptr);
    if (!desktop || !SetThreadDesktop(desktop)) {
        std::printf("Cannot create private test desktop: %lu\n", GetLastError());
        if (desktop) CloseDesktop(desktop);
        return 2;
    }
    INITCOMMONCONTROLSEX controls = {sizeof(controls), ICC_PROGRESS_CLASS | ICC_LISTVIEW_CLASSES};
    InitCommonControlsEx(&controls);
    HMODULE resource = LoadLibraryExW(argv[1], nullptr, LOAD_LIBRARY_AS_DATAFILE);
    HMODULE theme = LoadLibraryW(argv[2]);
    if (!resource || !theme) {
        std::printf("Cannot load test DLLs: %lu\n", GetLastError());
        return 2;
    }
    auto apply = reinterpret_cast<Plugin>(GetProcAddress(theme, "Apply"));
    auto progress = reinterpret_cast<Plugin>(GetProcAddress(theme, "Progress"));
    if (!apply || !progress) return 2;
    HWND root = CreateDialogParamW(resource, MAKEINTRESOURCEW(105), nullptr, DialogProc, 0);
    HWND placeholder = GetDlgItem(root, 1018);
    HWND page = CreateDialogParamW(resource, MAKEINTRESOURCEW(106), root, DialogProc, 0);
    if (!root || !placeholder || !page) return 2;
    RECT slot = {};
    GetWindowRect(placeholder, &slot);
    MapWindowPoints(nullptr, root, reinterpret_cast<POINT*>(&slot), 2);
    // NSIS positions its page in IDC_CHILDRECT without raising it. Deliberately
    // keep this fixture below the root siblings to expose overlapping placeholders.
    SetWindowPos(page, HWND_BOTTOM, slot.left, slot.top, slot.right-slot.left,
        slot.bottom-slot.top, SWP_NOACTIVATE | SWP_SHOWWINDOW);
    ShowWindow(root, SW_SHOWNOACTIVATE); // Private desktop only; never on the user's desktop.
    apply(root, 256, nullptr, nullptr, nullptr);
    Check("IDC_CHILDRECT is a hidden positioning placeholder",
        (GetWindowLongPtrW(placeholder, GWL_STYLE) & WS_VISIBLE) == 0);
    Check("instfiles page retains WS_CLIPSIBLINGS",
        (GetWindowLongPtrW(page, GWL_STYLE) & WS_CLIPSIBLINGS) != 0);
    Check("instfiles page is not occluded by root siblings", !OccludedBySibling(page));
    Check("page fits its layout slot", Within(page, root));
    RECT pageRect = {}, titleRect = {}, footerRect = {};
    GetWindowRect(page, &pageRect);
    GetWindowRect(GetDlgItem(root, 1038), &titleRect);
    GetWindowRect(GetDlgItem(root, 1035), &footerRect);
    Check("page lies below subtitle and above footer",
        pageRect.top >= titleRect.bottom && pageRect.bottom <= footerRect.top);
    HWND caption = GetDlgItem(page, 1102);
    HWND bar = GetDlgItem(page, 1103);
    HWND nativeBar = GetDlgItem(page, 1004);
    HWND details = GetDlgItem(page, 1016);
    HWND file = GetDlgItem(page, 1006);
    Check("progress controls belong to the instfiles child page",
        GetParent(caption) == page && GetParent(bar) == page &&
        GetParent(details) == page && GetParent(file) == page);
    Check("progress controls fit inside their parent",
        Within(caption, page) && Within(bar, page) && Within(details, page) && Within(file, page));
    Check("uninstall retains its native instruction progress",
        IsWindowVisible(nativeBar) && !IsWindowVisible(bar));
    Progress(progress, root, L"84", L"writing payload");
    wchar_t text[256] = {};
    GetWindowTextW(caption, text, 256);
    Check("84 percent and stage text are present", std::wstring(text).find(L"84%") == 0 &&
        std::wstring(text).find(L"writing payload") != std::wstring::npos);
    Check("byte progress is visible and native progress is hidden",
        IsWindowVisible(bar) && !IsWindowVisible(nativeBar) &&
        SendMessageW(bar, PBM_GETPOS, 0, 0) == 84);
    Check("file text and details list remain visible",
        IsWindowVisible(file) && IsWindowVisible(details));
    Check("84 percent bar paints distinct fill and track pixels", BarPaints(bar));
    RedrawWindow(root, nullptr, nullptr, RDW_ERASE | RDW_INVALIDATE | RDW_ALLCHILDREN | RDW_UPDATENOW);
    apply(root, 256, nullptr, nullptr, nullptr);
    Check("full repaint does not introduce a sibling overlay", !OccludedBySibling(page));
    Progress(progress, root, L"100", L"complete");
    GetWindowTextW(caption, text, 256);
    Check("completion shows 100 percent", std::wstring(text).find(L"100%") == 0 &&
        SendMessageW(bar, PBM_GETPOS, 0, 0) == 100);
    Progress(progress, root, L"-1", L"failed");
    GetWindowTextW(caption, text, 256);
    Check("error retains actual percent and displays failure stage",
        std::wstring(text).find(L"100%") == 0 && std::wstring(text).find(L"failed") != std::wstring::npos);
    // Exercise alternate dialog font metrics without changing display settings.
    HFONT font = CreateFontW(-24, 0, 0, 0, FW_NORMAL, FALSE, FALSE, FALSE,
        DEFAULT_CHARSET, OUT_DEFAULT_PRECIS, CLIP_DEFAULT_PRECIS,
        DEFAULT_QUALITY, DEFAULT_PITCH, L"Microsoft YaHei UI");
    SendMessageW(root, WM_SETFONT, reinterpret_cast<WPARAM>(font), TRUE);
    apply(root, 256, nullptr, nullptr, nullptr);
    Check("font refresh leaves instfiles unobscured", !OccludedBySibling(page));
    DestroyWindow(root);
    DeleteObject(font);
    // Callbacks are gone after DestroyWindow; loading the DLL is private to this process.
    FreeLibrary(theme);
    FreeLibrary(resource);
    SetThreadDesktop(original);
    CloseDesktop(desktop);
    std::printf("Checks=%d Failures=%d; no desktop switch or installer execution\n", checks, failures);
    return failures ? 1 : 0;
}
