#!/bin/bash
set -euo pipefail

reader_script_dir="$(cd "$(dirname "$0")" && pwd)"
reader_project_dir="$(cd "$reader_script_dir/.." && pwd)"
reader_product_dir="$(cd "$reader_project_dir/.." && pwd)/交付产品"
reader_build_dir="$reader_project_dir/build"
reader_app_dir="$reader_product_dir/轻阅.app"

mkdir -p "$reader_build_dir/ModuleCache" "$reader_product_dir"
reader_sources=("$reader_project_dir"/Sources/*.swift)
if [ ! -f "${reader_sources[0]}" ]; then
    echo "找不到 Sources/*.swift" >&2
    exit 1
fi

reader_sdk="$(/usr/bin/xcrun --show-sdk-path)"
echo "正在编译轻阅 · Intel macOS 13+"
/usr/bin/xcrun swiftc \
    -O -whole-module-optimization \
    -target x86_64-apple-macosx13.0 \
    -sdk "$reader_sdk" \
    -module-cache-path "$reader_build_dir/ModuleCache" \
    -framework AppKit -framework CoreFoundation \
    "${reader_sources[@]}" \
    -o "$reader_build_dir/LightReader"

/usr/bin/xcrun swift \
    -module-cache-path "$reader_build_dir/ModuleCache" \
    "$reader_script_dir/make-icon.swift" "$reader_build_dir/AppIcon.icns"

mkdir -p "$reader_app_dir/Contents/MacOS" "$reader_app_dir/Contents/Resources"
/bin/cp "$reader_build_dir/LightReader" "$reader_app_dir/Contents/MacOS/LightReader"
/bin/cp "$reader_project_dir/Resources/Info.plist" "$reader_app_dir/Contents/Info.plist"
/bin/cp "$reader_build_dir/AppIcon.icns" "$reader_app_dir/Contents/Resources/AppIcon.icns"
/bin/cp "$reader_project_dir/../操作指导/使用说明.md" "$reader_app_dir/Contents/Resources/使用说明.md"
/bin/cp "$reader_project_dir/Samples/示例.md" "$reader_app_dir/Contents/Resources/示例.md"
/usr/bin/plutil -lint "$reader_app_dir/Contents/Info.plist"
/usr/bin/codesign --force --sign - "$reader_app_dir"
/usr/bin/codesign --verify --deep --strict "$reader_app_dir"

echo "构建完成：$reader_app_dir"
/usr/bin/du -sh "$reader_app_dir"
