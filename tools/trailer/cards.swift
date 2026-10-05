// cards.swift - the trailer's captions and end card, appended to make-art.swift's drawing code
// (its Picture, dirt, logo and slab subtitle) by cards.sh. Captions are transparent 1920 x 1080
// pictures laid over the footage: yellow slab letters, the logo's green outline, a soft shadow.

// a caption: one or two lines, centred across, the last baseline at `baseline`
func caption(_ name: String, _ lines: [String], size: CGFloat = 92, baseline: CGFloat = 960) {
    let p = Picture(1920, 1080)
    // a dark band behind the letters so they hold over a bright board
    let band = CGGradient(colorsSpace: cs, colors: [rgb(0, 0, 0, 0), rgb(0, 0, 0, 0.72), rgb(0, 0, 0, 0)] as CFArray, locations: [0, 0.5, 1])!
    let top = baseline - size * (1.25 * CGFloat(lines.count) + 0.2), bottom = baseline + size * 0.75
    p.ctx.drawLinearGradient(band, start: CGPoint(x: 0, y: p.y(top)), end: CGPoint(x: 0, y: p.y(bottom)), options: [])
    for (i, line) in lines.enumerated() {
        drawSubtitle(p, size: size, baseline: baseline - CGFloat(lines.count - 1 - i) * size * 1.25, text: line)
    }
    p.save(name)
}

caption("save-them.png", ["SAVE THE LEMMINGS"])
caption("dig.png", ["DIG"], size: 120)
caption("build.png", ["BUILD"], size: 120)
caption("bash.png", ["BASH"], size: 120)
caption("boom.png", ["...OR BLOW IT ALL UP"], size: 100)
caption("float.png", ["FLOAT, GLIDE, SWIM, JUMP"])
caption("diorama.png", ["EVERY LEVEL A DIORAMA", "IN YOUR ROOM"], size: 80)
caption("levels.png", ["OVER 1,000 LEVELS"])
caption("point.png", ["POINT. ASSIGN. SAVE THEM ALL."], size: 80)

// the end card: the title on the dirt, the levels' sources under it
do {
    let p = Picture(1920, 1080)
    dirt(p, tile: 1.5, glow: CGPoint(x: 960, y: 470), glowRadius: 760)
    titleBlock(p, logoWidth: 1400, centreY: 450)
    let note = "LEVELS FROM LEMMINGS REDUX, LEMMINGS PLUS AND THE NEOLEMMIX INTRODUCTION PACK"
    let f = CTFontCreateWithName("Avenir-Heavy" as CFString, 26, nil)
    let attrs: [NSAttributedString.Key: Any] = [.init(kCTFontAttributeName as String): f, .init(kCTKernAttributeName as String): 2.0,
                                                 .init(kCTForegroundColorFromContextAttributeName as String): true]
    let line = CTLineCreateWithAttributedString(NSAttributedString(string: note, attributes: attrs))
    p.ctx.textPosition = .zero
    let b = CTLineGetImageBounds(line, p.ctx)
    p.ctx.setTextDrawingMode(.fill); p.ctx.setFillColor(rgb(235, 220, 190, 0.85))
    p.ctx.textPosition = CGPoint(x: 960 - b.width / 2 - b.minX, y: p.y(900))
    CTLineDraw(line, p.ctx)
    p.save("end.png")
}
