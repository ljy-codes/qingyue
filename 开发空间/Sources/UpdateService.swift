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

struct QingyueMacRelease: Equatable {
    let version: QingyueVersion?
    let build: Int?
    let url: URL
}

enum QingyueUpdateService {
    static let releasePrefix = "https://github.com/ljy-codes/qingyue/releases/tag/"

    /// Compatibility entry point for callers which only know the legacy build number.
    static func latestMacRelease(data: Data, currentBuild: Int) throws -> (build: Int, url: URL)? {
        try macReleases(data: data)
            .compactMap { release -> (build: Int, url: URL)? in
                guard let build = release.build, build > currentBuild else { return nil }
                return (build, release.url)
            }
            .max { $0.build < $1.build }
    }

    /// Version tags are compared only to the product version, never to a build
    /// number. Older macos-build-* releases remain a fallback for existing users.
    static func latestMacRelease(data: Data, currentBuild: Int, currentVersion: String) throws -> QingyueMacRelease? {
        guard let current = QingyueVersion(currentVersion) else {
            throw NSError(domain: "QingyueUpdateService", code: 1, userInfo: [NSLocalizedDescriptionKey: "当前版本号无效，无法检查 Mac 更新。"])
        }
        let releases = try macReleases(data: data)
        if let latest = releases.filter({ $0.version.map { $0 > current } ?? false })
            .max(by: { $0.version! < $1.version! }) {
            return latest
        }
        return releases.filter { $0.build.map { $0 > currentBuild } ?? false }
            .max { $0.build! < $1.build! }
    }

    private static func macReleases(data: Data) throws -> [QingyueMacRelease] {
        guard data.count <= 2 * 1024 * 1024, let releases = try JSONSerialization.jsonObject(with: data) as? [[String: Any]] else { throw CocoaError(.fileReadCorruptFile) }
        return releases.compactMap { item in
            guard item["draft"] as? Bool != true, let tag = item["tag_name"] as? String,
                  let link = item["html_url"] as? String, link == releasePrefix + tag, let url = URL(string: link) else { return nil }
            if tag.hasPrefix("macos-build-") {
                let number = tag.dropFirst("macos-build-".count)
                guard !number.isEmpty, number.allSatisfy({ $0.isASCII && $0.isNumber }),
                      let build = Int(number), build > 0 else { return nil }
                // Legacy Mac deliveries used GitHub prereleases.
                return QingyueMacRelease(version: nil, build: build, url: url)
            }
            // A platform suffix is mandatory. Bare vX.Y.Z tags and Windows
            // tags belong to the independent Windows release history.
            guard item["prerelease"] as? Bool != true, tag.hasPrefix("v"), tag.hasSuffix("-macos"),
                  let version = QingyueVersion(String(tag.dropLast("-macos".count))) else { return nil }
            return QingyueMacRelease(version: version, build: nil, url: url)
        }
    }

    @discardableResult static func checkMac(currentBuild: Int, completion: @escaping (Result<(build: Int, url: URL)?, Error>) -> Void) -> URLSessionDataTask {
        requestMacReleases(parse: { try latestMacRelease(data: $0, currentBuild: currentBuild) }, completion: completion)
    }

    @discardableResult static func checkMac(currentBuild: Int, currentVersion: String, completion: @escaping (Result<QingyueMacRelease?, Error>) -> Void) -> URLSessionDataTask {
        requestMacReleases(parse: { try latestMacRelease(data: $0, currentBuild: currentBuild, currentVersion: currentVersion) },
                           completion: completion)
    }

    private static func requestMacReleases<Value>(parse: @escaping (Data) throws -> Value, completion: @escaping (Result<Value, Error>) -> Void) -> URLSessionDataTask {
        var request = URLRequest(url: URL(string: "https://api.github.com/repos/ljy-codes/qingyue/releases?per_page=100")!)
        request.timeoutInterval = 5; request.setValue("Qingyue-macOS", forHTTPHeaderField: "User-Agent")
        let task = URLSession.shared.dataTask(with: request) { data, response, error in
            let result: Result<Value, Error> = Result {
                if let error { throw error }
                guard (response as? HTTPURLResponse)?.statusCode == 200, let data else { throw URLError(.badServerResponse) }
                return try parse(data)
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
