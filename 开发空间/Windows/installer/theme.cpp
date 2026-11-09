// Qingyue theme, adapted from EnvDock (MIT); see UPSTREAM.md.
// Uses documented Win32 APIs; native controls keep their input/accessibility behavior.
#define UNICODE
#define _UNICODE
#define WIN32_LEAN_AND_MEAN
#include <windows.h>
#include <commctrl.h>
#include <uxtheme.h>
#include <dwmapi.h>
#include <string>

static constexpr COLORREF background = RGB(12,18,30);
static constexpr COLORREF surface = RGB(22,33,51);
static constexpr COLORREF border = RGB(52,73,94);
static constexpr COLORREF foreground = RGB(228,237,247);
static constexpr COLORREF muted = RGB(124,146,168);
static constexpr COLORREF accent = RGB(107,192,255);
static HBRUSH backgroundBrush = nullptr;
static HBRUSH surfaceBrush = nullptr;
static constexpr UINT_PTR themeId = 0xED01;
static constexpr UINT_PTR timerId = 0xED02;
static HWND rootWindow = nullptr;
static HFONT headingFont = nullptr;
static bool byteProgress = false;
static int progressPercent = 0;
static std::wstring progressStage;
static std::wstring progressCaption;

static BOOL CALLBACK UpdateProgress(HWND window, LPARAM) {
    const int control = GetDlgCtrlID(window);
    if (control == 1004) ShowWindow(window, byteProgress ? SW_HIDE : SW_SHOW);
    if (control == 1103) {
        ShowWindow(window, byteProgress ? SW_SHOW : SW_HIDE);
        if (byteProgress) {
            SendMessageW(window,PBM_SETRANGE32,0,100);
            SendMessageW(window,PBM_SETPOS,progressPercent,0);
        }
    }
    if (control == 1102 && byteProgress) {
        wchar_t current[256] = {};
        GetWindowTextW(window,current,256);
        if (progressCaption != current) SetWindowTextW(window,progressCaption.c_str());
    }
    return TRUE;
}

static void BrandText(HDC dc, RECT r, const wchar_t* text, int size, int weight, COLORREF color) {
    HFONT font = CreateFontW(-size,0,0,0,weight,FALSE,FALSE,FALSE,DEFAULT_CHARSET,
        OUT_DEFAULT_PRECIS,CLIP_DEFAULT_PRECIS,CLEARTYPE_QUALITY,DEFAULT_PITCH,L"Microsoft YaHei UI");
    HGDIOBJ old = SelectObject(dc,font);
    SetTextColor(dc,color);
    SetBkMode(dc,TRANSPARENT);
    DrawTextW(dc,text,-1,&r,DT_LEFT|DT_TOP|DT_NOPREFIX);
    SelectObject(dc,old);
    DeleteObject(font);
}
static void PaintBrand(const DRAWITEMSTRUCT* item) {
    HDC dc = item->hDC;
    RECT r = item->rcItem;
    const int width = r.right, height = r.bottom;
    const int dpi = static_cast<int>(GetDpiForWindow(item->hwndItem));
    const auto px = [dpi](int n) {return MulDiv(n,dpi,96);};
    for (int y=0;y<height;++y) {
        int t = height ? y*255/height : 0;
        SetDCBrushColor(dc,RGB(17-t*5/255,36-t*16/255,64-t*30/255));
        RECT line = {0,y,width,y+1};
        FillRect(dc,&line,static_cast<HBRUSH>(GetStockObject(DC_BRUSH)));
    }
    HPEN grid = CreatePen(PS_SOLID,1,RGB(24,46,73));
    HGDIOBJ oldPen = SelectObject(dc,grid);
    for(int x=px(24);x<width;x+=px(30)) {MoveToEx(dc,x,px(155),nullptr);LineTo(dc,x,height-px(80));}
    for(int y=px(155);y<height-px(80);y+=px(30)) {MoveToEx(dc,px(24),y,nullptr);LineTo(dc,width-px(24),y);}
    SelectObject(dc,oldPen); DeleteObject(grid);
    HICON icon = static_cast<HICON>(LoadImageW(GetModuleHandleW(nullptr),MAKEINTRESOURCEW(103),
        IMAGE_ICON,px(84),px(84),LR_DEFAULTCOLOR));
    if (icon) {DrawIconEx(dc,px(28),px(44),icon,px(84),px(84),0,nullptr,DI_NORMAL); DestroyIcon(icon);}
    RECT title={px(30),px(154),width-px(20),px(200)};
    BrandText(dc,title,L"轻阅",px(32),FW_SEMIBOLD,RGB(235,244,255));
    RECT english={px(31),px(202),width-px(18),px(228)};
    BrandText(dc,english,L"Q I N G Y U E",px(12),FW_MEDIUM,RGB(129,191,239));
    RECT motto={px(31),px(260),width-px(20),height-px(80)};
    BrandText(dc,motto,L"把注意力\n交还给文字。",px(22),FW_NORMAL,RGB(204,222,242));
    RECT foot={px(31),height-px(60),width-px(14),height-px(12)};
    BrandText(dc,foot,L"LOCAL FIRST\nWINDOWS  /  x64",px(10),FW_MEDIUM,RGB(137,165,197));
}

static void Fill(HDC dc, RECT rect, COLORREF color) {
    SetDCBrushColor(dc, color);
    FillRect(dc, &rect, static_cast<HBRUSH>(GetStockObject(DC_BRUSH)));
}
static void Frame(HDC dc, RECT rect, COLORREF color) {
    SetDCBrushColor(dc, color);
    FrameRect(dc, &rect, static_cast<HBRUSH>(GetStockObject(DC_BRUSH)));
}
static void PaintButton(HWND window) {
    PAINTSTRUCT ps;
    HDC dc = BeginPaint(window, &ps);
    RECT rect;
    GetClientRect(window, &rect);
    const auto style = GetWindowLongPtr(window, GWL_STYLE) & BS_TYPEMASK;
    bool disabled = !IsWindowEnabled(window);
    bool focused = GetFocus() == window;
    bool choice = style == BS_CHECKBOX || style == BS_AUTOCHECKBOX ||
        style == BS_3STATE || style == BS_AUTO3STATE ||
        style == BS_RADIOBUTTON || style == BS_AUTORADIOBUTTON;
    bool group = style == BS_GROUPBOX;
    bool primary = GetDlgCtrlID(window) == IDOK && !choice && !group;
    bool pressed = (SendMessage(window, BM_GETSTATE, 0, 0) & BST_PUSHED) != 0;
    bool hover = GetProp(window, L"QingyueHover") != nullptr;
    COLORREF fill = choice || group ? background :
        primary && !disabled ? (pressed ? RGB(77,161,231) : accent) :
        hover && !disabled ? RGB(34,51,70) : surface;
    // Group boxes overlap sibling edit/browse controls: never erase their interior.
    if (!group) Fill(dc, rect, fill);
    if (!choice) Frame(dc, rect, focused ? accent : border);
    HFONT font = reinterpret_cast<HFONT>(SendMessage(window, WM_GETFONT, 0, 0));
    HGDIOBJ oldFont = font ? SelectObject(dc, font) : nullptr;
    SetBkMode(dc, TRANSPARENT);
    SetTextColor(dc, disabled ? muted : primary ? background : foreground);
    WCHAR text[1024] = {};
    GetWindowText(window, text, 1024);
    RECT label = rect;
    UINT format = DT_SINGLELINE | DT_VCENTER | DT_CENTER;
    if (choice) {
        int size = MulDiv(13, GetDpiForWindow(window), 96);
        RECT box = {rect.left + 1, (rect.bottom-size)/2, rect.left+1+size, (rect.bottom+size)/2};
        Fill(dc, box, surface);
        Frame(dc, box, disabled ? border : focused ? accent : muted);
        if (SendMessage(window, BM_GETCHECK, 0, 0) != BST_UNCHECKED) {
            InflateRect(&box, -3, -3);
            Fill(dc, box, accent);
        }
        label.left = box.right + MulDiv(9, GetDpiForWindow(window), 96);
        format = DT_SINGLELINE | DT_VCENTER | DT_LEFT;
    } else if (group) {
        label.left += 10;
        label.top += 2;
        format = DT_SINGLELINE | DT_TOP | DT_LEFT;
    }
    DrawText(dc, text, -1, &label, format);
    if (focused) {
        RECT focus = rect;
        InflateRect(&focus, -3, -3);
        Frame(dc, focus, accent);
    }
    if (oldFont) SelectObject(dc, oldFont);
    EndPaint(window, &ps);
}

static LRESULT CALLBACK ThemeProc(HWND window, UINT msg, WPARAM w, LPARAM l,
                                 UINT_PTR id, DWORD_PTR kind) {
    if (kind == 1 && msg == WM_PAINT) { PaintButton(window); return 0; }
    if (kind == 3 && msg == WM_PAINT) {
        PAINTSTRUCT ps; HDC dc = BeginPaint(window,&ps);
        RECT rect; GetClientRect(window,&rect); Fill(dc,rect,border);
        EndPaint(window,&ps); return 0;
    }
    if (kind == 4 && msg == WM_NCPAINT) {
        LRESULT result = DefSubclassProc(window,msg,w,l);
        HDC dc = GetWindowDC(window);
        RECT rect; GetWindowRect(window,&rect);
        OffsetRect(&rect,-rect.left,-rect.top);
        Frame(dc,rect,GetFocus()==window ? accent : border);
        ReleaseDC(window,dc);
        return result;
    }
    if (kind == 1 && msg == WM_MOUSEMOVE) {
        if (!GetProp(window,L"QingyueHover")) {
            SetProp(window,L"QingyueHover",reinterpret_cast<HANDLE>(1));
            TRACKMOUSEEVENT track = {sizeof(track),TME_LEAVE,window,0};
            TrackMouseEvent(&track);
            InvalidateRect(window,nullptr,FALSE);
        }
    }
    if (kind == 1 && msg == WM_MOUSELEAVE) {
        RemoveProp(window,L"QingyueHover");
        InvalidateRect(window,nullptr,FALSE);
    }
    switch (msg) {
    case WM_DRAWITEM: {
        const auto item = reinterpret_cast<DRAWITEMSTRUCT*>(l);
        if (item && item->CtlID == 1100) {PaintBrand(item); return TRUE;}
        break;
    }
    case WM_CTLCOLORDLG:
    case WM_CTLCOLORSTATIC:
    case WM_CTLCOLORBTN:
    case WM_CTLCOLORLISTBOX:
    case WM_CTLCOLOREDIT: {
        HDC dc = reinterpret_cast<HDC>(w);
        bool input = msg == WM_CTLCOLOREDIT || msg == WM_CTLCOLORLISTBOX;
        int controlId = GetDlgCtrlID(reinterpret_cast<HWND>(l));
        SetTextColor(dc, controlId == 1101 ? accent :
            (controlId == 1028 || controlId == 1038) ? RGB(159,180,204) : foreground);
        SetBkColor(dc,input ? surface : background);
        return reinterpret_cast<LRESULT>(input ? surfaceBrush : backgroundBrush);
    }
    case WM_ERASEBKGND:
        if (kind == 2) {
            RECT rect; GetClientRect(window,&rect);
            FillRect(reinterpret_cast<HDC>(w),&rect,backgroundBrush);
            return 1;
        }
        break;
    case WM_NCDESTROY:
        RemoveProp(window,L"QingyueHover");
        RemoveWindowSubclass(window,ThemeProc,id);
        break;
    }
    LRESULT result = DefSubclassProc(window,msg,w,l);
    if (kind == 1 && (msg == WM_SETFOCUS || msg == WM_KILLFOCUS ||
        msg == WM_ENABLE || msg == BM_SETCHECK || msg == BM_SETSTATE ||
        msg == WM_SETTEXT || msg == WM_LBUTTONUP || msg == WM_LBUTTONDOWN)) {
        InvalidateRect(window,nullptr,FALSE);
    }
    return result;
}

static BOOL CALLBACK ThemeChild(HWND window, LPARAM) {
    DWORD_PTR existing = 0;
    if (GetWindowSubclass(window,ThemeProc,themeId,&existing)) return TRUE;
    WCHAR name[64] = {};
    GetClassName(window,name,64);
    DWORD_PTR kind = lstrcmpi(name,L"Button") == 0 ? 1 :
        lstrcmpi(name,L"#32770") == 0 ? 2 :
        lstrcmpi(name,L"Static") == 0 && GetDlgCtrlID(window) == 1035 ? 3 :
        lstrcmpi(name,L"Edit") == 0 ? 4 : 0;
    SetWindowSubclass(window,ThemeProc,themeId,kind);
    SetWindowTheme(window,L"",L"");
    if (kind == 4) {
        SetWindowLongPtr(window,GWL_EXSTYLE,GetWindowLongPtr(window,GWL_EXSTYLE)&~WS_EX_CLIENTEDGE);
        SetWindowPos(window,nullptr,0,0,0,0,SWP_NOMOVE|SWP_NOSIZE|SWP_NOZORDER|SWP_FRAMECHANGED);
    }
    if (lstrcmpi(name,WC_LISTVIEW) == 0) {
        ListView_SetBkColor(window,background);
        ListView_SetTextBkColor(window,background);
        ListView_SetTextColor(window,foreground);
    }
    if (lstrcmpi(name,PROGRESS_CLASS) == 0) {
        SendMessage(window,PBM_SETBKCOLOR,0,surface);
        SendMessage(window,PBM_SETBARCOLOR,0,accent);
    }
    InvalidateRect(window,nullptr,TRUE);
    return TRUE;
}
static void CALLBACK Refresh(HWND window, UINT, UINT_PTR, DWORD) {
    EnumChildWindows(window,ThemeChild,0);
    if (byteProgress) EnumChildWindows(window,UpdateProgress,0);
}

// Values come from completed file writes, never from a timer or instruction count.
// NSIS /NOUNLOAD keeps the shared state and subclass callbacks alive.
extern "C" __declspec(dllexport) void __cdecl Progress(HWND parent, int size, wchar_t*, void* stack, void*) {
    struct Stack { Stack* next; wchar_t text[1]; };
    auto top = static_cast<Stack**>(stack);
    if (!top || !*top || size <= 0) return;
    const auto pop = [top]() {
        Stack* item = *top;
        if (!item) return std::wstring{};
        std::wstring text = item->text;
        *top = item->next;
        GlobalFree(item);
        return text;
    };
    const auto value = pop();
    progressStage = pop();
    const int percent = _wtoi(value.c_str());
    if (percent >= 0) progressPercent = percent > 100 ? 100 : percent;
    byteProgress = true;
    progressCaption = std::to_wstring(progressPercent) + L"%  ·  " + progressStage;
    if (parent) {
        EnumChildWindows(parent,ThemeChild,0);
        EnumChildWindows(parent,UpdateProgress,0);
        // Paint before the next synchronous extraction starts.
        RedrawWindow(parent,nullptr,nullptr,RDW_INVALIDATE|RDW_ALLCHILDREN|RDW_UPDATENOW);
    }
}

// NSIS calls this on its GUI thread. /NOUNLOAD keeps subclass code alive until exit.
extern "C" __declspec(dllexport) void __cdecl Apply(HWND parent, int, wchar_t*, void*, void*) {
    if (!parent) return;
    if (rootWindow == parent) {
        EnumChildWindows(parent,ThemeChild,0);
        RedrawWindow(parent,nullptr,nullptr,RDW_INVALIDATE|RDW_ALLCHILDREN);
        return;
    }
    rootWindow = parent;
    backgroundBrush = CreateSolidBrush(background);
    surfaceBrush = CreateSolidBrush(surface);
    headingFont = CreateFontW(-MulDiv(25,static_cast<int>(GetDpiForWindow(parent)),96),
        0,0,0,FW_SEMIBOLD,FALSE,FALSE,FALSE,DEFAULT_CHARSET,
        OUT_DEFAULT_PRECIS,CLIP_DEFAULT_PRECIS,CLEARTYPE_QUALITY,DEFAULT_PITCH,L"Microsoft YaHei UI");
    SendDlgItemMessageW(parent,1037,WM_SETFONT,reinterpret_cast<WPARAM>(headingFont),TRUE);
    SetWindowSubclass(parent,ThemeProc,themeId,2);
    BOOL dark = TRUE;
    DwmSetWindowAttribute(parent,20,&dark,sizeof(dark));
    DwmSetWindowAttribute(parent,35,&background,sizeof(background));
    DwmSetWindowAttribute(parent,36,&foreground,sizeof(foreground));
    EnumChildWindows(parent,ThemeChild,0);
    SetTimer(parent,timerId,100,Refresh);
    RedrawWindow(parent,nullptr,nullptr,RDW_INVALIDATE|RDW_ALLCHILDREN);
}
