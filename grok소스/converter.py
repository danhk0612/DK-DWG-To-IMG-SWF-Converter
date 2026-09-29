# -*- coding: utf-8 -*-
"""DWG -> PNG / SVG / SWF conversion core.

Pipeline: DWG --(LibreDWG dwg2dxf)--> DXF --(ezdxf drawing add-on)--> recording
          recording -> PNG (matplotlib Agg), SVG (ezdxf SVGBackend), SWF (own writer)
Stateless per call: every conversion creates its own temp dir / figure, so it can be
run any number of times in the same process.
"""
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


def _setup_fonts():
    """Prefer Korean-capable fonts for fallback; bundle DejaVuSans as last resort."""
    try:
        from ezdxf.fonts import font_manager as fm_mod, fonts
        # order: Malgun Gothic (Windows Korean) first, then ezdxf defaults
        for i, name in enumerate(("malgun.ttf", "NanumGothic.ttf")):
            if name not in fm_mod.DEFAULT_FONTS:
                fm_mod.DEFAULT_FONTS.insert(i, name)
        bundled = os.path.join(_base_dir(), "fonts")
        if os.path.isdir(bundled):
            fonts.font_manager.scan_folder(__import__("pathlib").Path(bundled))
        fonts.font_manager._fallback_font_name = ""
    except Exception:  # noqa: BLE001
        pass

class ConversionError(Exception):
    pass


# --------------------------------------------------------------------------- dwg2dxf
def _base_dir():
    if getattr(sys, "frozen", False):
        return getattr(sys, "_MEIPASS", os.path.dirname(sys.executable))
    return os.path.dirname(os.path.abspath(__file__))


def find_dwg2dxf():
    """Return the command (list) used to run dwg2dxf."""
    env = os.environ.get("DWG2DXF")
    if env:
        return env.split("|")
    base = _base_dir()
    cands = [os.path.join(base, "libredwg", "dwg2dxf.exe"),
             os.path.join(base, "..", "vendor", "libredwg-win64", "dwg2dxf.exe")]
    if os.name == "nt":
        for c in cands:
            if os.path.isfile(c):
                return [os.path.abspath(c)]
    else:
        native = shutil.which("dwg2dxf")
        if native:
            return [native]
        wine = shutil.which("wine")
        for c in cands:
            if wine and os.path.isfile(c):
                return [wine, os.path.abspath(c)]
    raise ConversionError("dwg2dxf(LibreDWG)를 찾을 수 없습니다.")


_setup_fonts()


def dwg_to_dxf(dwg_path, dxf_path, timeout=300):
    cmd = find_dwg2dxf()
    src, dst = os.path.abspath(dwg_path), os.path.abspath(dxf_path)
    if cmd[0].endswith("wine"):  # Linux test path: map to Z: drive
        src, dst = "Z:" + src.replace("/", "\\"), "Z:" + dst.replace("/", "\\")
    kwargs = {}
    if os.name == "nt":
        kwargs["creationflags"] = 0x08000000  # CREATE_NO_WINDOW
    try:
        proc = subprocess.run(cmd + ["-y", "-o", dst, src], capture_output=True,
                              timeout=timeout, stdin=subprocess.DEVNULL, **kwargs)
    except subprocess.TimeoutExpired:
        raise ConversionError("DWG 해석 시간 초과 (%d초)" % timeout)
    if not os.path.isfile(dxf_path) or os.path.getsize(dxf_path) == 0:
        err = (proc.stderr or b"").decode("utf-8", "replace").strip().splitlines()
        err = [e for e in err if e.startswith("ERROR") and "Failed to decode file" not in e]
        err = [re.sub(r"magic: .*", "magic 불일치 (DWG 파일이 아님)", e) for e in err]
        tail = " / ".join(err[:2]) if err else "exit code %s" % proc.returncode
        raise ConversionError("DWG→DXF 변환 실패 (지원되지 않거나 손상된 DWG일 수 있음): " + tail)


def load_dxf(dxf_path):
    try:
        doc, auditor = recover.readfile(dxf_path)
    except Exception as e:  # noqa: BLE001
        raise ConversionError("DXF 읽기 실패: %s" % e)
    return doc


# --------------------------------------------------------------------------- render
def _config(dark_bg=False):
    return Configuration(background_policy=BackgroundPolicy.BLACK if dark_bg else BackgroundPolicy.WHITE,
                         lineweight_policy=LineweightPolicy.ABSOLUTE)


INFINITE_TYPES = {"XLINE", "RAY"}


def _bw_override(dark_bg):
    """ACI 7 (black/white) follows the background, like CAD viewers."""
    from ezdxf.addons.drawing.recorder import Override

    def ov(props):
        c = (props.color or "")[:7].lower()
        if dark_bg and c == "#000000":
            props = props._replace(color="#ffffff" + props.color[7:])
        elif not dark_bg and c == "#ffffff":
            props = props._replace(color="#000000" + props.color[7:])
        return Override(props, True)
    return ov


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


def write_png(player, out_path, max_px=2000, dark_bg=False):
    bbox = _ext(player)
    _, w, h = _fit(bbox, max_px - 20)
    dpi = 100
    fig = Figure(figsize=((w + 20) / dpi, (h + 20) / dpi), dpi=dpi)
    FigureCanvasAgg(fig)
    ax = fig.add_axes([0, 0, 1, 1])
    try:
        bg = "#000000" if dark_bg else "#ffffff"
        fig.set_facecolor(bg)
        ax.set_facecolor(bg)
        player.replay(MatplotlibBackend(ax, adjust_figure=False), override=_bw_override(dark_bg))
        fig.set_size_inches((w + 20) / dpi, (h + 20) / dpi)
        ax.set_aspect("equal")
        mx, my = bbox.size.x * 0.01 + 1e-9, bbox.size.y * 0.01 + 1e-9
        ax.set_xlim(bbox.extmin.x - mx, bbox.extmax.x + mx)
        ax.set_ylim(bbox.extmin.y - my, bbox.extmax.y + my)
        ax.axis("off")
        fig.savefig(out_path, dpi=dpi, facecolor=bg)
    finally:
        fig.clf()


def write_svg(player, out_path, dark_bg=False):
    backend = svg.SVGBackend()
    player.replay(backend, override=_bw_override(dark_bg))
    page = layout.Page(0, 0, layout.Units.mm, margins=layout.Margins.all(2))
    s = backend.get_string(page)
    with open(out_path, "w", encoding="utf-8") as f:
        f.write(s)


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


# --------------------------------------------------------------------------- public API
def unique_path(path):
    if not os.path.exists(path):
        return path
    base, ext = os.path.splitext(path)
    i = 1
    while os.path.exists("%s (%d)%s" % (base, i, ext)):
        i += 1
    return "%s (%d)%s" % (base, i, ext)


def _ascii_tempdir():
    """dwg2dxf.exe uses ANSI argv: make sure the work dir path is pure ASCII
    (Windows %TEMP% contains the user name, which may be Korean)."""
    base = tempfile.gettempdir()
    try:
        base.encode("ascii")
    except UnicodeEncodeError:
        for cand in (os.path.join(os.environ.get("SystemDrive", "C:") + os.sep, "Temp"),
                     os.path.join(os.environ.get("PUBLIC", ""), "Documents"),
                     os.environ.get("ProgramData", "")):
            try:
                cand.encode("ascii")
                os.makedirs(cand, exist_ok=True)
                return tempfile.mkdtemp(prefix="dwgconv_", dir=cand)
            except Exception:  # noqa: BLE001
                continue
    return tempfile.mkdtemp(prefix="dwgconv_", dir=base)


def convert_file(dwg_path, out_dir, formats, overwrite=True, log=print, png_px=2000,
                 dark_bg=False):
    """Convert one DWG. Returns list of written files. Raises ConversionError."""
    formats = [f for f in formats if f in FORMATS]
    if not formats:
        raise ConversionError("출력 형식을 하나 이상 선택하세요.")
    os.makedirs(out_dir, exist_ok=True)
    name = os.path.splitext(os.path.basename(dwg_path))[0]
    tmp = _ascii_tempdir()
    written = []
    try:
        if dwg_path.lower().endswith(".dxf"):
            dxf = dwg_path
        else:
            src = os.path.join(tmp, "in.dwg")   # ASCII-only copy (Korean file names)
            shutil.copyfile(dwg_path, src)
            dxf = os.path.join(tmp, "in.dxf")
            dwg_to_dxf(src, dxf)
        doc = load_dxf(dxf)
        player = record(doc, dark_bg)
        for fmt in formats:
            out = os.path.join(out_dir, "%s.%s" % (name, fmt))
            if not overwrite:
                out = unique_path(out)
            try:
                pl = copy.copy(player)  # backends may transform records in place
                if fmt == "png":
                    write_png(pl, out, png_px, dark_bg)
                elif fmt == "svg":
                    write_svg(pl, out, dark_bg)
                elif fmt == "swf":
                    write_swf(pl, out, dark_bg=dark_bg)
                written.append(out)
            except Exception as e:  # noqa: BLE001
                log("  [%s] 저장 실패: %s" % (fmt.upper(), e))
        if not written:
            raise ConversionError("모든 형식 저장에 실패했습니다.")
        return written
    finally:
        shutil.rmtree(tmp, ignore_errors=True)


def collect_dwgs(paths, recursive=True):
    out = []
    for p in paths:
        if os.path.isdir(p):
            for root, dirs, files in os.walk(p):
                for f in sorted(files):
                    if f.lower().endswith(".dwg"):
                        out.append(os.path.join(root, f))
                if not recursive:
                    break
        elif os.path.isfile(p) and p.lower().endswith((".dwg", ".dxf")):
            out.append(p)
    seen, uniq = set(), []
    for p in out:
        k = os.path.normcase(os.path.abspath(p))
        if k not in seen:
            seen.add(k)
            uniq.append(p)
    return uniq
