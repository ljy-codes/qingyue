#!/bin/bash
set -euo pipefail
reader_test_root="$(cd "$(dirname "$0")/.." && pwd)"
reader_test_build="$reader_test_root/build/tests"
mkdir -p "$reader_test_build/ModuleCache"
xcrun swiftc -module-cache-path "$reader_test_build/ModuleCache" "$reader_test_root/Sources/TextFile.swift" "$reader_test_root/Tests/FileCoreTests.swift" -o "$reader_test_build/FileCoreTests"
"$reader_test_build/FileCoreTests"
xcrun swiftc -module-cache-path "$reader_test_build/ModuleCache" "$reader_test_root/Sources/TextFeatures.swift" "$reader_test_root/Tests/TextFeatureTests.swift" -o "$reader_test_build/TextFeatureTests"
"$reader_test_build/TextFeatureTests"
xcrun swiftc -module-cache-path "$reader_test_build/ModuleCache" "$reader_test_root/Sources/TextFeatures.swift" "$reader_test_root/Sources/ReadingDocument.swift" "$reader_test_root/Tests/ReadingDocumentTests.swift" -o "$reader_test_build/ReadingDocumentTests"
"$reader_test_build/ReadingDocumentTests"
xcrun swiftc -module-cache-path "$reader_test_build/ModuleCache" "$reader_test_root/Sources/TextFeatures.swift" "$reader_test_root/Sources/TextEditing.swift" "$reader_test_root/Sources/Appearance.swift" "$reader_test_root/Tests/LineIndexTests.swift" -o "$reader_test_build/LineIndexTests"
"$reader_test_build/LineIndexTests"
xcrun swiftc -module-cache-path "$reader_test_build/ModuleCache" "$reader_test_root/Sources/TextSearch.swift" "$reader_test_root/Tests/TextSearchTests.swift" -o "$reader_test_build/TextSearchTests"
"$reader_test_build/TextSearchTests"
xcrun swiftc -module-cache-path "$reader_test_build/ModuleCache" "$reader_test_root/Sources/TextEditing.swift" "$reader_test_root/Tests/TextEditingTests.swift" -o "$reader_test_build/TextEditingTests"
"$reader_test_build/TextEditingTests"
xcrun swiftc -module-cache-path "$reader_test_build/ModuleCache" "$reader_test_root/Sources/TextFile.swift" "$reader_test_root/Sources/WorkspaceStore.swift" "$reader_test_root/Tests/WorkspaceStoreTests.swift" -o "$reader_test_build/WorkspaceStoreTests"
"$reader_test_build/WorkspaceStoreTests"
xcrun swiftc -module-cache-path "$reader_test_build/ModuleCache" "$reader_test_root/Sources/QuickOpenController.swift" "$reader_test_root/Tests/QuickOpenTests.swift" -o "$reader_test_build/QuickOpenTests"
"$reader_test_build/QuickOpenTests"
reader_ui_sources=()
for reader_source in "$reader_test_root"/Sources/*.swift; do
    if [ "$(basename "$reader_source")" != "main.swift" ]; then reader_ui_sources+=("$reader_source"); fi
done
xcrun swiftc -module-cache-path "$reader_test_build/ModuleCache" "${reader_ui_sources[@]}" "$reader_test_root/Tests/UIWorkflowTests.swift" -o "$reader_test_build/UIWorkflowTests"
"$reader_test_build/UIWorkflowTests"
xcrun swiftc -module-cache-path "$reader_test_build/ModuleCache" "$reader_test_root/Sources/WindowMenuBar.swift" "$reader_test_root/Tests/WindowMenuBarTests.swift" -o "$reader_test_build/WindowMenuBarTests"
"$reader_test_build/WindowMenuBarTests"
xcrun swiftc -module-cache-path "$reader_test_build/ModuleCache" "${reader_ui_sources[@]}" "$reader_test_root/Tests/ReaderMenusTests.swift" -o "$reader_test_build/ReaderMenusTests"
"$reader_test_build/ReaderMenusTests"
