import AppKit
import Foundation

/// Process-local bounded cache used by later viewport schedulers. It never
/// writes image data to disk and only retains decoded images for the current
/// app session.
final class ReadingMediaCache {
    struct Key: Hashable { let reference: String; let revision: String }
    private let cache = NSCache<NSString, NSImage>()
    private let lock = NSLock()
    init(countLimit: Int = 64, totalCostLimit: Int = 48 * 1024 * 1024) { cache.countLimit = max(1, countLimit); cache.totalCostLimit = max(1, totalCostLimit) }
    func image(for key: Key) -> NSImage? { lock.lock(); defer { lock.unlock() }; return cache.object(forKey: cacheKey(key)) }
    func insert(_ image: NSImage, for key: Key, cost: Int? = nil) { lock.lock(); cache.setObject(image, forKey: cacheKey(key), cost: cost ?? max(1, Int(image.size.width * image.size.height * 4))); lock.unlock() }
    func removeAll() { lock.lock(); cache.removeAllObjects(); lock.unlock() }
    func load(_ key: Key, retries: Int = 1, queue: DispatchQueue = .global(qos: .userInitiated), loader: @escaping () throws -> NSImage, completion: @escaping (Result<NSImage, Error>) -> Void) {
        if let image = image(for: key) { DispatchQueue.main.async { completion(.success(image)) }; return }
        queue.async { [weak self] in
            var last: Error?
            for _ in 0...max(0, retries) {
                do { let image = try loader(); self?.insert(image, for: key); DispatchQueue.main.async { completion(.success(image)) }; return }
                catch { last = error }
            }
            DispatchQueue.main.async { completion(.failure(last ?? NSError(domain: "ReadingMediaCache", code: 1))) }
        }
    }
    private func cacheKey(_ key: Key) -> NSString { "\(key.revision)|\(key.reference)" as NSString }
}
