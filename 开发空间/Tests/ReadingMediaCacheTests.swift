import AppKit
import Foundation

@main
struct ReadingMediaCacheTests {
    private static var checks = 0
    static func main() {
        let cache = ReadingMediaCache(countLimit: 2); let key = ReadingMediaCache.Key(reference: "a.png", revision: "1"); var attempts = 0
        let done = DispatchSemaphore(value: 0)
        cache.load(key, retries: 2, loader: {
            attempts += 1; if attempts < 2 { throw NSError(domain: "test", code: 1) }; return NSImage(size: NSSize(width: 4, height: 4))
        }) { result in if case .success(let image) = result { expect(image.size.width == 4, "重试后返回解码图片") }; done.signal() }
        pump(until: done); expect(attempts == 2, "失败后按预算重试")
        expect(cache.image(for: key) != nil, "成功结果进入进程内缓存")
        let second = DispatchSemaphore(value: 0); var called = 0
        cache.load(key, retries: 0, loader: { called += 1; return NSImage(size: NSSize(width: 1, height: 1)) }) { _ in second.signal() }
        pump(until: second); expect(called == 0, "缓存命中不重复解码")
        print("ReadingMediaCacheTests: \(checks) checks passed")
    }
    private static func expect(_ value: @autoclosure () -> Bool, _ message: String) { guard value() else { fatalError(message) }; checks += 1 }
    private static func pump(until semaphore: DispatchSemaphore) {
        let deadline = Date(timeIntervalSinceNow: 3)
        while semaphore.wait(timeout: .now()) != .success {
            guard Date() < deadline else { fatalError("缓存回调未返回") }
            RunLoop.current.run(until: Date(timeIntervalSinceNow: 0.01))
        }
    }
}
