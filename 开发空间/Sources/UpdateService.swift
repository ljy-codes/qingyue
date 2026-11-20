import Foundation

struct QingyueVersion: Comparable, Equatable {
    let major: Int
    let minor: Int
    let patch: Int

    init?(_ value: String) {
        var cleaned = value.trimmingCharacters(in: .whitespacesAndNewlines)
        if cleaned.hasPrefix("v") || cleaned.hasPrefix("V") { cleaned.removeFirst() }
        let pieces = cleaned.split(separator: ".", omittingEmptySubsequences: false)
        guard pieces.count == 3, pieces.allSatisfy({ !$0.isEmpty && $0.allSatisfy({ $0.isASCII && $0.isNumber }) }),
              let major = Int(pieces[0]), let minor = Int(pieces[1]), let patch = Int(pieces[2]) else { return nil }
        self.major = major; self.minor = minor; self.patch = patch
    }

    static func < (lhs: QingyueVersion, rhs: QingyueVersion) -> Bool {
        (lhs.major, lhs.minor, lhs.patch) < (rhs.major, rhs.minor, rhs.patch)
    }

    var stringValue: String { "\(major).\(minor).\(patch)" }
}

struct QingyueReleaseUpdate: Equatable {
    let currentVersion: QingyueVersion
    let latestVersion: QingyueVersion?
    let releaseURL: URL?
    let publishedAt: Date?
    let isAvailable: Bool
    let error: String?
}

enum QingyueUpdateService {
    static let releasePrefix = "https://github.com/ljy-codes/qingyue/releases/tag/"

    static func latestMacRelease(data: Data, currentBuild: Int) throws -> (build: Int, url: URL)? {
        guard data.count <= 2 * 1024 * 1024, let releases = try JSONSerialization.jsonObject(with: data) as? [[String: Any]] else { throw CocoaError(.fileReadCorruptFile) }
        return releases.compactMap { item -> (build: Int, url: URL)? in
            guard item["draft"] as? Bool != true, let tag = item["tag_name"] as? String,
                  tag.hasPrefix("macos-build-"), let build = Int(tag.dropFirst("macos-build-".count)), build > currentBuild,
                  let link = item["html_url"] as? String, link == releasePrefix + tag, let url = URL(string: link) else { return nil }
            return (build, url)
        }.max { $0.build < $1.build }
    }

    @discardableResult static func checkMac(currentBuild: Int, completion: @escaping (Result<(build: Int, url: URL)?, Error>) -> Void) -> URLSessionDataTask {
        var request = URLRequest(url: URL(string: "https://api.github.com/repos/ljy-codes/qingyue/releases?per_page=100")!)
        request.timeoutInterval = 5; request.setValue("Qingyue-macOS", forHTTPHeaderField: "User-Agent")
        let task = URLSession.shared.dataTask(with: request) { data, response, error in
            let result: Result<(build: Int, url: URL)?, Error> = Result {
                if let error { throw error }
                guard (response as? HTTPURLResponse)?.statusCode == 200, let data else { throw URLError(.badServerResponse) }
                return try latestMacRelease(data: data, currentBuild: currentBuild)
            }
            DispatchQueue.main.async { completion(result) }
        }
        task.resume(); return task
    }
    static func parseLatestRelease(data: Data, currentVersion: String, now: Date = Date()) -> QingyueReleaseUpdate {
        guard let current = QingyueVersion(currentVersion) else { return QingyueReleaseUpdate(currentVersion: QingyueVersion("0.0.0")!, latestVersion: nil, releaseURL: nil, publishedAt: nil, isAvailable: false, error: "当前版本号无效。") }
        do {
            let root = try JSONSerialization.jsonObject(with: data) as? [[String: Any]] ?? []
            let candidates: [(QingyueVersion, URL, Date?)] = root.compactMap { item in
                guard (item["draft"] as? Bool) != true, (item["prerelease"] as? Bool) != true,
                      let tag = item["tag_name"] as? String, let version = QingyueVersion(tag), let link = item["html_url"] as? String, link == releasePrefix + tag, let url = URL(string: link) else { return nil }
                let date = (item["published_at"] as? String).flatMap { ISO8601DateFormatter().date(from: $0) }
                return (version, url, date)
            }
            guard let latest = candidates.max(by: { $0.0 < $1.0 }) else { return QingyueReleaseUpdate(currentVersion: current, latestVersion: nil, releaseURL: nil, publishedAt: nil, isAvailable: false, error: "没有可用的稳定版本。") }
            return QingyueReleaseUpdate(currentVersion: current, latestVersion: latest.0, releaseURL: latest.1, publishedAt: latest.2, isAvailable: latest.0 > current, error: nil)
        } catch { return QingyueReleaseUpdate(currentVersion: current, latestVersion: nil, releaseURL: nil, publishedAt: nil, isAvailable: false, error: "更新信息格式无效。") }
    }
}
