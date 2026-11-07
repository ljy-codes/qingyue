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
    context.cgContext.scaleBy(x: CGFloat(pixels) / 1024, y: CGFloat(pixels) / 1024)
    color(0x11141A).setFill()
    NSBezierPath(roundedRect: NSRect(x: 36, y: 36, width: 952, height: 952), xRadius: 210, yRadius: 210).fill()

    color(0x253A63).setFill()
    NSBezierPath(roundedRect: NSRect(x: 233, y: 191, width: 580, height: 628), xRadius: 62, yRadius: 62).fill()
    color(0x4782FF).setFill()
    NSBezierPath(roundedRect: NSRect(x: 207, y: 218, width: 580, height: 628), xRadius: 62, yRadius: 62).fill()

    color(0xEDF1F8).setFill()
    NSBezierPath(roundedRect: NSRect(x: 274, y: 288, width: 446, height: 493), xRadius: 22, yRadius: 22).fill()
    color(0xC3D4FA).setFill()
    NSBezierPath(rect: NSRect(x: 274, y: 288, width: 22, height: 493)).fill()

    let bookmark = NSBezierPath()
    bookmark.move(to: NSPoint(x: 605, y: 781))
    bookmark.line(to: NSPoint(x: 662, y: 781))
    bookmark.line(to: NSPoint(x: 662, y: 653))
    bookmark.line(to: NSPoint(x: 633.5, y: 678))
    bookmark.line(to: NSPoint(x: 605, y: 653))
    bookmark.close()
    color(0x4782FF).setFill()
    bookmark.fill()

    color(0x5B6F96).setFill()
    let strokes: [(CGFloat, CGFloat)] = [(634, 222), (551, 315), (468, 315), (385, 222)]
    for (y, width) in strokes {
        NSBezierPath(roundedRect: NSRect(x: 333, y: y, width: width, height: 22), xRadius: 11, yRadius: 11).fill()
    }
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
