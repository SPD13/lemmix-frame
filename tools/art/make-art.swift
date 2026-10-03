// make-art.swift <neolemmix gfx/menu dir> <neolemmix styles dir> <repo root>
// The app's own artwork, from NeoLemmix's title screen as the newer releases draw it (the dirt
// background.png, logo.png) and a "STEAM FRAME EDITION" subtitle in a heavy slab serif, yellow
// with the logo's dark green outline and a drop shadow:
//   app/Splash/splash.png          1920 x 1080  Godot's boot splash
//   app/Splash/subtitle.png        the subtitle alone, transparent, for the lobby's title screen
//   steam/library/capsule.png      600 x 900    Steam library capsule (with sign_group.png's lemming,
//                                               "VR" on its board)
//   steam/library/header.png       920 x 430    Steam library header
//   steam/library/hero.png         3840 x 1240  Steam library hero (no logo: Steam lays it over): a
//                                               small Dirt level at 10x, lemmings at work on it
//   steam/library/logo.png         1280 x 720   Steam library logo, transparent
// Every picture: the dirt tiled, a warm glow behind the logo, the edges darkened. Run on the Mac
// (CoreGraphics, CoreText): tools/art/make-art.sh.
import CoreGraphics
import CoreText
import Foundation
import ImageIO
import UniformTypeIdentifiers

let args = CommandLine.arguments
guard args.count == 4 else { FileHandle.standardError.write("usage: make-art <gfx/menu dir> <styles dir> <repo root>\n".data(using: .utf8)!); exit(2) }
let menu = URL(fileURLWithPath: args[1]), styles = URL(fileURLWithPath: args[2]), root = URL(fileURLWithPath: args[3])

func load(_ name: String, from dir: URL = menu) -> CGImage {
    guard let src = CGImageSourceCreateWithURL(dir.appendingPathComponent(name) as CFURL, nil),
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

// ---- pixels: a level's worth, drawn the way the game draws them, blown up afterwards
final class Pixels {
    let w: Int, h: Int
    var d: [UInt8]                                    // RGBA, straight alpha
    init(_ w: Int, _ h: Int) { self.w = w; self.h = h; d = [UInt8](repeating: 0, count: w * h * 4) }
    init(_ img: CGImage) {
        w = img.width; h = img.height
        d = [UInt8](repeating: 0, count: w * h * 4)
        let c = CGContext(data: &d, width: w, height: h, bitsPerComponent: 8, bytesPerRow: w * 4, space: cs,
                          bitmapInfo: CGImageAlphaInfo.premultipliedLast.rawValue)!
        c.draw(img, in: CGRect(x: 0, y: 0, width: w, height: h))
        // (premultiplied: the pieces' alpha is all or nothing, so the colours stand)
    }
    func a(_ x: Int, _ y: Int) -> UInt8 { x < 0 || y < 0 || x >= w || y >= h ? 0 : d[(y * w + x) * 4 + 3] }
    // `src`'s box (sx, sy, sw, sh) over this at (x, y), mirrored across if `flip`
    func put(_ src: Pixels, _ x: Int, _ y: Int, sx: Int = 0, sy: Int = 0, sw: Int? = nil, sh: Int? = nil, flip: Bool = false) {
        let bw = sw ?? src.w, bh = sh ?? src.h
        for j in 0..<bh { for i in 0..<bw {
            let fx = sx + (flip ? bw - 1 - i : i), fy = sy + j, tx = x + i, ty = y + j
            if tx < 0 || ty < 0 || tx >= w || ty >= h { continue }
            let si = (fy * src.w + fx) * 4
            if src.d[si + 3] < 128 { continue }
            // the sprites' mask colour (magenta) is never drawn
            if src.d[si] == 255 && src.d[si + 1] == 0 && src.d[si + 2] == 255 { continue }
            let di = (ty * w + tx) * 4
            d[di] = src.d[si]; d[di + 1] = src.d[si + 1]; d[di + 2] = src.d[si + 2]; d[di + 3] = 255
        } }
    }
    func clear(_ x: Int, _ y: Int, _ cw: Int, _ ch: Int) {
        for j in y..<(y + ch) { for i in x..<(x + cw) where i >= 0 && j >= 0 && i < w && j < h { d[(j * w + i) * 4 + 3] = 0 } }
    }
    func fill(_ x: Int, _ y: Int, _ cw: Int, _ ch: Int, _ r: UInt8, _ g: UInt8, _ b: UInt8) {
        for j in y..<(y + ch) { for i in x..<(x + cw) where i >= 0 && j >= 0 && i < w && j < h {
            let di = (j * w + i) * 4; d[di] = r; d[di + 1] = g; d[di + 2] = b; d[di + 3] = 255 } }
    }
    // the first solid row from the top in a column (the ground a lemming stands on), or h
    func surface(_ x: Int, from y0: Int = 0) -> Int { var y = y0; while y < h && a(x, y) == 0 { y += 1 }; return y }
    func image() -> CGImage {
        var p = d                                       // premultiply for CoreGraphics
        for i in stride(from: 0, to: p.count, by: 4) { let al = Int(p[i + 3]); if al < 255 { p[i] = UInt8(Int(p[i]) * al / 255); p[i + 1] = UInt8(Int(p[i + 1]) * al / 255); p[i + 2] = UInt8(Int(p[i + 2]) * al / 255) } }
        let c = CGContext(data: &p, width: w, height: h, bitsPerComponent: 8, bytesPerRow: w * 4, space: cs,
                          bitmapInfo: CGImageAlphaInfo.premultipliedLast.rawValue)!
        return c.makeImage()!
    }
}

// a lemming animation from the sprite set: frames stacked down, left-facing in the left half
// and right-facing in the right; its frame count and feet from scheme.nxmi
struct Anim { let sheet: Pixels; let frames: Int; let footR: (Int, Int); let footL: (Int, Int) }
let lemmingsDir = styles.appendingPathComponent("default/lemmings")
let scheme = (try? String(contentsOf: lemmingsDir.appendingPathComponent("scheme.nxmi"), encoding: .utf8)) ?? ""
func anim(_ name: String) -> Anim {
    // the $NAME section: FRAMES, then $RIGHT / $LEFT with FOOT_X / FOOT_Y
    let lines = scheme.components(separatedBy: .newlines).map { $0.trimmingCharacters(in: .whitespaces) }
    var i = lines.firstIndex(of: "$" + name.uppercased())!
    var frames = 1, side = "", foot: [String: (Int, Int)] = [:], depth = 0
    i += 1
    while i < lines.count {
        let l = lines[i]; i += 1
        if l == "$END" { if depth == 0 { break }; depth -= 1; continue }
        if l.hasPrefix("$") { side = String(l.dropFirst()); depth += 1; continue }
        let parts = l.split(separator: " ")
        guard parts.count == 2, let v = Int(parts[1]) else { continue }
        if parts[0] == "FRAMES" { frames = v }
        else if parts[0] == "FOOT_X" { foot[side] = (v, foot[side]?.1 ?? 0) }
        else if parts[0] == "FOOT_Y" { foot[side] = (foot[side]?.0 ?? 0, v) }
    }
    return Anim(sheet: Pixels(load(name + ".png", from: lemmingsDir)), frames: frames, footR: foot["RIGHT"] ?? (8, 10), footL: foot["LEFT"] ?? (8, 10))
}
// a lemming of `a` at frame `f`, its feet at (x, y), facing right or left
func lemming(_ on: Pixels, _ a: Anim, _ f: Int, _ x: Int, _ y: Int, right: Bool = true) {
    let fw = a.sheet.w / 2, fh = a.sheet.h / a.frames, foot = right ? a.footR : a.footL
    on.put(a.sheet, x - foot.0, y - foot.1, sx: right ? fw : 0, sy: (f % a.frames) * fh, sw: fw, sh: fh)
}

// the hero's level: 384 x 124 game pixels (10x: 3840 x 1240). Steam lays the logo over the
// bottom left, so the busy part is in the middle and on the right.
func heroScene() -> Pixels {
    let dirtDir = styles.appendingPathComponent("orig_dirt")
    func piece(_ n: String) -> Pixels { Pixels(load(n + ".png", from: dirtDir.appendingPathComponent("terrain"))) }
    func object(_ n: String) -> Pixels { Pixels(load(n + ".png", from: dirtDir.appendingPathComponent("objects"))) }
    let W = 384, H = 124
    let terrain = Pixels(W, H)
    // the ground: the long flat clump end to end, every other one mirrored, a little grass on it
    let slab = piece("clump_04")
    var x = -8, flip = false
    while x < W { terrain.put(slab, x, H - slab.h, flip: flip); x += slab.w - 6; flip.toggle() }
    // a hill on the right (the exit on top, a climber up its side) and a mound for the basher
    let hill = piece("clump_05")
    let hillX = 304
    terrain.put(hill, hillX, H - hill.h)
    let mound = piece("clump_02")
    let moundX = 244
    terrain.put(mound, moundX, H - slab.h - mound.h + 10)
    let ground = { (x: Int) in terrain.surface(x) }

    // the digger's hole and the basher's tunnel, cut before anyone stands in them
    let digX = 214, digDepth = 7
    let digTop = ground(digX)
    terrain.clear(digX - 4, digTop, 9, digDepth)
    let bashY = ground(moundX - 6)
    terrain.clear(moundX - 2, bashY - 10, 13, 10)

    let scene = Pixels(W, H)
    // the trapdoor, open, a lemming dropping from it; the exit on the hill
    let window = object("window"), wf = window.h / 10
    scene.put(window, 150, 6, sy: 9 * wf, sh: wf)
    let exit = object("exit"), ef = exit.h / 6
    let hillTop = ground(hillX + hill.w / 2 + 2)
    scene.put(exit, hillX + hill.w / 2 - exit.w / 2, hillTop - ef + 2, sy: 0, sh: ef)
    scene.put(terrain, 0, 0)

    let walker = anim("walker"), faller = anim("faller"), blocker = anim("blocker"), builder = anim("builder")
    let digger = anim("digger"), basher = anim("basher"), climber = anim("climber"), floater = anim("floater")
    let exiter = anim("exiter")
    lemming(scene, faller, 1, 174, 34)
    lemming(scene, faller, 3, 173, 62)
    lemming(scene, walker, 2, 70, ground(70))
    lemming(scene, walker, 6, 112, ground(112))
    lemming(scene, walker, 3, 140, ground(140), right: false)
    lemming(scene, blocker, 4, 160, ground(160))
    // the builder on his fourth brick, going up to the right
    let bx = 178, by = ground(bx)
    for i in 0..<5 { scene.fill(bx - 2 + i * 2, by - 1 - i, 6, 1, 0xD0, 0x80, 0x20) }
    lemming(scene, builder, 6, bx + 8, by - 4)
    lemming(scene, digger, 5, digX, digTop + digDepth - 2)
    lemming(scene, basher, 6, moundX + 4, bashY)
    lemming(scene, climber, 3, hillX + 3, H - 40)
    lemming(scene, floater, 10, 274, 46, right: false)
    lemming(scene, exiter, 3, hillX + hill.w / 2 + 1, hillTop)
    lemming(scene, walker, 1, 360, ground(360), right: false)
    return scene
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
    dirt(p, tile: 2.0, glow: CGPoint(x: 2300, y: 560), glowRadius: 1900)
    let scene = heroScene()
    p.ctx.interpolationQuality = .none
    p.ctx.draw(scene.image(), in: CGRect(x: 0, y: 0, width: 3840, height: 1240))
    p.save("steam/library/hero.png")
}
do {
    let p = Picture(1280, 720)
    titleBlock(p, logoWidth: 1240, centreY: 360)
    p.save("steam/library/logo.png")
}
