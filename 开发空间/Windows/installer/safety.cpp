// Native NSIS helpers. All destructive operations use canonical, bounded paths.
#define UNICODE
#define _UNICODE
#define WIN32_LEAN_AND_MEAN
#include <windows.h>
#include <shlobj.h>
#include <shellapi.h>
#include <string>
#include <vector>
#include <algorithm>

struct Stack { Stack* next; wchar_t text[1]; };
static Stack** stackTop;
static int stringSize;
static void Init(int size, void* stack) { stringSize=size; stackTop=static_cast<Stack**>(stack); }
static std::wstring Pop() {
    if (!stackTop || !*stackTop) return {};
    Stack* item=*stackTop; std::wstring value=item->text;
    *stackTop=item->next; GlobalFree(item); return value;
}
static void Push(const std::wstring& text) {
    auto item=static_cast<Stack*>(GlobalAlloc(GPTR,sizeof(Stack)+static_cast<SIZE_T>(stringSize)*sizeof(wchar_t)));
    if (!item) return;
    lstrcpynW(item->text,text.c_str(),stringSize); item->next=*stackTop; *stackTop=item;
}
static bool Equal(const std::wstring& a,const std::wstring& b) {return _wcsicmp(a.c_str(),b.c_str())==0;}
static std::wstring Canonical(const std::wstring& path) {
    if (path.size()<4 || path[1]!=L':' || path[2]!=L'\\' || path.size()>220 ||
        path.find_first_of(L"*?\"<>|")!=std::wstring::npos || path.find(L':',2)!=std::wstring::npos)
        return {};
    wchar_t full[32768]={};
    DWORD size=GetFullPathNameW(path.c_str(),32768,full,nullptr);
    if (!size || size>=32768) return {};
    std::wstring result(full);
    while(result.size()>3 && result.back()==L'\\') result.pop_back();
    return result;
}
static bool Contains(const std::wstring& parent,const std::wstring& child) {
    return Equal(parent,child) || (child.size()>parent.size() &&
        _wcsnicmp(parent.c_str(),child.c_str(),parent.size())==0 && child[parent.size()]==L'\\');
}
static std::wstring NoLinks(const std::wstring& path) {
    for(size_t i=3;i<=path.size();++i) {
        if(i<path.size() && path[i]!=L'\\') continue;
        const std::wstring part=path.substr(0,i);
        const DWORD attr=GetFileAttributesW(part.c_str());
        if(attr!=INVALID_FILE_ATTRIBUTES && (attr&FILE_ATTRIBUTE_REPARSE_POINT))
            return L"路径包含符号链接或目录联接，请选择普通本地目录。";
    }
    return {};
}
static std::wstring Known(REFKNOWNFOLDERID id) {
    PWSTR value=nullptr; std::wstring result;
    if(SUCCEEDED(SHGetKnownFolderPath(id,0,nullptr,&value))) {result=value;CoTaskMemFree(value);}
    return result;
}
static bool TreeHasLinks(const std::wstring& root) {
    WIN32_FIND_DATAW data; HANDLE search=FindFirstFileW((root+L"\\*").c_str(),&data);
    if(search==INVALID_HANDLE_VALUE) return false;
    bool links=false;
    do {
        if(Equal(data.cFileName,L".") || Equal(data.cFileName,L"..")) continue;
        if(data.dwFileAttributes&FILE_ATTRIBUTE_REPARSE_POINT) {links=true;break;}
        if((data.dwFileAttributes&FILE_ATTRIBUTE_DIRECTORY) && TreeHasLinks(root+L"\\"+data.cFileName)) {links=true;break;}
    } while(FindNextFileW(search,&data));
    FindClose(search); return links;
}
static std::wstring Validate(const std::wstring& raw,const std::wstring& dataRoot) {
    auto path=Canonical(raw), data=Canonical(dataRoot);
    if(path.size()<=3 || data.empty()) return L"请选择磁盘下的专用文件夹，路径不要超过 220 个字符。";
    const KNOWNFOLDERID protectedIds[]={FOLDERID_Profile,FOLDERID_Desktop,FOLDERID_Documents,
        FOLDERID_Downloads,FOLDERID_LocalAppData,FOLDERID_RoamingAppData,FOLDERID_Programs,
        FOLDERID_ProgramFiles,FOLDERID_ProgramFilesX86};
    for(const auto& id:protectedIds) {auto k=Known(id); if(!k.empty() && Equal(k,path)) return L"请在所选位置新建轻阅专用文件夹。";}
    wchar_t windows[MAX_PATH]={};GetWindowsDirectoryW(windows,MAX_PATH);
    if(Contains(windows,path) || Contains(path,data) || Contains(data,path))
        return L"安装目录不能是系统目录，也不能与轻阅用户数据目录重叠。";
    auto err=NoLinks(path);if(!err.empty()) return err;
    if(TreeHasLinks(path)) return L"现有安装目录含链接，已停止操作以保护链接指向的文件。";
    return {};
}
// Never follows reparse points. The parent is checked immediately before entry.
static bool RemoveTree(const std::wstring& root) {
    DWORD attributes=GetFileAttributesW(root.c_str());
    if(attributes==INVALID_FILE_ATTRIBUTES)
        return GetLastError()==ERROR_FILE_NOT_FOUND || GetLastError()==ERROR_PATH_NOT_FOUND;
    if(attributes&FILE_ATTRIBUTE_REPARSE_POINT)
        return (attributes&FILE_ATTRIBUTE_DIRECTORY) ? RemoveDirectoryW(root.c_str())!=FALSE : DeleteFileW(root.c_str())!=FALSE;
    if(!(attributes&FILE_ATTRIBUTE_DIRECTORY)) return DeleteFileW(root.c_str())!=FALSE;
    WIN32_FIND_DATAW data; HANDLE search=FindFirstFileW((root+L"\\*").c_str(),&data);
    if(search==INVALID_HANDLE_VALUE) return false;
    bool success=true;
    do {
        if(Equal(data.cFileName,L".") || Equal(data.cFileName,L"..")) continue;
        const auto path=root+L"\\"+data.cFileName;
        if(data.dwFileAttributes&FILE_ATTRIBUTE_DIRECTORY) {
            if(!RemoveTree(path)) success=false;
        } else {
            // Clear read-only only on data files explicitly selected for purge.
            if(data.dwFileAttributes&FILE_ATTRIBUTE_READONLY)
                SetFileAttributesW(path.c_str(),data.dwFileAttributes&~FILE_ATTRIBUTE_READONLY);
            if(!DeleteFileW(path.c_str())) success=false;
        }
    } while(FindNextFileW(search,&data));
    const DWORD error=GetLastError();FindClose(search);
    if(error!=ERROR_NO_MORE_FILES) success=false;
    return success && RemoveDirectoryW(root.c_str())!=FALSE;
}
static bool MarkerMatches(const std::wstring& root,const std::wstring& product) {
    const auto marker=root+L"\\Qingyue.install";
    const DWORD attributes=GetFileAttributesW(marker.c_str());
    if(attributes==INVALID_FILE_ATTRIBUTES || (attributes&FILE_ATTRIBUTE_DIRECTORY) ||
        (attributes&FILE_ATTRIBUTE_REPARSE_POINT)) return false;
    HANDLE file=CreateFileW(marker.c_str(),GENERIC_READ,
        FILE_SHARE_READ|FILE_SHARE_WRITE|FILE_SHARE_DELETE,nullptr,OPEN_EXISTING,
        FILE_ATTRIBUTE_NORMAL,nullptr);
    if(file==INVALID_HANDLE_VALUE) return false;
    const DWORD length=GetFileSize(file,nullptr);
    if(length==INVALID_FILE_SIZE || length>1024) {CloseHandle(file);return false;}
    std::vector<BYTE> bytes(length);
    DWORD read=0;
    const bool loaded=length==0 || (ReadFile(file,bytes.data(),length,&read,nullptr)!=FALSE && read==length);
    CloseHandle(file);
    if(!loaded) return false;
    std::wstring value;
    if(bytes.size()>=2 && bytes[0]==0xFF && bytes[1]==0xFE) {
        for(size_t i=2;i+1<bytes.size();i+=2)
            value.push_back(static_cast<wchar_t>(bytes[i]) |
                static_cast<wchar_t>(bytes[i+1])<<8);
    } else if(bytes.size()>=2 && bytes[1]==0 && bytes.size()%2==0) {
        for(size_t i=0;i+1<bytes.size();i+=2)
            value.push_back(static_cast<wchar_t>(bytes[i]));
    } else {
        for(const auto byte:bytes) value.push_back(static_cast<wchar_t>(byte));
    }
    while(!value.empty() && (value.back()==L'\r' || value.back()==L'\n' || value.back()==L'\0'))
        value.pop_back();
    return Equal(value,product);
}
static bool RemoveInstallContents(const std::wstring& root) {
    const DWORD attributes=GetFileAttributesW(root.c_str());
    if(attributes==INVALID_FILE_ATTRIBUTES || (attributes&FILE_ATTRIBUTE_REPARSE_POINT) ||
        !(attributes&FILE_ATTRIBUTE_DIRECTORY)) return false;
    WIN32_FIND_DATAW data; HANDLE search=FindFirstFileW((root+L"\\*").c_str(),&data);
    if(search==INVALID_HANDLE_VALUE) return false;
    bool success=true;
    do {
        if(Equal(data.cFileName,L".") || Equal(data.cFileName,L"..")) continue;
        const auto path=root+L"\\"+data.cFileName;
        const bool directory=(data.dwFileAttributes&FILE_ATTRIBUTE_DIRECTORY)!=0;
        const bool reserved=!directory &&
            (Equal(data.cFileName,L"Uninstall.exe") || Equal(data.cFileName,L"Qingyue.install"));
        if(reserved) continue;
        if(data.dwFileAttributes&FILE_ATTRIBUTE_REPARSE_POINT) {
            success=false;
        } else if(directory) {
            if(!RemoveInstallContents(path) || !RemoveDirectoryW(path.c_str()))
                success=false;
        } else {
            if(data.dwFileAttributes&FILE_ATTRIBUTE_READONLY &&
                !SetFileAttributesW(path.c_str(),data.dwFileAttributes&~FILE_ATTRIBUTE_READONLY))
                success=false;
            if(!DeleteFileW(path.c_str())) success=false;
        }
    } while(FindNextFileW(search,&data));
    const DWORD error=GetLastError();FindClose(search);
    if(error!=ERROR_NO_MORE_FILES) success=false;
    return success;
}

extern "C" __declspec(dllexport) void __cdecl ValidateRoot(HWND,int size,wchar_t*,void* stack,void*) {
    Init(size,stack);auto root=Pop(),data=Pop();Push(Validate(root,data));
}
extern "C" __declspec(dllexport) void __cdecl NormalizePath(HWND,int size,wchar_t*,void* stack,void*) {
    Init(size,stack);Push(Canonical(Pop()));
}
extern "C" __declspec(dllexport) void __cdecl RequestedDirectory(HWND,int size,wchar_t*,void* stack,void*) {
    Init(size,stack);int count=0;
    LPWSTR* args=CommandLineToArgvW(GetCommandLineW(),&count);
    std::wstring directory;
    if(args) {
        for(int i=1;i<count;++i) {
            if(wcsncmp(args[i],L"/D=",3)!=0) continue;
            directory=args[i]+3;
            // NSIS /D= is the final option and permits an unquoted path with spaces.
            for(int j=i+1;j<count;++j) {directory+=L" ";directory+=args[j];}
            break;
        }
        LocalFree(args);
    }
    Push(directory);
}
extern "C" __declspec(dllexport) void __cdecl HasFiles(HWND,int size,wchar_t*,void* stack,void*) {
    Init(size,stack);auto root=Pop();WIN32_FIND_DATAW data;
    HANDLE search=FindFirstFileW((root+L"\\*").c_str(),&data);
    if(search==INVALID_HANDLE_VALUE) {
        DWORD error=GetLastError();
        Push(error==ERROR_FILE_NOT_FOUND || error==ERROR_PATH_NOT_FOUND ? L"0" : L"1");
        return;
    }
    bool found=false;
    do {if(!Equal(data.cFileName,L".") && !Equal(data.cFileName,L"..")) {found=true;break;}}
    while(FindNextFileW(search,&data));
    FindClose(search);Push(found?L"1":L"0");
}
extern "C" __declspec(dllexport) void __cdecl CanReplace(HWND,int size,wchar_t*,void* stack,void*) {
    Init(size,stack);auto path=Pop();
    auto links=NoLinks(path);
    if(!links.empty()) {Push(links);return;}
    DWORD attr=GetFileAttributesW(path.c_str());
    if(attr==INVALID_FILE_ATTRIBUTES) {
        DWORD error=GetLastError();
        Push(error==ERROR_FILE_NOT_FOUND || error==ERROR_PATH_NOT_FOUND ? L"" : L"无法检查程序文件权限。");
        return;
    }
    HANDLE file=CreateFileW(path.c_str(),GENERIC_WRITE|DELETE,
        FILE_SHARE_READ|FILE_SHARE_WRITE|FILE_SHARE_DELETE,nullptr,OPEN_EXISTING,FILE_ATTRIBUTE_NORMAL,nullptr);
    if(file==INVALID_HANDLE_VALUE) {Push(L"程序文件被占用、只读或没有写入权限，请处理后重试。");return;}
    CloseHandle(file);Push(L"");
}
extern "C" __declspec(dllexport) void __cdecl PurgeData(HWND,int size,wchar_t*,void* stack,void*) {
    Init(size,stack);auto raw=Pop(),leaf=Pop();
    const auto expected=Known(FOLDERID_LocalAppData)+L"\\"+leaf, path=Canonical(raw);
    if(leaf.empty() || leaf.find_first_of(L"\\/:.")!=std::wstring::npos || !Equal(expected,path) ||
        !NoLinks(path).empty()) {Push(L"用户数据路径校验失败，已保留数据。");return;}
    Push(RemoveTree(path)?L"":L"部分用户数据无法删除，请关闭占用程序后重试。");
}
extern "C" __declspec(dllexport) void __cdecl PurgeInstall(HWND,int size,wchar_t*,void* stack,void*) {
    Init(size,stack);auto raw=Pop(),data=Pop(),product=Pop();
    const auto path=Canonical(raw);
    const auto validation=Validate(raw,data);
    if(product.empty() || path.empty() || !validation.empty() || !MarkerMatches(path,product)) {
        Push(L"安装目录校验失败或安装标记不匹配，已保留安装目录。");
        return;
    }
    Push(RemoveInstallContents(path)?L"":L"部分安装文件无法删除，请关闭占用程序后重试。");
}
