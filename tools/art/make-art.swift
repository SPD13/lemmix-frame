// make-art.swift <neolemmix gfx/menu dir> <repo root>
// The app's own artwork, from NeoLemmix's title screen as the newer releases draw it (the dirt
// background.png, logo.png) and a "STEAM FRAME EDITION" subtitle in a heavy slab serif, yellow
// with the logo's dark green outline and a drop shadow:
//   app/Splash/splash.png          1920 x 1080  Godot's boot splash
//   app/Splash/subtitle.png        the subtitle alone, transparent, for the lobby's title screen
//   steam/library/capsule.png      600 x 900    Steam library capsule (with sign_group.png's lemming,
//                                               "VR" on its board)
//   steam/library/header.png       920 x 430    Steam library header
//   steam/library/hero.png         3840 x 1240  Steam library hero (no logo: Steam lays it over)
//   steam/library/logo.png         1280 x 720   Steam library logo, transparent
// Every picture: the dirt tiled, a warm glow behind the logo, the edges darkened. Run on the Mac
// (CoreGraphics, CoreText): tools/art/make-art.sh.
import CoreGraphics
import CoreText
import Foundation
import ImageIO
import UniformTypeIdentifiers

let args = CommandLine.arguments
guard args.count == 3 else { FileHandle.standardError.write("usage: make-art <gfx/menu dir> <repo root>\n".data(using: .utf8)!); exit(2) }
let menu = URL(fileURLWithPath: args[1]), root = URL(fileURLWithPath: args[2])

func load(_ name: String) -> CGImage {
    guard let src = CGImageSourceCreateWithURL(menu.appendingPathComponent(name) as CFURL, nil),
          let img = CGImageSourceCreateImageAtIndex(src, 0, nil) else { fatalError("cannot read \(name)") }
    return img
}
let background = load("background.png"), logo = load("logo.png"), signGroup = load("sign_group.png")
let cs = CGColorSpace(name: CGColorSpace.sRGB)!
func rgb(_ r: Int, _ g: Int, _ b: Int, _ a: CGFloat = 1) -> CGColor { CGColor(srgbRed: CGFloat(r) / 255, green: CGFloat(g) / 255, blue: CGFloat(b) / 255, alpha: a) }

// a picture to draw on, y counted from the top (CoreGraphics counts it up from the bottom)
final class Picture {
    let w: Int, h: Int, ctx: CGContext
    init(_ w: Int, _ h: Int) {
        self.w = w; self.h = h
        ctx = CGContext(data: nil, width: w, height: h, bitsPerComponent: 8, bytesPerRow: 0, space: cs,
                        bitmapInfo: CGImageAlphaInfo.premultipliedLast.rawValue)!
        ctx.interpolationQuality = .high
    }
    func y(_ top: CGFloat, _ height: CGFloat = 0) -> CGFloat { CGFloat(h) - top - height }
    func save(_ path: String) {
        let url = root.appendingPathComponent(path)
        try? FileManager.default.createDirectory(at: url.deletingLastPathComponent(), withIntermediateDirectories: true)
        let dest = CGImageDestinationCreateWithURL(url as CFURL, UTType.png.identifier as CFString, 1, nil)!
        CGImageDestinationAddImage(dest, ctx.makeImage()!, nil)
        guard CGImageDestinationFinalize(dest) else { fatalError("cannot write \(path)") }
        print("\(path) (\(w) x \(h))")
    }
}

// the dirt tiled (`tile`: its scale), a warm glow round `glow` (from the top), the edges darkened
func dirt(_ p: Picture, tile: CGFloat, glow: CGPoint, glowRadius: CGFloat) {
    let t = CGFloat(background.width) * tile
    var ty: CGFloat = 0
    while ty < CGFloat(p.h) { var tx: CGFloat = 0; while tx < CGFloat(p.w) { p.ctx.draw(background, in: CGRect(x: tx, y: ty, width: t, height: t)); tx += t }; ty += t }
    let c = CGPoint(x: glow.x, y: p.y(glow.y))
    let warm = CGGradient(colorsSpace: cs, colors: [rgb(150, 82, 40, 0.38), rgb(150, 82, 40, 0)] as CFArray, locations: [0, 1])!
    p.ctx.drawRadialGradient(warm, startCenter: c, startRadius: 0, endCenter: c, endRadius: glowRadius, options: [])
    let dark = CGGradient(colorsSpace: cs, colors: [rgb(0, 0, 0, 0), rgb(0, 0, 0, 0.72)] as CFArray, locations: [0, 1])!
    let reach = max(CGFloat(p.w), CGFloat(p.h))
    p.ctx.drawRadialGradient(dark, startCenter: c, startRadius: reach * 0.22, endCenter: c, endRadius: reach * 0.6, options: [.drawsAfterEndLocation])
}

// the logo `width` wide, centred across, its top at `top`; its height
@discardableResult
func drawLogo(_ p: Picture, width: CGFloat, top: CGFloat) -> CGFloat {
    let s = width / CGFloat(logo.width), lh = CGFloat(logo.height) * s
    p.ctx.draw(logo, in: CGRect(x: (CGFloat(p.w) - width) / 2, y: p.y(top, lh), width: width, height: lh))
    return lh
}

// "STEAM FRAME EDITION" at `size` points, centred across, its baseline at `baseline` (from the
// top); outline, rim and shadow in proportion to the size (68 pt: 12, 4, (5, 7))
let subtitleText = "STEAM FRAME EDITION"
func subtitleFont(_ size: CGFloat) -> CTFont {
    var f = CTFontCreateWithName("Superclarendon-Black" as CFString, size, nil)
    if (CTFontCopyPostScriptName(f) as String) != "Superclarendon-Black" { f = CTFontCreateWithName("Rockwell-Bold" as CFString, size, nil) }
    return f
}
func subtitleLine(_ size: CGFloat, _ text: String = subtitleText) -> CTLine {
    let attrs: [NSAttributedString.Key: Any] = [.init(kCTFontAttributeName as String): subtitleFont(size), .init(kCTKernAttributeName as String): size * 5 / 68,
                                                 .init(kCTForegroundColorFromContextAttributeName as String): true]
    return CTLineCreateWithAttributedString(NSAttributedString(string: text, attributes: attrs))
}
func drawSubtitle(_ p: Picture, size: CGFloat, baseline: CGFloat, text: String = subtitleText, centreX: CGFloat? = nil) {
    let ctx = p.ctx, line = subtitleLine(size, text), k = size / 68
    ctx.textPosition = .zero                          // (the image bounds are taken from the text position)
    let bounds = CTLineGetImageBounds(line, ctx)
    let origin = CGPoint(x: (centreX ?? CGFloat(p.w) / 2) - bounds.width / 2 - bounds.minX, y: p.y(baseline))
    func draw(at q: CGPoint, mode: CGTextDrawingMode) { ctx.textPosition = q; ctx.setTextDrawingMode(mode); CTLineDraw(line, ctx) }
    ctx.setLineJoin(.round)
    ctx.setFillColor(rgb(0, 0, 0, 0.65)); ctx.setStrokeColor(rgb(0, 0, 0, 0.65)); ctx.setLineWidth(12 * k)
    draw(at: CGPoint(x: origin.x + 5 * k, y: origin.y - 7 * k), mode: .fillStroke)
    ctx.setStrokeColor(rgb(10, 52, 8)); ctx.setLineWidth(12 * k)
    draw(at: origin, mode: .stroke)
    ctx.setStrokeColor(rgb(150, 170, 20)); ctx.setLineWidth(4 * k)
    draw(at: origin, mode: .stroke)
    ctx.saveGState()
    draw(at: origin, mode: .clip)
    let fill = CGGradient(colorsSpace: cs, colors: [rgb(255, 246, 140), rgb(250, 222, 30), rgb(214, 172, 8)] as CFArray, locations: [0, 0.5, 1])!
    ctx.drawLinearGradient(fill, start: CGPoint(x: 0, y: origin.y + bounds.maxY), end: CGPoint(x: 0, y: origin.y + bounds.minY), options: [])
    ctx.restoreGState()
}
// the subtitle's size in points for a given width of its letters
func subtitleSize(width: CGFloat) -> CGFloat {
    let probe = Picture(8, 8)
    return 100 * width / CTLineGetImageBounds(subtitleLine(100), probe.ctx).width
}

// the logo and the subtitle stacked, centred on `centreY`, the logo `logoWidth` wide
func titleBlock(_ p: Picture, logoWidth: CGFloat, centreY: CGFloat) {
    let lh = CGFloat(logo.height) * logoWidth / CGFloat(logo.width)
    let size = subtitleSize(width: logoWidth * 0.74)
    let gap = size * 1.65                              // the logo's foot to the subtitle's baseline
    let top = centreY - (lh + gap) / 2
    drawLogo(p, width: logoWidth, top: top)
    drawSubtitle(p, size: size, baseline: top + lh + gap)
}

// ---- the boot splash
do {
    let p = Picture(1920, 1080)
    dirt(p, tile: 1.5, glow: CGPoint(x: 960, y: 500), glowRadius: 760)
    titleBlock(p, logoWidth: 1500, centreY: 520)
    p.save("app/Splash/splash.png")
}
// ---- the lobby's subtitle: 720 px wide letters (360 of the title screen's 864, at its 2x canvas)
do {
    let size = subtitleSize(width: 720)
    let p = Picture(760, Int((size * 1.45).rounded()))
    drawSubtitle(p, size: size, baseline: size * 1.08)
    p.save("app/Splash/subtitle.png")
}
// ---- Steam: the library capsule, header, hero and logo
do {
    let p = Picture(600, 900)
    dirt(p, tile: 1.0, glow: CGPoint(x: 300, y: 420), glowRadius: 560)
    titleBlock(p, logoWidth: 560, centreY: 215)
    // the lemming holding up its sign, 4x and crisp, "VR" on the board's panel
    let k: CGFloat = 4, sw = CGFloat(signGroup.width) * k, sh = CGFloat(signGroup.height) * k
    let sx = (CGFloat(p.w) - sw) / 2, top: CGFloat = 900 - sh - 70
    p.ctx.interpolationQuality = .none
    p.ctx.draw(signGroup, in: CGRect(x: sx, y: p.y(top, sh), width: sw, height: sh))
    p.ctx.interpolationQuality = .high
    // (the board's panel: 72..108 x 38..64 of the 120 x 87 sign, centred on its middle)
    drawSubtitle(p, size: 96, baseline: top + 64 * k - 16, text: "VR", centreX: sx + 72 * k)
    p.save("steam/library/capsule.png")
}
do {
    let p = Picture(920, 430)
    dirt(p, tile: 1.0, glow: CGPoint(x: 460, y: 215), glowRadius: 520)
    titleBlock(p, logoWidth: 820, centreY: 215)
    p.save("steam/library/header.png")
}
do {
    let p = Picture(3840, 1240)
    dirt(p, tile: 2.0, glow: CGPoint(x: 1920, y: 620), glowRadius: 1700)
    p.save("steam/library/hero.png")
}
do {
    let p = Picture(1280, 720)
    titleBlock(p, logoWidth: 1240, centreY: 360)
    p.save("steam/library/logo.png")
}
