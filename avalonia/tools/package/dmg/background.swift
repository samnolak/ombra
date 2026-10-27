// Draws the background of an installer image's Finder window (make-dmg.py): a title, a subtitle and an arrow from the
// app's icon to the Applications link, on the window's plain colour. The icons and their names are Finder's own.
//   swift background.swift <out.png> <scale> <width> <height> <title> <subtitle> <title-y> <subtitle-y> <arrow-x1> <arrow-x2> <arrow-y>
// Sizes and positions are in points from the top left (as Finder places the icons); a line's y is the middle of its
// capitals. The image has <scale> pixels per point and that DPI, so Finder shows it at the window's size.
import AppKit

let a = CommandLine.arguments
guard a.count == 12, let scale = Double(a[2]), let width = Double(a[3]), let height = Double(a[4]),
      let titleY = Double(a[7]), let subtitleY = Double(a[8]), let arrowX1 = Double(a[9]), let arrowX2 = Double(a[10]),
      let arrowY = Double(a[11]) else {
    FileHandle.standardError.write("usage: background.swift <out.png> <scale> <width> <height> <title> <subtitle> <title-y> <subtitle-y> <arrow-x1> <arrow-x2> <arrow-y>\n".data(using: .utf8)!)
    exit(2)
}
let (out, title, subtitle) = (a[1], a[5], a[6])

func color(_ hex: UInt32) -> NSColor {
    NSColor(srgbRed: CGFloat((hex >> 16) & 0xFF) / 255, green: CGFloat((hex >> 8) & 0xFF) / 255,
            blue: CGFloat(hex & 0xFF) / 255, alpha: 1)
}

func font(_ size: CGFloat, _ weight: NSFont.Weight, serif: Bool) -> NSFont {
    let system = NSFont.systemFont(ofSize: size, weight: weight)
    guard serif, let descriptor = system.fontDescriptor.withDesign(.serif) else { return system }
    return NSFont(descriptor: descriptor, size: size) ?? system
}

/// One line centred across the window, the middle of its capitals on <y>.
func drawLine(_ text: String, _ font: NSFont, _ color: NSColor, kern: CGFloat, y: Double) {
    let string = NSAttributedString(string: text, attributes: [.font: font, .foregroundColor: color, .kern: kern])
    let baseline = y + font.capHeight / 2
    string.draw(at: NSPoint(x: (width - string.size().width) / 2, y: baseline - font.ascender))
}

let rep = NSBitmapImageRep(bitmapDataPlanes: nil, pixelsWide: Int(width * scale), pixelsHigh: Int(height * scale),
                           bitsPerSample: 8, samplesPerPixel: 4, hasAlpha: true, isPlanar: false,
                           colorSpaceName: .deviceRGB, bytesPerRow: 0, bitsPerPixel: 0)!
rep.size = NSSize(width: width, height: height)
let cg = NSGraphicsContext(bitmapImageRep: rep)!.cgContext
cg.translateBy(x: 0, y: height)
cg.scaleBy(x: 1, y: -1)
NSGraphicsContext.current = NSGraphicsContext(cgContext: cg, flipped: true)

color(0xF4F3EF).setFill()
NSRect(x: 0, y: 0, width: width, height: height).fill()

drawLine(title, font(44, .semibold, serif: true), color(0x161616), kern: -0.6, y: titleY)
drawLine(subtitle, font(17, .regular, serif: false), color(0x6E6C68), kern: 0, y: subtitleY)

let arrow = NSBezierPath()
arrow.lineWidth = 1.75
arrow.lineCapStyle = .round
arrow.lineJoinStyle = .round
arrow.move(to: NSPoint(x: arrowX1, y: arrowY))
arrow.line(to: NSPoint(x: arrowX2, y: arrowY))
arrow.move(to: NSPoint(x: arrowX2 - 9, y: arrowY - 9))
arrow.line(to: NSPoint(x: arrowX2, y: arrowY))
arrow.line(to: NSPoint(x: arrowX2 - 9, y: arrowY + 9))
color(0x8C8A86).setStroke()
arrow.stroke()

NSGraphicsContext.current = nil
try! rep.representation(using: .png, properties: [:])!.write(to: URL(fileURLWithPath: out))
