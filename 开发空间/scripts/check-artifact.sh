#!/bin/bash
set -euo pipefail

export LC_ALL=C LANG=C LC_CTYPE=C

reader_script_dir="$(cd "$(dirname "$0")" && pwd)"
reader_project_dir="$(cd "$reader_script_dir/.." && pwd)"
reader_product_root="$(cd "$reader_project_dir/.." && pwd)/交付产品"
reader_info_plist="$reader_project_dir/Resources/Info.plist"
reader_expected_arch="${QINGYUE_EXPECTED_ARCH:-universal}"
reader_version="$(/usr/bin/plutil -extract CFBundleShortVersionString raw "$reader_info_plist")"
reader_build_number="$(/usr/bin/plutil -extract CFBundleVersion raw "$reader_info_plist")"
reader_product_dir="$reader_product_root/轻阅-macOS-$reader_version"
reader_app_dir="$reader_product_dir/轻阅.app"
reader_zip="$reader_product_root/轻阅-macOS-$reader_version-交付.zip"

fail() {
    echo "产物检查失败：$*" >&2
    exit 1
}

for reader_command in /usr/bin/plutil /usr/bin/codesign /usr/bin/shasum /usr/bin/ditto /usr/bin/lipo /usr/bin/sips; do
    [ -x "$reader_command" ] || fail "找不到工具 $reader_command"
done

case "$reader_expected_arch" in
    x86_64|arm64|universal) ;;
    *) fail "QINGYUE_EXPECTED_ARCH 必须是 x86_64、arm64 或 universal，当前为：$reader_expected_arch" ;;
esac

[ -d "$reader_product_dir" ] || fail "找不到交付目录：$reader_product_dir"
[ -d "$reader_app_dir" ] || fail "找不到应用包：$reader_app_dir"
[ -f "$reader_zip" ] || fail "找不到交付 ZIP：$reader_zip"
[ -f "$reader_product_dir/SHA256SUMS.txt" ] || fail "找不到 SHA256SUMS.txt"

/usr/bin/plutil -lint "$reader_info_plist" >/dev/null || fail "源 Info.plist 无法解析"
/usr/bin/plutil -lint "$reader_app_dir/Contents/Info.plist" >/dev/null || fail "应用 Info.plist 无法解析"

reader_app_version="$(/usr/bin/plutil -extract CFBundleShortVersionString raw "$reader_app_dir/Contents/Info.plist")"
reader_app_build_number="$(/usr/bin/plutil -extract CFBundleVersion raw "$reader_app_dir/Contents/Info.plist")"
reader_executable="$(/usr/bin/plutil -extract CFBundleExecutable raw "$reader_app_dir/Contents/Info.plist")"
[ "$reader_app_version" = "$reader_version" ] || fail "应用版本不一致：$reader_app_version != $reader_version"
[ "$reader_app_build_number" = "$reader_build_number" ] || fail "应用构建号不一致：$reader_app_build_number != $reader_build_number"
[ -n "$reader_executable" ] || fail "应用未声明 CFBundleExecutable"

reader_binary="$reader_app_dir/Contents/MacOS/$reader_executable"
[ -x "$reader_binary" ] || fail "找不到可执行文件：$reader_binary"

for reader_icon in sky sakura mint peach; do
    reader_icon_path="$reader_app_dir/Contents/Resources/IconThemes/Qingyue-$reader_icon@2x.png"
    [ -f "$reader_icon_path" ] || fail "缺少 2x 图标资源：$reader_icon_path"
    reader_icon_width="$(/usr/bin/sips -g pixelWidth "$reader_icon_path" 2>/dev/null | awk '/pixelWidth/ { print $2 }')"
    reader_icon_height="$(/usr/bin/sips -g pixelHeight "$reader_icon_path" 2>/dev/null | awk '/pixelHeight/ { print $2 }')"
    [ "$reader_icon_width" = "1024" ] && [ "$reader_icon_height" = "1024" ] || fail "图标尺寸不是 1024×1024：$reader_icon_path (${reader_icon_width}×${reader_icon_height})"
done

reader_arches="$(/usr/bin/lipo -archs "$reader_binary")"
case "$reader_expected_arch" in
    x86_64|arm64)
        case " $reader_arches " in
            *" $reader_expected_arch "*) ;;
            *) fail "可执行文件不含 $reader_expected_arch 架构：$reader_arches" ;;
        esac
        ;;
    universal)
        if [[ "$reader_arches" != *x86_64* || "$reader_arches" != *arm64* ]]; then
            fail "可执行文件不是 universal（需要 x86_64 + arm64）：$reader_arches"
        fi
        ;;
esac

/usr/bin/codesign --verify --deep --strict "$reader_app_dir" || fail "应用签名校验失败"
(cd "$reader_product_dir" && /usr/bin/shasum -a 256 -c SHA256SUMS.txt) || fail "SHA256 校验失败"

reader_unpack_dir="$(mktemp -d "${TMPDIR:-/tmp}/qingyue-artifact.XXXXXX")"
trap 'rm -rf "$reader_unpack_dir"' EXIT
/usr/bin/ditto -x -k "$reader_zip" "$reader_unpack_dir" || fail "ZIP 无法解包"
reader_unpacked_app="$reader_unpack_dir/轻阅-macOS-$reader_version/轻阅.app"
[ -d "$reader_unpacked_app" ] || fail "ZIP 中缺少应用包"
[ -f "$reader_unpacked_app/Contents/Info.plist" ] || fail "ZIP 中缺少应用 Info.plist"
[ -f "$reader_unpacked_app/Contents/MacOS/$reader_executable" ] || fail "ZIP 中缺少应用可执行文件"

reader_zip_version="$(/usr/bin/plutil -extract CFBundleShortVersionString raw "$reader_unpacked_app/Contents/Info.plist")"
reader_zip_build_number="$(/usr/bin/plutil -extract CFBundleVersion raw "$reader_unpacked_app/Contents/Info.plist")"
[ "$reader_zip_version" = "$reader_version" ] || fail "ZIP 中版本不一致：$reader_zip_version != $reader_version"
[ "$reader_zip_build_number" = "$reader_build_number" ] || fail "ZIP 中构建号不一致：$reader_zip_build_number != $reader_build_number"

echo "产物检查通过：轻阅 $reader_version（Mac 构建 $reader_build_number）"
echo "架构：$reader_arches"
echo "期望架构：$reader_expected_arch"
echo "目录：$reader_product_dir"
echo "ZIP：$reader_zip"
