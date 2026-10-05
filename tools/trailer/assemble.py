#!/usr/bin/env python3
"""assemble.py <edit.json> <out.mp4> - the trailer cut from its edit list.

The edit list names the shots (record.sh's clips), the stills (the splash, the end card) and the
captions (cards.sh's transparent pictures) in order:

    {"clips": dir, "cards": dir, "music": wav, "xfade": 0.4, "sfx": 0.55,
     "segments": [{"image": "splash.png", "dur": 4.5},
                  {"clip": "01-cindy-open", "from": 0.5, "dur": 7, "caption": "save-them.png",
                   "cap": [1.0, 6.0], "cut": true}, ...]}

Each segment is made 1920x1080 at 30 fps with stereo sound (a still: a slow push in, silence),
its game sound at `vol` (1), its caption faded in and out over [cap] seconds (none at 0 or at the end, so a caption runs on
over hard cuts), then the segments are joined by crossfades of
`xfade` seconds (a segment with "cut": true joins its predecessor by a hard cut). The game's sounds
go under the music bed at `sfx`, the music fades out at the end, and the mix is normalised to -14
LUFS. Needs ffmpeg.
"""
import json
import os
import subprocess
import sys
import tempfile

W, H, FPS = 1920, 1080, 30


def run(args):
    r = subprocess.run(["ffmpeg", "-hide_banner", "-loglevel", "error", "-y", *args])
    if r.returncode:
        sys.exit(f"ffmpeg failed: {' '.join(args)}")


def segment(seg, edit, base, out):
    dur = seg["dur"]
    inputs, chain = [], []
    if "image" in seg:
        img = os.path.join(edit["cards"] if not os.path.isabs(seg["image"]) else "", seg["image"])
        inputs += ["-loop", "1", "-framerate", str(FPS), "-t", str(dur), "-i", img]
        # a slow push in, 4% over the segment, from a 2x canvas so the motion stays smooth
        z = seg.get("zoom", 0.04)
        frames = int(dur * FPS)
        chain.append(f"[0:v]scale={W*2}:{H*2},zoompan=z='1+{z}*on/{frames}':x='iw/2-(iw/zoom/2)':y='ih/2-(ih/zoom/2)'"
                     f":d=1:s={W}x{H}:fps={FPS},format=yuv420p[v0]")
        inputs += ["-f", "lavfi", "-t", str(dur), "-i", "anullsrc=r=48000:cl=stereo"]
        audio = "[1:a]"
        nxt = 2
    else:
        clip = os.path.join(edit["clips"], seg["clip"] + ".mp4")
        inputs += ["-ss", str(seg.get("from", 0)), "-t", str(dur), "-i", clip]
        chain.append(f"[0:v]scale={W}:{H},fps={FPS},format=yuv420p[v0]")
        audio = "[0:a]"
        nxt = 1
    v = "[v0]"
    if "caption" in seg:
        a, b = seg.get("cap", [0.4, dur - 0.4])
        inputs += ["-loop", "1", "-framerate", str(FPS), "-t", str(dur), "-i", os.path.join(edit["cards"], seg["caption"])]
        # (a caption from 0 or to the end has no fade there: it carries on across hard cuts)
        fades = ([f"fade=t=in:st={a}:d=0.35:alpha=1"] if a > 0 else []) + ([f"fade=t=out:st={b - 0.35}:d=0.35:alpha=1"] if b < dur else [])
        chain.append(f"[{nxt}:v]" + ",".join(["format=rgba", *fades]) + "[cap]")
        chain.append(f"{v}[cap]overlay=0:0:format=auto,format=yuv420p[v1]")
        v = "[v1]"
    chain.append(f"{audio}aresample=48000,aformat=channel_layouts=stereo,volume={seg.get('vol', 1)},apad,atrim=0:{dur}[a0]")
    run([*inputs, "-filter_complex", ";".join(chain), "-map", v, "-map", "[a0]", "-t", str(dur),
         "-c:v", "libx264", "-crf", "12", "-preset", "fast", "-c:a", "pcm_s16le", out])


def main():
    edit_path, out = sys.argv[1], sys.argv[2]
    edit = json.load(open(edit_path))
    base = os.path.dirname(os.path.abspath(edit_path))
    for k in ("clips", "cards", "music"):
        edit[k] = os.path.join(base, edit[k])
    xf = edit.get("xfade", 0.4)
    segs = edit["segments"]
    tmp = tempfile.mkdtemp(prefix="trailer-")
    parts = []
    for i, s in enumerate(segs):
        p = os.path.join(tmp, f"seg{i:02d}.mov")
        segment(s, edit, base, p)
        parts.append(p)
        print(f"[assemble] {i:2d} {s.get('clip') or s.get('image')} {s['dur']}s")

    # the joins: a crossfade (or a cut) per boundary, the offsets running on
    inputs, vchain, achain = [], [], []
    for p in parts:
        inputs += ["-i", p]
    # (one time base for all: xfade refuses a concat's output otherwise)
    for i in range(len(parts)):
        vchain.append(f"[{i}:v]settb=AVTB,setpts=PTS-STARTPTS[s{i}]")
    v, a, t = "[s0]", "[0:a]", segs[0]["dur"]
    for i in range(1, len(parts)):
        d = 0.0 if segs[i].get("cut") else xf
        if d > 0:
            vchain.append(f"{v}[s{i}]xfade=transition=fade:duration={d}:offset={t - d:.3f}[v{i}]")
            achain.append(f"{a}[{i}:a]acrossfade=d={d}[a{i}]")
        else:
            vchain.append(f"{v}[s{i}]concat=n=2:v=1:a=0,settb=AVTB[v{i}]")
            achain.append(f"{a}[{i}:a]concat=n=2:v=0:a=1[a{i}]")
        v, a = f"[v{i}]", f"[a{i}]"
        t += segs[i]["dur"] - d
    total = t
    # the music under it all, in at the start, out over the last seconds; the game's sounds on top
    mi = len(parts)
    inputs += ["-i", edit["music"]]
    fade_out = edit.get("music_fade", 3.5)
    achain.append(f"[{mi}:a]aresample=48000,atrim=0:{total},afade=t=in:d=0.3,afade=t=out:st={total - fade_out}:d={fade_out},"
                  f"volume={edit.get('music_vol', 0.8)}[mus]")
    achain.append(f"{a}volume={edit.get('sfx', 0.55)}[sfx]")
    achain.append("[mus][sfx]amix=inputs=2:duration=first:normalize=0,loudnorm=I=-14:TP=-1.5:LRA=11[aout]")
    vchain.append(f"{v}fade=t=in:d=0.6,fade=t=out:st={total - 1.2:.3f}:d=1.2[vout]")
    run([*inputs, "-filter_complex", ";".join(vchain + achain), "-map", "[vout]", "-map", "[aout]",
         "-c:v", "libx264", "-crf", "19", "-preset", "slow", "-pix_fmt", "yuv420p", "-profile:v", "high",
         "-c:a", "aac", "-b:a", "192k", "-ar", "48000", "-movflags", "+faststart", "-t", f"{total:.3f}", out])
    for p in parts:
        os.remove(p)
    os.rmdir(tmp)
    print(f"[assemble] {out}: {total:.1f}s")


if __name__ == "__main__":
    main()
