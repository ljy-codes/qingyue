#!/bin/bash
set -euo pipefail

# Some macOS command-line tools invoke Perl while packaging. The desktop
# environment may expose an unavailable C.UTF-8 locale, which creates noisy
# warnings during an otherwise valid build.
export LC_ALL=C LANG=C LC_CTYPE=C

reader_script_dir="$(cd "$(dirname "$0")" && pwd)"
reader_project_dir="$(cd "$reader_script_dir/.." && pwd)"
reader_product_root="$(cd "$reader_project_dir/.." && pwd)/交付产品"
reader_build_dir="$reader_project_dir/build"
reader_info_plist="$reader_project_dir/Resources/Info.plist"
reader_version="$(/usr/bin/plutil -extract CFBundleShortVersionString raw "$reader_info_plist")"
reader_build_number="$(/usr/bin/plutil -extract CFBundleVersion raw "$reader_info_plist")"
reader_product_dir="$reader_product_root/轻阅-macOS-$reader_version"
reader_app_dir="$reader_product_dir/轻阅.app"
reader_arch="${QINGYUE_ARCH:-universal}"
reader_macos_target="${QINGYUE_MACOS_TARGET:-13.0}"

case "$reader_arch" in
    x86_64|arm64|universal) ;;
    *)
        echo "QINGYUE_ARCH 必须是 x86_64、arm64 或 universal，当前为：$reader_arch" >&2
        exit 2
        ;;
esac

case "$reader_macos_target" in
    13|13.0|13.[0-9]*|1[4-9]|1[4-9].[0-9]*) ;;
    *)
        echo "QINGYUE_MACOS_TARGET 必须是 macOS 13 或更高版本，当前为：$reader_macos_target" >&2
        exit 2
        ;;
esac

mkdir -p "$reader_build_dir/ModuleCache" "$reader_build_dir/ModuleCache-x86_64" "$reader_build_dir/ModuleCache-arm64" "$reader_product_dir"
reader_sources=("$reader_project_dir"/Sources/*.swift)
if [ ! -f "${reader_sources[0]}" ]; then
    echo "找不到 Sources/*.swift" >&2
    exit 1
fi

reader_sdk="$(/usr/bin/xcrun --show-sdk-path)"
echo "正在编译轻阅 $reader_version（Mac 构建 $reader_build_number）· 架构 $reader_arch · macOS $reader_macos_target+"

build_reader_arch() {
    local arch="$1"
    /usr/bin/xcrun swiftc \
        -O -whole-module-optimization \
        -target "${arch}-apple-macosx${reader_macos_target}" \
        -sdk "$reader_sdk" \
        -module-cache-path "$reader_build_dir/ModuleCache-$arch" \
        -framework AppKit -framework CoreFoundation \
        "${reader_sources[@]}" \
        -o "$reader_build_dir/LightReader.$arch"
}

case "$reader_arch" in
    x86_64|arm64)
        build_reader_arch "$reader_arch"
        /bin/cp "$reader_build_dir/LightReader.$reader_arch" "$reader_build_dir/LightReader"
        ;;
    universal)
        build_reader_arch x86_64
        build_reader_arch arm64
        /usr/bin/lipo -create "$reader_build_dir/LightReader.x86_64" "$reader_build_dir/LightReader.arm64" -output "$reader_build_dir/LightReader"
        ;;
esac

/usr/bin/xcrun swift \
    -module-cache-path "$reader_build_dir/ModuleCache" \
    "$reader_script_dir/make-icon.swift" "$reader_build_dir/AppIcon.icns"

# Rebuild the delivery app from scratch so stale resources cannot survive a
# version or icon-theme change.
/bin/rm -rf "$reader_app_dir"
mkdir -p "$reader_app_dir/Contents/MacOS" "$reader_app_dir/Contents/Resources/IconThemes"
/bin/cp "$reader_build_dir/LightReader" "$reader_app_dir/Contents/MacOS/LightReader"
/bin/cp "$reader_info_plist" "$reader_app_dir/Contents/Info.plist"
/bin/cp "$reader_build_dir/AppIcon.icns" "$reader_app_dir/Contents/Resources/AppIcon.icns"
/bin/cp "$reader_project_dir"/Resources/IconThemes/*.png "$reader_app_dir/Contents/Resources/IconThemes/"
/bin/cp "$reader_project_dir/../操作指导/使用说明.md" "$reader_app_dir/Contents/Resources/使用说明.md"
/bin/cp "$reader_project_dir/Samples/示例.md" "$reader_app_dir/Contents/Resources/示例.md"
/bin/cp "$reader_project_dir/../操作指导/使用说明.md" "$reader_product_dir/使用说明.md"
/usr/bin/plutil -lint "$reader_app_dir/Contents/Info.plist"
/usr/bin/codesign --force --sign - "$reader_app_dir"
/usr/bin/codesign --verify --deep --strict "$reader_app_dir"
(cd "$reader_product_dir" && shasum -a 256 \
    "轻阅.app/Contents/MacOS/LightReader" \
    "轻阅.app/Contents/Info.plist" \
    "轻阅.app/Contents/Resources/AppIcon.icns" \
    "使用说明.md" > SHA256SUMS.txt)
/bin/rm -f "$reader_product_root/轻阅-macOS-$reader_version-交付.zip"
/usr/bin/ditto -c -k --sequesterRsrc --keepParent "$reader_product_dir" "$reader_product_root/轻阅-macOS-$reader_version-交付.zip"

echo "构建完成：轻阅 $reader_version（Mac 构建 $reader_build_number）"
echo "$reader_product_dir"
/usr/bin/du -sh "$reader_product_dir" "$reader_product_root/轻阅-macOS-$reader_version-交付.zip"
