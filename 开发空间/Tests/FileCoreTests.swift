import Foundation
import Darwin

@main
struct FileCoreTests {
    static var passed = 0

    static func main() throws {
        let fm = FileManager.default
        let directory = fm.temporaryDirectory.appendingPathComponent("lightreader-file-tests-\(UUID().uuidString)")
        try fm.createDirectory(at: directory, withIntermediateDirectories: true)
        defer { try? fm.removeItem(at: directory) }

        try test("New files can be saved repeatedly through /tmp and /private/tmp") {
            for parent in [URL(fileURLWithPath: "/tmp"), URL(fileURLWithPath: "/private/tmp"), directory] {
                let url = parent.appendingPathComponent("lightreader-new-\(UUID().uuidString).txt")
                defer { try? fm.removeItem(at: url) }
                let file = TextFile(text: "新建文件\n")
                try file.save(to: url)
                try expect(!file.diskChanged(), "fresh save should not be an external change: \(url.path)")
                file.text += "第二次保存\n"
                try file.save()
                try expect(!file.diskChanged(), "second save remains clean: \(url.path)")
                try expect(String(contentsOf: url) == file.text, "second save writes content")
            }
        }

        try test("UTF-8 BOM / UTF-16 BOM / GB18030 roundtrip") {
            for codec in TextCodec.allCases {
                let url = directory.appendingPathComponent(codec.rawValue + ".txt")
                let raw = "你好，世界 😀\r\n配置 = 真\r\n"
                let data = try codec.encode(raw)
                try data.write(to: url)
                let file = try TextFile.open(url)
                try expect(file.codec == codec, "detect \(codec.label)")
                try expect(file.text == "你好，世界 😀\n配置 = 真\n", "normalize content")
                try expect(file.lineEnding == .crlf, "detect CRLF")
                try expect(!file.isDirty, "opened document is clean")
                try file.save()
                try expect(Data(contentsOf: url) == data, "unchanged roundtrip bytes")
                file.text += "新增一行\n"
                try file.save()
                try expect(codec.decode(Data(contentsOf: url)) == raw + "新增一行\r\n", "edit retains encoding / newline")
                try expect(!file.isDirty, "successful save resets baseline")
            }
        }

        try test("UTF-16 without BOM remains without BOM") {
            for codec in [TextCodec.utf16LE, .utf16BE] {
                let url = directory.appendingPathComponent("no-bom-\(codec.rawValue).txt")
                try codec.encode("甲\n乙", includeBOM: false).write(to: url)
                let file = try TextFile.open(url, codec: codec)
                file.text += "丙"
                try file.save()
                let expected = try codec.encode("甲\n乙丙", includeBOM: false)
                try expect(Data(contentsOf: url) == expected, "preserve missing BOM")
            }
        }

        try test("Strict decoding rejects invalid UTF-8 and unpaired UTF-16") {
            try expectError({ _ = try TextCodec.utf8.decode(Data([0xC0, 0xAF])) }, matching: .decoding)
            try expectError({ _ = try TextCodec.utf16LE.decode(Data([0x00, 0xD8])) }, matching: .decoding)
        }

        try test("Unicode normalization edits are not mistaken for unchanged text") {
            let url = directory.appendingPathComponent("normalization.txt")
            try Data("\u{00E9}".utf8).write(to: url)
            let file = try TextFile.open(url)
            file.text = "e\u{0301}"
            try expect(file.isDirty, "detect exact code point changes")
            try file.save()
            try expect(Data(contentsOf: url) == Data("e\u{0301}".utf8), "write exact user sequence")
        }

        try test("Save As does not overwrite an existing target without explicit approval") {
            let url = directory.appendingPathComponent("existing-target.txt")
            try Data("keep".utf8).write(to: url)
            let file = TextFile(text: "new")
            try expectError({ try file.save(to: url) }, matching: .externalChange)
            try expect(String(contentsOf: url) == "keep", "target remains intact")
            try file.save(to: url, allowOverwrite: true)
            try expect(String(contentsOf: url) == "new", "approved Save As replaces target")
        }

        try test("A proposed URL on a new document never grants overwrite permission") {
            let url = directory.appendingPathComponent("proposed-existing.txt")
            try Data("existing content".utf8).write(to: url)
            let file = TextFile(text: "new content", url: url)
            try expectError({ try file.save() }, matching: .externalChange)
            try expect(String(contentsOf: url) == "existing content", "initial URL does not own disk file")
            try expect(file.isDirty && file.text == "new content", "refused save retains new content")
        }

        try test("Untitled renaming only suggests a name and survives new and legacy recovery") {
            let file = TextFile(text: "尚未保存的正文 😀")
            let id = file.id
            try file.rename(toName: "我的配置.json")
            try expect(file.title == "我的配置.json" && file.suggestedName == "我的配置.json", "untitled tab shows its chosen name")
            try expect(file.url == nil && file.isDirty && file.id == id, "naming does not create a saved document or change identity")
            let snapshot = file.recoverySnapshot()
            let restored = try TextFile.restore(snapshot: JSONDecoder().decode(TextFile.RecoverySnapshot.self, from: JSONEncoder().encode(snapshot)))
            try expect(restored.title == file.title && restored.text == file.text && restored.url == nil, "per-document snapshot preserves suggested name")
            let legacy = directory.appendingPathComponent("named-recovery/state.json")
            try TextFile.writeRecovery([file], to: legacy)
            try expect(TextFile.recovery(from: legacy)[0].title == file.title, "legacy envelope also preserves suggested name")

            // Snapshots produced before naming existed must still decode.
            var oldObject = try JSONSerialization.jsonObject(with: JSONEncoder().encode(snapshot)) as! [String: Any]
            oldObject.removeValue(forKey: "suggestedName")
            let oldSnapshot = try JSONDecoder().decode(TextFile.RecoverySnapshot.self, from: JSONSerialization.data(withJSONObject: oldObject))
            try expect(TextFile.restore(snapshot: oldSnapshot).title == "未命名", "missing optional name remains backward compatible")
        }

        try test("Saved renaming preserves dirty text, identity, encoding and disk baseline") {
            let original = directory.appendingPathComponent("rename-original.txt")
            let destination = directory.appendingPathComponent("重命名后的配置.json")
            let bytes = try TextCodec.utf8BOM.encode("磁盘旧内容\r\n")
            try bytes.write(to: original)
            try fm.setAttributes([.posixPermissions: 0o640], ofItemAtPath: original.path)
            let file = try TextFile.open(original), id = file.id
            file.text = "尚未保存的新内容\n"
            try file.rename(toName: destination.lastPathComponent)
            try expect(!fm.fileExists(atPath: original.path) && fm.fileExists(atPath: destination.path), "directory entry is renamed")
            try expect(Data(contentsOf: destination) == bytes, "renaming never saves pending text")
            try expect(file.id == id && file.url == destination && file.title == destination.lastPathComponent, "identity retained and path updated")
            try expect(file.text == "尚未保存的新内容\n" && file.isDirty && !file.diskChanged(), "unsaved text and baseline remain coherent")
            try expect(file.codec == .utf8BOM && file.lineEnding == .crlf, "format state is unchanged")
            try expect((fm.attributesOfItem(atPath: destination.path)[.posixPermissions] as? NSNumber)?.intValue == 0o640, "rename preserves mode")
            let restored = try TextFile.restore(snapshot: file.recoverySnapshot())
            try expect(restored.url == destination && restored.isDirty && !restored.diskChanged(), "renamed draft restores its updated path and baseline")
            try file.save()
            try expect(Data(contentsOf: destination) == TextCodec.utf8BOM.encode("尚未保存的新内容\r\n"), "later explicit save uses the new path and old encoding")
            try expect(!fm.fileExists(atPath: original.path) && !file.isDirty, "save never recreates the old path")
        }

        try test("Renaming refuses existing files, directories and dangling symbolic links") {
            let source = directory.appendingPathComponent("rename-collision-source.txt")
            let target = directory.appendingPathComponent("rename-collision-target.txt")
            let folder = directory.appendingPathComponent("rename-collision-folder")
            let dangling = directory.appendingPathComponent("rename-collision-link.txt")
            try Data("source".utf8).write(to: source)
            try Data("do not replace".utf8).write(to: target)
            try fm.createDirectory(at: folder, withIntermediateDirectories: false)
            try fm.createSymbolicLink(at: dangling, withDestinationURL: directory.appendingPathComponent("not-created-target"))
            let file = try TextFile.open(source)
            file.text = "unsaved"
            for name in [target.lastPathComponent, folder.lastPathComponent, dangling.lastPathComponent] {
                try expectIO({ try file.rename(toName: name) }, contains: "已存在")
            }
            try expect(String(contentsOf: source) == "source" && String(contentsOf: target) == "do not replace", "collisions leave both files intact")
            try expect(file.url == source && file.text == "unsaved" && file.isDirty, "collision preserves model and pending edits")
            try expect(try fm.destinationOfSymbolicLink(atPath: dangling.path).hasSuffix("not-created-target"), "dangling link is not followed or overwritten")
        }

        try test("External changes and proposed locations cannot authorize a rename") {
            let source = directory.appendingPathComponent("rename-external.txt")
            let target = directory.appendingPathComponent("rename-external-new.txt")
            try Data("loaded".utf8).write(to: source)
            let file = try TextFile.open(source)
            file.text = "pending"
            try Data("changed elsewhere".utf8).write(to: source)
            try expectError({ try file.rename(toName: target.lastPathComponent) }, matching: .externalChange)
            try expect(String(contentsOf: source) == "changed elsewhere" && !fm.fileExists(atPath: target.path), "changed disk entry is not moved")
            try expect(file.url == source && file.text == "pending" && file.isDirty, "refused external rename keeps edits")
            let proposed = TextFile(text: "new", url: source)
            try expectIO({ try proposed.rename(toName: target.lastPathComponent) }, contains: "尚未保存")
            try expect(String(contentsOf: source) == "changed elsewhere", "new document cannot rename an unowned path")
        }

        try test("Symbolic-link renaming moves only the link and preserves target writes") {
            let target = directory.appendingPathComponent("rename-link-target.conf")
            let link = directory.appendingPathComponent("rename-link.conf")
            let renamed = directory.appendingPathComponent("rename-link-new.conf")
            try Data("key=before\n".utf8).write(to: target)
            try fm.createSymbolicLink(atPath: link.path, withDestinationPath: target.lastPathComponent)
            let file = try TextFile.open(link)
            file.text = "key=after\n"
            try file.rename(toName: renamed.lastPathComponent)
            try expect(!fm.fileExists(atPath: link.path), "old link name is removed")
            try expect(try fm.destinationOfSymbolicLink(atPath: renamed.path) == target.lastPathComponent, "new link retains exact relative target")
            try expect(String(contentsOf: target) == "key=before\n" && !file.diskChanged() && file.isDirty, "rename does not save or move the target")
            try file.save()
            try expect(String(contentsOf: target) == "key=after\n", "later save still writes through renamed link")
            try expect(try fm.destinationOfSymbolicLink(atPath: renamed.path) == target.lastPathComponent, "saving retains the renamed link")
        }

        try test("Case-only renaming is safe and never treats a real hard link as an alias") {
            let source = directory.appendingPathComponent("CaseRename.txt")
            let destination = directory.appendingPathComponent("caserename.TXT")
            try Data("case bytes".utf8).write(to: source)
            let file = try TextFile.open(source)
            try file.rename(toName: destination.lastPathComponent)
            try expect(file.url == destination && !file.isDirty && !file.diskChanged(), "case-only rename establishes a coherent clean path")
            try expect(Data(contentsOf: destination) == Data("case bytes".utf8), "case-only rename retains data")
            let actualNames = try fm.contentsOfDirectory(atPath: directory.path)
            try expect(actualNames.contains { $0.utf8.elementsEqual(destination.lastPathComponent.utf8) }, "directory records the requested capitalization")
            try expect(!actualNames.contains { $0.utf8.elementsEqual(source.lastPathComponent.utf8) }, "old spelling is no longer a directory entry")

            let other = directory.appendingPathComponent("rename-hardlink-other.txt")
            try fm.linkItem(at: destination, to: other)
            try expectIO({ try file.rename(toName: other.lastPathComponent) }, contains: "已存在")
            let renamed = directory.appendingPathComponent("rename-hardlink-allowed.txt")
            try file.rename(toName: renamed.lastPathComponent)
            try expect(String(contentsOf: renamed) == "case bytes" && String(contentsOf: other) == "case bytes", "moving one hard-link name retains both byte streams")
            let ino1 = try fm.attributesOfItem(atPath: renamed.path)[.systemFileNumber] as? NSNumber
            let ino2 = try fm.attributesOfItem(atPath: other.path)[.systemFileNumber] as? NSNumber
            try expect(ino1 == ino2, "directory-entry rename does not split hard links")
        }

        try test("Invalid rename suggestions never change text, paths or titles") {
            let file = TextFile(text: "keep")
            for bad in ["", "   ", ".", "..", "folder/file.txt", "bad\0name", "bad\nname", "bad\tname"] {
                try expectIO({ try file.rename(toName: bad) }, contains: "文件名")
                try expect(file.title == "未命名" && file.url == nil && file.text == "keep", "invalid name leaves untitled document unchanged")
            }
            let source = directory.appendingPathComponent("rename-no-op.txt")
            try Data("same".utf8).write(to: source)
            let saved = try TextFile.open(source)
            try saved.rename(toName: source.lastPathComponent)
            try expect(saved.url == source && !saved.isDirty && !saved.diskChanged(), "identical name is a safe no-op")
        }

        try test("Mixed newlines are preserved and require explicit conversion after editing") {
            let url = directory.appendingPathComponent("mixed.txt")
            let data = Data("甲\r\n乙\n丙\r丁".utf8)
            try data.write(to: url)
            let file = try TextFile.open(url)
            try expect(file.lineEnding == .mixed, "mixed detection")
            try file.save()
            try expect(Data(contentsOf: url) == data, "preserve exact mixed bytes")
            file.text += "新"
            try expectError({ try file.save() }, matching: .mixedLineEndings)
            try expect(Data(contentsOf: url) == data, "failed save leaves file intact")
            file.lineEnding = .crlf
            try file.save()
            try expect(String(contentsOf: url, encoding: .utf8) == "甲\r\n乙\r\n丙\r\n丁新", "explicit conversion")
        }

        try test("Recovered clean mixed text cannot silently normalize on overwrite or Save As") {
            let url = directory.appendingPathComponent("recovered-mixed.txt")
            let copy = directory.appendingPathComponent("recovered-mixed-copy.txt")
            let recovery = directory.appendingPathComponent("mixed-recovery/state.json")
            let original = Data("a\r\nb\nc\r".utf8)
            try original.write(to: url)
            let file = try TextFile.open(url)
            let baseline = file.text
            file.text += "草稿修改"
            try TextFile.writeRecovery([file], to: recovery)
            let restored = try TextFile.recovery(from: recovery)[0]
            restored.text = baseline
            try expect(!restored.isDirty && restored.originalData == nil, "recovered text can return to a clean baseline without raw bytes")
            try restored.save()
            try expect(Data(contentsOf: url) == original, "ordinary unchanged save is still a byte-preserving no-op")
            try expectError({ try restored.save(to: copy) }, matching: .mixedLineEndings)
            try expect(!fm.fileExists(atPath: copy.path), "refused Save As creates no converted file")

            let external = Data("external content\r\n".utf8)
            try external.write(to: url)
            try expectError({ try restored.save() }, matching: .externalChange)
            try expectError({ try restored.save(allowOverwrite: true) }, matching: .mixedLineEndings)
            try expect(Data(contentsOf: url) == external, "overwrite approval does not authorize newline conversion")
            try expect(restored.text == baseline && restored.lineEnding == .mixed, "refused write retains recovered text and format state")
            restored.lineEnding = .crlf
            try restored.save(allowOverwrite: true)
            try expect(Data(contentsOf: url) == Data("a\r\nb\r\nc\r\n".utf8), "explicit newline choice permits the overwrite")
            try expect(!restored.isDirty, "explicit converted save establishes a new baseline")
        }

        try test("Large uncached mixed snapshots require explicit conversion for every real write") {
            let url = directory.appendingPathComponent("large-mixed.txt")
            let copy = directory.appendingPathComponent("large-mixed-copy.txt")
            let original = Data((String(repeating: "x", count: 4 * 1024 * 1024 + 16) + "\r\n甲\n乙\r").utf8)
            try original.write(to: url)
            let file = try TextFile.open(url)
            try expect(file.originalData == nil && file.lineEnding == .mixed && !file.isDirty, "large mixed input deliberately has no raw cache")
            try file.save()
            try expect(Data(contentsOf: url) == original, "large clean no-op retains exact mixed bytes")
            try expectError({ try file.save(to: copy) }, matching: .mixedLineEndings)
            try expect(!fm.fileExists(atPath: copy.path), "large Save As does not silently normalize")
            let external = Data("other process".utf8)
            try external.write(to: url)
            try expectError({ try file.save(allowOverwrite: true) }, matching: .mixedLineEndings)
            try expect(Data(contentsOf: url) == external, "large clean overwrite refuses format loss")
            file.lineEnding = .lf
            try file.save(allowOverwrite: true)
            try expect(Data(contentsOf: url) == Data(file.text.utf8), "an explicit LF choice writes the normalized snapshot")
        }

        try test("Untouched cached mixed snapshots retain exact bytes when copied or explicitly overwritten") {
            for codec in TextCodec.allCases {
                let url = directory.appendingPathComponent("cached-mixed-\(codec.rawValue).txt")
                let copy = directory.appendingPathComponent("cached-mixed-copy-\(codec.rawValue).txt")
                let original = try codec.encode("甲\r\n乙\n丙\r")
                try original.write(to: url)
                let file = try TextFile.open(url)
                try file.save(to: copy)
                try expect(Data(contentsOf: copy) == original, "cached Save As preserves mixed bytes and \(codec.label)")
                try Data("external".utf8).write(to: copy)
                try file.save(allowOverwrite: true)
                try expect(Data(contentsOf: copy) == original, "cached overwrite restores exact mixed bytes and \(codec.label)")
                try expect(file.lineEnding == .mixed && !file.isDirty, "byte copy keeps clean mixed state")
            }
        }

        try test("Cached raw bytes are a snapshot even when another process writes in place") {
            let url = directory.appendingPathComponent("snapshot.txt")
            let original = Data((String(repeating: "A", count: 3 * 1024 * 1024) + "\r\n\n").utf8)
            try original.write(to: url)
            let file = try TextFile.open(url)
            let handle = try FileHandle(forWritingTo: url)
            try handle.write(contentsOf: Data("B".utf8)); try handle.close()
            try expect(file.originalData == original, "external in-place writes do not mutate loaded raw snapshot")
            try expect(file.diskChanged(), "external write is detected")
        }

        try test("External modification blocks overwrite until explicitly allowed") {
            let url = directory.appendingPathComponent("conflict.json")
            try Data("{\"a\":1}".utf8).write(to: url)
            let file = try TextFile.open(url)
            file.text = "{\"a\":2}"
            try Data("{\"a\":3}".utf8).write(to: url)
            try expect(file.diskChanged(), "detect external modification")
            try expectError({ try file.save() }, matching: .externalChange)
            try expect(String(contentsOf: url) == "{\"a\":3}", "do not overwrite external bytes")
            try expect(file.isDirty, "failed save retains edits")
            try file.save(allowOverwrite: true)
            try expect(String(contentsOf: url) == "{\"a\":2}", "explicit overwrite")
        }

        try test("Explicit overwrite of a clean snapshot really writes its content") {
            let url = directory.appendingPathComponent("clean-overwrite.txt")
            try Data("loaded snapshot".utf8).write(to: url)
            let file = try TextFile.open(url)
            try Data("external content".utf8).write(to: url)
            try expect(!file.isDirty && (try file.diskChanged()), "clean editor can still have a disk conflict")
            try file.save(allowOverwrite: true)
            try expect(String(contentsOf: url) == "loaded snapshot", "explicit overwrite was not skipped")
            try expect(!file.diskChanged(), "overwrite updates disk fingerprint")
            try fm.removeItem(at: url)
            try file.save(allowOverwrite: true)
            try expect(String(contentsOf: url) == "loaded snapshot", "explicit overwrite recreates deleted snapshot")
        }

        try test("Deleted file is an external conflict") {
            let url = directory.appendingPathComponent("deleted.txt")
            try Data("old".utf8).write(to: url)
            let file = try TextFile.open(url)
            try fm.removeItem(at: url)
            file.text = "new"
            try expectError({ try file.save() }, matching: .externalChange)
            try expect(!fm.fileExists(atPath: url.path), "not silently recreated")
        }

        try test("Symbolic link remains a link and target permissions survive") {
            let target = directory.appendingPathComponent("real.conf")
            let link = directory.appendingPathComponent("alias.conf")
            try Data("key=old\n".utf8).write(to: target)
            try fm.setAttributes([.posixPermissions: 0o640], ofItemAtPath: target.path)
            try fm.createSymbolicLink(at: link, withDestinationURL: target)
            let file = try TextFile.open(link)
            file.text = "key=new\n"
            try file.save()
            try expect(try fm.destinationOfSymbolicLink(atPath: link.path) == target.path, "retain symbolic link")
            try expect(String(contentsOf: target) == "key=new\n", "write resolved target")
            let mode = try fm.attributesOfItem(atPath: target.path)[.posixPermissions] as? NSNumber
            try expect(mode?.intValue == 0o640, "preserve mode")
        }

        try test("Retargeted symbolic link is an external conflict") {
            let original = directory.appendingPathComponent("link-original.txt")
            let replacement = directory.appendingPathComponent("link-other.txt")
            let link = directory.appendingPathComponent("changing-link.txt")
            try Data("same".utf8).write(to: original)
            try Data("same".utf8).write(to: replacement)
            try fm.createSymbolicLink(at: link, withDestinationURL: original)
            let file = try TextFile.open(link)
            file.text = "edit"
            try fm.removeItem(at: link)
            try fm.createSymbolicLink(at: link, withDestinationURL: replacement)
            try expectError({ try file.save() }, matching: .externalChange)
            try expect(String(contentsOf: replacement) == "same", "replacement target untouched")
        }

        try test("Atomic saving does not silently detach hard links") {
            let url = directory.appendingPathComponent("hardlink-original.txt")
            let other = directory.appendingPathComponent("hardlink-other.txt")
            let copy = directory.appendingPathComponent("hardlink-save-as.txt")
            try Data("linked data".utf8).write(to: url)
            try fm.linkItem(at: url, to: other)
            let file = try TextFile.open(url)
            file.text = "edited data"
            do {
                try file.save()
                throw NSError(domain: "FileCoreTests", code: 3, userInfo: [NSLocalizedDescriptionKey: "Expected a hard-link preservation error"])
            } catch TextFileError.io(let message) {
                try expect(message.contains("硬链接"), "actionable hard-link error")
            }
            try expect(String(contentsOf: url) == "linked data" && String(contentsOf: other) == "linked data", "both links remain intact")
            let inode = try fm.attributesOfItem(atPath: url.path)[.systemFileNumber] as? NSNumber
            let otherInode = try fm.attributesOfItem(atPath: other.path)[.systemFileNumber] as? NSNumber
            try expect(inode == otherInode, "original hard-link identity is retained")
            try file.save(to: copy)
            try expect(String(contentsOf: copy) == "edited data", "Save As can create an independent copy")
        }

        try test("Atomic replacement preserves macOS ACLs and extended attributes") {
            let url = directory.appendingPathComponent("acl-and-xattr.txt")
            try Data("before".utf8).write(to: url)
            let attr = Data("qingyue regression fixture".utf8)
            let rc = attr.withUnsafeBytes { setxattr(url.path, "com.qingyue.fixture", $0.baseAddress, attr.count, 0, 0) }
            try expect(rc == 0, "set fixture xattr")
            let process = Process()
            process.executableURL = URL(fileURLWithPath: "/bin/chmod")
            process.arguments = ["+a", "everyone allow read", url.path]
            try process.run(); process.waitUntilExit()
            try expect(process.terminationStatus == 0, "set fixture ACL")
            let originalACL = try aclText(url)
            let file = try TextFile.open(url)
            file.text = "after"
            try file.save()
            try expect(aclText(url) == originalACL, "ACL entries survive atomic replacement")
            var bytes = [UInt8](repeating: 0, count: 128)
            let size = getxattr(url.path, "com.qingyue.fixture", &bytes, bytes.count, 0, 0)
            try expect(size == attr.count && Data(bytes.prefix(max(0, size))) == attr, "extended attribute bytes survive")
        }

        try test("Read-only file fails without losing edits") {
            let url = directory.appendingPathComponent("readonly.txt")
            try Data("original".utf8).write(to: url)
            try fm.setAttributes([.posixPermissions: 0o444], ofItemAtPath: url.path)
            defer { try? fm.setAttributes([.posixPermissions: 0o600], ofItemAtPath: url.path) }
            let file = try TextFile.open(url)
            file.text = "edit"
            try expectError({ try file.save() }, matching: .readOnly)
            try expect(file.text == "edit" && file.isDirty, "retain document state")
            try expect(String(contentsOf: url) == "original", "original untouched")
        }

        try test("Recovery keeps saved baseline, conflict detection and unsaved content") {
            let url = directory.appendingPathComponent("recover.txt")
            try Data("原文\r\n".utf8).write(to: url)
            let saved = try TextFile.open(url)
            saved.text += "编辑\n"
            let unsaved = TextFile(text: "新草稿 😀")
            let recovery = directory.appendingPathComponent("private/recovery.json")
            try TextFile.writeRecovery([saved, unsaved], to: recovery)
            let restored = try TextFile.recovery(from: recovery)
            try expect(restored.count == 2, "recover tabs")
            try expect(restored[0].id == saved.id, "preserve stable identity")
            try expect(restored[0].text == saved.text && restored[0].isDirty, "recover edits and dirty state")
            try expect(restored[0].lineEnding == .crlf, "recover newline state")
            try expect(!restored[0].diskChanged(), "baseline fingerprint retained")
            try expect(restored[1].url == nil && restored[1].isDirty && restored[1].text == unsaved.text, "recover new file")
            try Data("external".utf8).write(to: url)
            try expectError({ try restored[0].save() }, matching: .externalChange)
            try expect(String(contentsOf: url) == "external", "recovery never writes original on load")
            let attrs = try fm.attributesOfItem(atPath: recovery.path)
            try expect((attrs[.posixPermissions] as? NSNumber)?.intValue == 0o600, "private recovery file")
        }

        try test("Clean recovery stays clean and first save requires destination") {
            let url = directory.appendingPathComponent("clean.txt")
            try Data("clean".utf8).write(to: url)
            let file = try TextFile.open(url)
            let recovery = directory.appendingPathComponent("clean-recovery/state.json")
            try TextFile.writeRecovery([file], to: recovery)
            let restored = try TextFile.recovery(from: recovery)
            try expect(!restored[0].isDirty, "clean tab retained")
            try expectError({ try TextFile().save() }, matching: .missingDestination)
        }

        try test("Recovery never blesses a new symlink at the old physical path") {
            let url = directory.appendingPathComponent("recovery-original.txt")
            let other = directory.appendingPathComponent("recovery-redirected.txt")
            let state = directory.appendingPathComponent("redirect-recovery/state.json")
            try Data("same content".utf8).write(to: url)
            try Data("same content".utf8).write(to: other)
            let file = try TextFile.open(url)
            file.text = "unsaved edit"
            try TextFile.writeRecovery([file], to: state)
            try fm.removeItem(at: url)
            try fm.createSymbolicLink(at: url, withDestinationURL: other)
            let restored = try TextFile.recovery(from: state)[0]
            try expect(restored.diskChanged(), "recorded physical path remains a historical baseline")
            try expectError({ try restored.save() }, matching: .externalChange)
            try expect(String(contentsOf: other) == "same content", "redirected file is not overwritten")
        }

        try test("Recovery writer never replaces a valid snapshot with unreadable state") {
            let state = directory.appendingPathComponent("limits-recovery/state.json")
            let file = TextFile(text: "recover me")
            try TextFile.writeRecovery([file], to: state)
            let before = try Data(contentsOf: state)
            do {
                try TextFile.writeRecovery((0...200).map { _ in TextFile(text: "tab") }, to: state)
                throw NSError(domain: "FileCoreTests", code: 4, userInfo: [NSLocalizedDescriptionKey: "Expected writer tab limit"])
            } catch TextFileError.io { }
            try expect(Data(contentsOf: state) == before, "refused write retains valid recovery bytes")
            let oversize = TextFile(text: String(repeating: "x", count: TextFile.maximumSize + 1))
            do {
                try TextFile.writeRecovery([oversize], to: state)
                throw NSError(domain: "FileCoreTests", code: 5, userInfo: [NSLocalizedDescriptionKey: "Expected writer size limit"])
            } catch TextFileError.io { }
            try expect(Data(contentsOf: state) == before, "oversize text does not replace valid recovery bytes")
            try expect(TextFile.recovery(from: state)[0].text == "recover me", "old recovery still loads")
        }

        try test("Oversize and binary input are rejected before editing") {
            let binary = directory.appendingPathComponent("binary.bin")
            try Data([1, 2, 0, 4]).write(to: binary)
            try expectError({ _ = try TextFile.open(binary) }, matching: .binaryFile)
            let huge = directory.appendingPathComponent("huge.txt")
            fm.createFile(atPath: huge.path, contents: nil)
            let handle = try FileHandle(forWritingTo: huge)
            try handle.truncate(atOffset: UInt64(TextFile.maximumSize + 1))
            try handle.close()
            try expectError({ _ = try TextFile.open(huge) }, matching: .tooLarge)
        }

        print("FileCoreTests: \(passed) groups passed")
    }

    static func test(_ title: String, _ body: () throws -> Void) throws {
        try body()
        passed += 1
        print("PASS \(title)")
        fflush(stdout)
    }

    static func expect(_ condition: @autoclosure () throws -> Bool, _ message: String) throws {
        if try !condition() { throw NSError(domain: "FileCoreTests", code: 1, userInfo: [NSLocalizedDescriptionKey: message]) }
    }

    static func expectError(_ body: () throws -> Void, matching expected: TextFileError) throws {
        do { try body() }
        catch let actual as TextFileError {
            if String(describing: actual) == String(describing: expected) { return }
            throw actual
        }
        throw NSError(domain: "FileCoreTests", code: 2, userInfo: [NSLocalizedDescriptionKey: "Expected \(expected)"])
    }

    static func expectIO(_ body: () throws -> Void, contains fragment: String) throws {
        do { try body() }
        catch TextFileError.io(let message) {
            try expect(message.contains(fragment), "rename error is actionable: \(message)")
            return
        }
        throw NSError(domain: "FileCoreTests", code: 6, userInfo: [NSLocalizedDescriptionKey: "Expected rename error containing \(fragment)"])
    }

    static func aclText(_ url: URL) throws -> String {
        guard let acl = acl_get_file(url.path, ACL_TYPE_EXTENDED) else {
            throw NSError(domain: NSPOSIXErrorDomain, code: Int(errno))
        }
        defer { acl_free(UnsafeMutableRawPointer(acl)) }
        guard let text = acl_to_text(acl, nil) else {
            throw NSError(domain: NSPOSIXErrorDomain, code: Int(errno))
        }
        defer { acl_free(text) }
        return String(cString: text)
    }
}
