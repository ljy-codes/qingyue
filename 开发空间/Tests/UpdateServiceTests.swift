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
        print("UpdateServiceTests: \(checks) checks passed")
    }
    private static func expect(_ value: @autoclosure () -> Bool, _ message: String) { guard value() else { fatalError(message) }; checks += 1 }
}
