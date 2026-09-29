# converter.py 에서 SWF 관련 부분 발췌 (원본 그대로, 줄 번호는 converter.py 기준)
# --- lines 9-34: imports ---
import copy
import os
import re
import shutil
import subprocess
import sys
import tempfile

import matplotlib
matplotlib.use("Agg")
from matplotlib.figure import Figure  # noqa: E402
from matplotlib.backends.backend_agg import FigureCanvasAgg  # noqa: E402

import ezdxf  # noqa: E402
from ezdxf import recover  # noqa: E402
from ezdxf.addons.drawing import Frontend, RenderContext, layout, svg  # noqa: E402
from ezdxf.addons.drawing.config import (  # noqa: E402
    BackgroundPolicy, Configuration, LineweightPolicy)
from ezdxf.addons.drawing.matplotlib import MatplotlibBackend  # noqa: E402
from ezdxf.addons.drawing.recorder import (  # noqa: E402
    FilledPathsRecord, ImageRecord, PathRecord, PointsRecord, Recorder,
    SolidLinesRecord)

from swf_writer import SWFDocument  # noqa: E402

FORMATS = ("png", "svg", "swf")

# --- lines 141-184: record / _ext / _fit ---
def record(doc, dark_bg=False):
    msp = doc.modelspace()
    ctx = RenderContext(doc)
    rec = Recorder()
    Frontend(ctx, rec, config=_config(dark_bg)).draw_layout(msp, finalize=True)
    player = rec.player()
    # Infinite construction lines (XLINE/RAY) would blow up the extents:
    # compute extents from everything else and crop to that (+5%).
    inf = {e.dxf.handle for e in msp if e.dxftype() in INFINITE_TYPES}
    if inf:
        from ezdxf.math import BoundingBox2d
        bb = BoundingBox2d()
        for r in player.records:
            if r.handle not in inf:
                bb.extend(r.bbox())
        if bb.has_data and (bb.size.x > 0 or bb.size.y > 0):
            m = max(bb.size.x, bb.size.y) * 0.05
            player.crop_rect((bb.extmin.x - m, bb.extmin.y - m),
                             (bb.extmax.x + m, bb.extmax.y + m), m / 100)
    if not player.records or not player.bbox().has_data:
        raise ConversionError("그릴 수 있는 도형이 없습니다 (빈 도면 또는 미지원 개체만 있음).")
    return player


def _ext(player):
    """Drawing extents; degenerate (single point / zero width) extents are padded."""
    from ezdxf.math import BoundingBox2d, Vec2
    bb = player.bbox()
    sx, sy = bb.size.x, bb.size.y
    big = max(sx, sy)
    pad = big * 0.05 if big > 0 else 1.0
    padx = pad if sx < big * 0.01 or big == 0 else 0.0
    pady = pad if sy < big * 0.01 or big == 0 else 0.0
    if padx or pady:
        return BoundingBox2d([bb.extmin - Vec2(padx, pady), bb.extmax + Vec2(padx, pady)])
    return bb


def _fit(bbox, max_px):
    sx, sy = max(bbox.size.x, 1e-9), max(bbox.size.y, 1e-9)
    scale = max_px / max(sx, sy)
    w = max(16, int(round(sx * scale)))
    h = max(16, int(round(sy * scale)))
    return scale, w, h

# --- lines 219-281: _rgba / write_swf ---
_HEX = re.compile(r"#?([0-9a-fA-F]{6})([0-9a-fA-F]{2})?")


def _rgba(color, dark_bg=False):
    m = _HEX.match(color or "#000000")
    if not m:
        return (0, 0, 0, 255)
    v = m.group(1)
    a = int(m.group(2), 16) if m.group(2) else 255
    r, g, b = int(v[0:2], 16), int(v[2:4], 16), int(v[4:6], 16)
    if dark_bg and (r, g, b) == (0, 0, 0):
        r, g, b = 255, 255, 255
    elif not dark_bg and (r, g, b) == (255, 255, 255):
        r, g, b = 0, 0, 0
    return (r, g, b, a)


def write_swf(player, out_path, max_px=1600, dark_bg=False):
    bbox = _ext(player)
    margin = 10
    scale, w, h = _fit(bbox, max_px - 2 * margin)
    W, H = w + 2 * margin, h + 2 * margin
    x0, y1 = bbox.extmin.x, bbox.extmax.y
    flat = max(bbox.size.x, bbox.size.y) / 4000.0 or 0.01

    def tp(v):
        return ((v.x - x0) * scale + margin, (y1 - v.y) * scale + margin)

    def lw(props):
        return max(1.0, min(6.0, props.lineweight / 0.25))

    doc = SWFDocument(W, H, background=(0, 0, 0) if dark_bg else (255, 255, 255))
    for rec, props in player.recordings():
        rgba = _rgba(props.color, dark_bg)
        if isinstance(rec, SolidLinesRecord):
            vs = rec.lines.vertices()
            for i in range(0, len(vs) - 1, 2):
                doc.add_polyline([tp(vs[i]), tp(vs[i + 1])], lw(props), rgba)
        elif isinstance(rec, PathRecord):
            for sp in (rec.path.sub_paths() if rec.path.has_sub_paths else [rec.path]):
                pts = [tp(v) for v in sp.flattening(flat)]
                doc.add_polyline(pts, lw(props), rgba)
        elif isinstance(rec, PointsRecord):
            vs = rec.points.vertices()
            if len(vs) > 2:
                doc.add_fill([[tp(v) for v in vs]], rgba)
            elif len(vs) == 2:
                doc.add_polyline([tp(vs[0]), tp(vs[1])], lw(props), rgba)
            elif len(vs) == 1:
                x, y = tp(vs[0])
                doc.add_polyline([(x, y), (x + 1, y)], lw(props), rgba)
        elif isinstance(rec, FilledPathsRecord):
            rings = []
            for p in rec.paths:
                for sp in (p.sub_paths() if p.has_sub_paths else [p]):
                    rings.append([tp(v) for v in sp.flattening(flat)])
            doc.add_fill(rings, rgba)
        elif isinstance(rec, ImageRecord):
            vs = rec.boundary.vertices()
            if len(vs) >= 2:
                doc.add_polyline([tp(v) for v in vs] + [tp(vs[0])], 1.0, (128, 128, 128, 255))
    with open(out_path, "wb") as f:
        f.write(doc.tobytes())
