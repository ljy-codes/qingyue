import Foundation

@main
struct UpdateServiceTests {
    private static var checks = 0
    static func main() {
        let json = """
        [{"tag_name":"v0.13.0","html_url":"https://github.com/ljy-codes/qingyue/releases/tag/v0.13.0","prerelease":false,"draft":false,"published_at":"2026-11-16T00:00:00Z"},{"tag_name":"v0.14.0","html_url":"https://github.com/ljy-codes/qingyue/releases/tag/v0.14.0","prerelease":false,"draft":false,"published_at":"2026-11-17T00:00:00Z"},{"tag_name":"v0.99.0-beta","html_url":"https://github.com/ljy-codes/qingyue/releases/tag/v0.99.0-beta","prerelease":true,"draft":false}]
        """
        let result = QingyueUpdateService.parseLatestRelease(data: Data(json.utf8), currentVersion: "0.13.0")
        expect(result.latestVersion?.stringValue == "0.14.0" && result.isAvailable, "选择最高稳定版本并提示更新")
        expect(result.releaseURL?.scheme == "https", "保留安全发布页 URL")
        let current = QingyueUpdateService.parseLatestRelease(data: Data(json.utf8), currentVersion: "0.14.0")
        expect(!current.isAvailable, "当前版本最新时不提示更新")
        let malformed = QingyueUpdateService.parseLatestRelease(data: Data("bad".utf8), currentVersion: "0.1.0")
        expect(malformed.error != nil && !malformed.isAvailable, "损坏响应安全降级")
        let empty = QingyueUpdateService.parseLatestRelease(data: Data("[]".utf8), currentVersion: "0.1.0")
        expect(empty.error != nil, "没有稳定版本时返回可解释结果")
        for invalid in ["", "v", "0", "0.1", "0.1.x", "0..1", "-1.0.0", "1.2.3.4", "0.99.0-beta"] {
            expect(QingyueVersion(invalid) == nil, "拒绝无效或预发布版本：\(invalid)")
        }
        let macJSON = Data("""
        [{"tag_name":"v0.18.0","html_url":"https://github.com/ljy-codes/qingyue/releases/tag/v0.18.0"},
        {"tag_name":"macos-build-9","prerelease":true,"html_url":"https://github.com/ljy-codes/qingyue/releases/tag/macos-build-9"},
        {"tag_name":"macos-build-999","html_url":"https://evil.invalid/mac"}]
        """.utf8)
        let mac = try! QingyueUpdateService.latestMacRelease(data: macJSON, currentBuild: 8)
        expect(mac?.build == 9, "Mac 更新只选 Mac 标签，不推荐 Windows 包或其他站点")
        expect((try! QingyueUpdateService.latestMacRelease(data: macJSON, currentBuild: 9))?.build == nil, "已是最新 Mac 构建时没有误报")
        let sameVersion = releaseData([release("v0.4.0-macos"), release("macos-build-14")])
        expect((try! QingyueUpdateService.latestMacRelease(data: sameVersion, currentBuild: 14, currentVersion: "0.4.0")) == nil,
               "平台标签中的0.4.0不被误当作本机0.4.0的新构建")
        let versioned = releaseData([
            release("v0.9.0-macos"), release("v0.12.0-macos"), release("v0.4.0-macos"),
            release("macos-build-15"), release("v99.0.0-windows"), release("v99.0.0"),
            release("v99.0.0-macos", prerelease: true), release("v98.0.0-macos", draft: true)
        ])
        let versionUpdate = try! QingyueUpdateService.latestMacRelease(data: versioned, currentBuild: 14, currentVersion: "0.4.0")
        expect(versionUpdate?.version?.stringValue == "0.12.0" && versionUpdate?.build == nil,
               "语义版本按数值排序，优先选择稳定Mac平台版本")
        expect(versionUpdate?.url.absoluteString == QingyueUpdateService.releasePrefix + "v0.12.0-macos",
               "新标签保留精确Mac发布页")
        let newer = releaseData([release("v0.4.0-macos")])
        expect((try! QingyueUpdateService.latestMacRelease(data: newer, currentBuild: 999, currentVersion: "0.3.0"))?.version?.stringValue == "0.4.0",
               "产品版本与构建号独立比较")
        expect((try! QingyueUpdateService.latestMacRelease(data: newer, currentBuild: 1, currentVersion: "0.5.0")) == nil,
               "较旧产品版本不会因本机构建号较小而触发更新")
        let legacy = try! QingyueUpdateService.latestMacRelease(data: macJSON, currentBuild: 8, currentVersion: "0.1.0")
        expect(legacy?.build == 9 && legacy?.version == nil, "新API仍接受历史Mac预发布构建标签")
        expect((try! QingyueUpdateService.latestMacRelease(data: sameVersion, currentBuild: 13, currentVersion: "0.4.0"))?.build == 14,
               "没有更高产品版本时仍识别更高历史构建号")
        expect((try! QingyueUpdateService.latestMacRelease(data: newer, currentBuild: 14)) == nil,
               "只提供构建号的旧API不拿语义版本与构建号混比")
        for tag in ["v99.0.0", "v99.0.0-windows", "windows-build-999", "v99.0-macos",
                    "v99.0.0-beta-macos", "99.0.0-macos", "v99.0.0-macos-extra", "macos-build-+999"] {
            expect((try! QingyueUpdateService.latestMacRelease(data: releaseData([release(tag)]), currentBuild: 14, currentVersion: "0.4.0")) == nil,
                   "忽略Windows或无效Mac标签：\(tag)")
        }
        for item in [release("v99.0.0-macos", draft: true),
                     release("v99.0.0-macos", prerelease: true),
                     release("v99.0.0-macos", url: "https://evil.invalid/mac"),
                     release("v99.0.0-macos", url: QingyueUpdateService.releasePrefix + "v0.4.0-macos")] {
            expect((try! QingyueUpdateService.latestMacRelease(data: releaseData([item]), currentBuild: 14, currentVersion: "0.4.0")) == nil,
                   "草稿、预发布与地址不匹配的新Mac标签不会推荐")
        }
        do {
            _ = try QingyueUpdateService.latestMacRelease(data: newer, currentBuild: 14, currentVersion: "bad")
            fatalError("无效本机版本必须明确失败")
        } catch { expect(true, "无效本机版本不猜测是否升级") }
        print("UpdateServiceTests: \(checks) checks passed")
    }
    private static func release(_ tag: String, draft: Bool = false, prerelease: Bool = false, url: String? = nil) -> [String: Any] {
        ["tag_name": tag, "draft": draft, "prerelease": prerelease,
         "html_url": url ?? QingyueUpdateService.releasePrefix + tag]
    }
    private static func releaseData(_ releases: [[String: Any]]) -> Data { try! JSONSerialization.data(withJSONObject: releases) }
    private static func expect(_ value: @autoclosure () -> Bool, _ message: String) { guard value() else { fatalError(message) }; checks += 1 }
}
