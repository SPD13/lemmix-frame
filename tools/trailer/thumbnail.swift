// thumbnail.swift - the trailer's YouTube thumbnail (1280 x 720), appended to make-art.swift's
// drawing code (its Picture, logo and slab subtitle) by thumbnail.sh: a frame of the footage
// filling the picture, darkened at the top under the logo and the subtitle, and sign_group.png's
// lemming holding up "VR" in the bottom right (clear of YouTube's duration badge in the corner).
// THUMB_FRAME: the frame (1920 x 1080, already graded by thumbnail.sh).

let frame: CGImage = {
    let url = URL(fileURLWithPath: ProcessInfo.processInfo.environment["THUMB_FRAME"]!)
    guard let src = CGImageSourceCreateWithURL(url as CFURL, nil), let img = CGImageSourceCreateImageAtIndex(src, 0, nil) else { fatalError("cannot read \(url.path)") }
    return img
}()
do {
    let p = Picture(1280, 720)
    // the footage, a little zoomed and moved to keep the lemmings and the exit clear of the sign
    let zoom: CGFloat = 1.2, fw = 1280 * zoom, fh = 720 * zoom
    p.ctx.draw(frame, in: CGRect(x: -60, y: p.y(-110, fh), width: fw, height: fh))
    // dark at the top for the title, and round the edges
    let top = CGGradient(colorsSpace: cs, colors: [rgb(0, 0, 0, 0.82), rgb(0, 0, 0, 0.45), rgb(0, 0, 0, 0)] as CFArray, locations: [0, 0.55, 1])!
    p.ctx.drawLinearGradient(top, start: CGPoint(x: 0, y: p.y(0)), end: CGPoint(x: 0, y: p.y(300)), options: [])
    let c = CGPoint(x: 640, y: p.y(420))
    let dark = CGGradient(colorsSpace: cs, colors: [rgb(0, 0, 0, 0), rgb(0, 0, 0, 0.6)] as CFArray, locations: [0, 1])!
    p.ctx.drawRadialGradient(dark, startCenter: c, startRadius: 380, endCenter: c, endRadius: 860, options: [.drawsAfterEndLocation])

    // a soft shade right behind the title so the green holds over the gold
    let tc = CGPoint(x: 640, y: p.y(130))
    let shade = CGGradient(colorsSpace: cs, colors: [rgb(0, 0, 0, 0.6), rgb(0, 0, 0, 0)] as CFArray, locations: [0, 1])!
    p.ctx.saveGState()
    p.ctx.scaleBy(x: 1, y: 0.4); p.ctx.translateBy(x: 0, y: tc.y / 0.4 - tc.y)
    p.ctx.drawRadialGradient(shade, startCenter: tc, startRadius: 0, endCenter: tc, endRadius: 620, options: [])
    p.ctx.restoreGState()
    titleBlock(p, logoWidth: 900, centreY: 132)

    // the lemming and its sign, crisp, with a shadow to lift it off the footage
    let k: CGFloat = 2.9, sw = CGFloat(signGroup.width) * k, sh = CGFloat(signGroup.height) * k
    let sx = 1280 - sw - 34, st = 720 - sh - 46
    p.ctx.saveGState()
    p.ctx.setShadow(offset: CGSize(width: 8, height: -10), blur: 18, color: rgb(0, 0, 0, 0.75))
    p.ctx.interpolationQuality = .none
    p.ctx.draw(signGroup, in: CGRect(x: sx, y: p.y(st, sh), width: sw, height: sh))
    p.ctx.restoreGState()
    p.ctx.interpolationQuality = .high
    // (the board's panel: 72..108 x 38..64 of the 120 x 87 sign)
    drawSubtitle(p, size: 96 * k / 4, baseline: st + 64 * k - 16 * k / 4, text: "VR", centreX: sx + 72 * k)
    p.save("thumbnail.png")
}
