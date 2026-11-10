import AppKit

// Build-time asset generation only. The app itself never runs this script.
let arguments = CommandLine.arguments
guard arguments.count == 2 else {
    fputs("用法：swift make-icon.swift /path/to/AppIcon.icns\n", stderr)
    exit(1)
}
let destination = URL(fileURLWithPath: arguments[1])
try FileManager.default.createDirectory(at: destination.deletingLastPathComponent(), withIntermediateDirectories: true)

func color(_ value: UInt32) -> NSColor {
    NSColor(srgbRed: CGFloat((value >> 16) & 255) / 255,
            green: CGFloat((value >> 8) & 255) / 255,
            blue: CGFloat(value & 255) / 255, alpha: 1)
}

func paintIcon(pixels: Int) throws -> Data {
    guard let bitmap = NSBitmapImageRep(bitmapDataPlanes: nil, pixelsWide: pixels, pixelsHigh: pixels,
                                      bitsPerSample: 8, samplesPerPixel: 4, hasAlpha: true,
                                      isPlanar: false, colorSpaceName: .deviceRGB,
                                      bytesPerRow: 0, bitsPerPixel: 0),
          let context = NSGraphicsContext(bitmapImageRep: bitmap) else {
        throw NSError(domain: "LightReaderIcon", code: 1)
    }
    NSGraphicsContext.saveGraphicsState()
    NSGraphicsContext.current = context
    let source = URL(fileURLWithPath: #filePath).deletingLastPathComponent().deletingLastPathComponent()
        .appendingPathComponent("Windows/src/Qingyue.Windows/Assets/Qingyue-512.png")
    guard let image = NSImage(contentsOf: source) else { throw NSError(domain: "QingyueSharedIcon", code: 1) }
    context.imageInterpolation = .high
    image.draw(in: NSRect(x: 0, y: 0, width: pixels, height: pixels), from: .zero, operation: .copy, fraction: 1)
    NSGraphicsContext.restoreGraphicsState()
    guard let png = bitmap.representation(using: .png, properties: [:]) else {
        throw NSError(domain: "LightReaderIcon", code: 2)
    }
    return png
}

func bigEndianLength(_ size: Int) -> Data {
    var value = UInt32(size).bigEndian
    return withUnsafeBytes(of: &value) { Data($0) }
}

// ICNS supports PNG data in these modern image chunks, including Retina sizes.
let representations: [(String, Int)] = [
    ("icp4", 16), ("ic11", 32), ("icp5", 32), ("ic12", 64),
    ("ic07", 128), ("ic13", 256), ("ic08", 256), ("ic14", 512),
    ("ic09", 512), ("ic10", 1024)
]
var contents = Data()
for (type, pixels) in representations {
    let png = try paintIcon(pixels: pixels)
    contents.append(type.data(using: .ascii)!)
    contents.append(bigEndianLength(png.count + 8))
    contents.append(png)
}
var icon = Data("icns".utf8)
icon.append(bigEndianLength(contents.count + 8))
icon.append(contents)
try icon.write(to: destination, options: .atomic)
