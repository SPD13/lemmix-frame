// make-splash.swift <neolemmix gfx/menu dir> <out.png>
// The app's boot splash (Godot's application/boot_splash/image): NeoLemmix's title screen as the
// newer releases draw it - the dirt background.png tiled, darkened toward the edges with a warm
// glow behind the logo - logo.png in the middle, and under it "STEAM FRAME EDITION" in a heavy
// slab serif, yellow with a dark green outline and a drop shadow (the logo's own colours).
// 1920 x 1080. Run on the Mac (CoreGraphics, CoreText): tools/splash/make-splash.sh.
import CoreGraphics
import CoreText
import Foundation
import ImageIO
import UniformTypeIdentifiers

let args = CommandLine.arguments
guard args.count == 3 else { FileHandle.standardError.write("usage: make-splash <gfx/menu dir> <out.png>\n".data(using: .utf8)!); exit(2) }
let menu = URL(fileURLWithPath: args[1]), out = URL(fileURLWithPath: args[2])

func load(_ name: String) -> CGImage {
    guard let src = CGImageSourceCreateWithURL(menu.appendingPathComponent(name) as CFURL, nil),
          let img = CGImageSourceCreateImageAtIndex(src, 0, nil) else { fatalError("cannot read \(name)") }
    return img
}

let W = 1920, H = 1080
let cs = CGColorSpace(name: CGColorSpace.sRGB)!
let ctx = CGContext(data: nil, width: W, height: H, bitsPerComponent: 8, bytesPerRow: 0, space: cs,
                    bitmapInfo: CGImageAlphaInfo.premultipliedLast.rawValue)!
ctx.interpolationQuality = .high
func rgb(_ r: Int, _ g: Int, _ b: Int, _ a: CGFloat = 1) -> CGColor { CGColor(srgbRed: CGFloat(r) / 255, green: CGFloat(g) / 255, blue: CGFloat(b) / 255, alpha: a) }
// CoreGraphics counts y up from the bottom: a box given from the top
func box(_ x: CGFloat, top: CGFloat, _ w: CGFloat, _ h: CGFloat) -> CGRect { CGRect(x: x, y: CGFloat(H) - top - h, width: w, height: h) }

// ---- the dirt, tiled at 1.5x
let bg = load("background.png")
let tile = CGFloat(bg.width) * 1.5
var ty: CGFloat = 0
while ty < CGFloat(H) { var tx: CGFloat = 0; while tx < CGFloat(W) { ctx.draw(bg, in: CGRect(x: tx, y: ty, width: tile, height: tile)); tx += tile }; ty += tile }

let centre = CGPoint(x: CGFloat(W) / 2, y: CGFloat(H) / 2 + 40)
// a warm glow behind the logo, then the edges darkened
let glow = CGGradient(colorsSpace: cs, colors: [rgb(150, 82, 40, 0.38), rgb(150, 82, 40, 0)] as CFArray, locations: [0, 1])!
ctx.drawRadialGradient(glow, startCenter: centre, startRadius: 0, endCenter: centre, endRadius: 760, options: [])
let vignette = CGGradient(colorsSpace: cs, colors: [rgb(0, 0, 0, 0), rgb(0, 0, 0, 0.72)] as CFArray, locations: [0, 1])!
ctx.drawRadialGradient(vignette, startCenter: centre, startRadius: 420, endCenter: centre, endRadius: 1150, options: [.drawsAfterEndLocation])

// ---- the logo
let logo = load("logo.png")
let ls: CGFloat = 1500 / CGFloat(logo.width)
let lw = CGFloat(logo.width) * ls, lh = CGFloat(logo.height) * ls
let logoTop: CGFloat = 370
ctx.draw(logo, in: box((CGFloat(W) - lw) / 2, top: logoTop, lw, lh))

// ---- the subtitle
let text = "STEAM FRAME EDITION"
var font = CTFontCreateWithName("Superclarendon-Black" as CFString, 68, nil)
if (CTFontCopyPostScriptName(font) as String) != "Superclarendon-Black" {
    font = CTFontCreateWithName("Rockwell-Bold" as CFString, 68, nil)
}
FileHandle.standardError.write("subtitle font: \(CTFontCopyPostScriptName(font))\n".data(using: .utf8)!)
let attrs: [NSAttributedString.Key: Any] = [.init(kCTFontAttributeName as String): font, .init(kCTKernAttributeName as String): 5.0,
                                             .init(kCTForegroundColorFromContextAttributeName as String): true]
let line = CTLineCreateWithAttributedString(NSAttributedString(string: text, attributes: attrs))
let bounds = CTLineGetImageBounds(line, ctx)
let baselineTop = logoTop + lh + 112                  // the baseline, from the top
let origin = CGPoint(x: (CGFloat(W) - bounds.width) / 2 - bounds.minX, y: CGFloat(H) - baselineTop)
func draw(at p: CGPoint, mode: CGTextDrawingMode) { ctx.textPosition = p; ctx.setTextDrawingMode(mode); CTLineDraw(line, ctx) }
ctx.setLineJoin(.round)
// the drop shadow: the letters and their outline, dark, down and right
ctx.setFillColor(rgb(0, 0, 0, 0.65)); ctx.setStrokeColor(rgb(0, 0, 0, 0.65)); ctx.setLineWidth(12)
draw(at: CGPoint(x: origin.x + 5, y: origin.y - 7), mode: .fillStroke)
// the outline: the logo's dark green, then a thin yellow-green rim inside it
ctx.setStrokeColor(rgb(10, 52, 8)); ctx.setLineWidth(12)
draw(at: origin, mode: .stroke)
ctx.setStrokeColor(rgb(150, 170, 20)); ctx.setLineWidth(4)
draw(at: origin, mode: .stroke)
// the fill: yellow, light at the top, deeper at the foot
ctx.saveGState()
draw(at: origin, mode: .clip)
let fill = CGGradient(colorsSpace: cs, colors: [rgb(255, 246, 140), rgb(250, 222, 30), rgb(214, 172, 8)] as CFArray, locations: [0, 0.5, 1])!
ctx.drawLinearGradient(fill, start: CGPoint(x: 0, y: origin.y + bounds.maxY), end: CGPoint(x: 0, y: origin.y + bounds.minY), options: [])
ctx.restoreGState()

// ---- out
let img = ctx.makeImage()!
let dest = CGImageDestinationCreateWithURL(out as CFURL, UTType.png.identifier as CFString, 1, nil)!
CGImageDestinationAddImage(dest, img, nil)
guard CGImageDestinationFinalize(dest) else { fatalError("cannot write \(out.path)") }
print("\(out.path) (\(W) x \(H))")
