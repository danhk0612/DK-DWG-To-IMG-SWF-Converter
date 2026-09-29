# -*- coding: utf-8 -*-
"""Minimal vector SWF writer (DefineShape3 + PlaceObject2), no external deps.

Only straight edges are emitted (curves are flattened by the caller).
Produces SWF version 10, zlib-compressed (CWS) by default.
"""
import struct
import zlib

TWIPS = 20  # 1 px = 20 twips


class BitWriter:
    def __init__(self):
        self.buf = bytearray()
        self.acc = 0
        self.nbits = 0

    def ub(self, value, n):
        if n == 0:
            return
        value &= (1 << n) - 1
        self.acc = (self.acc << n) | value
        self.nbits += n
        while self.nbits >= 8:
            self.nbits -= 8
            self.buf.append((self.acc >> self.nbits) & 0xFF)
        self.acc &= (1 << self.nbits) - 1 if self.nbits else 0

    def sb(self, value, n):
        self.ub(value & ((1 << n) - 1), n)

    def flush(self):
        if self.nbits:
            self.buf.append((self.acc << (8 - self.nbits)) & 0xFF)
            self.acc = 0
            self.nbits = 0
        return bytes(self.buf)


def sbits(*values):
    """Minimum number of bits to hold all values as signed ints."""
    m = 1
    for v in values:
        v = int(v)
        n = (v if v >= 0 else ~v).bit_length() + 1
        if n > m:
            m = n
    return m


def ubits(v):
    return max(1, int(v).bit_length())


def rect(xmin, xmax, ymin, ymax):
    bw = BitWriter()
    n = sbits(xmin, xmax, ymin, ymax)
    bw.ub(n, 5)
    for v in (xmin, xmax, ymin, ymax):
        bw.sb(v, n)
    return bw.flush()


def tag(code, data):
    if len(data) < 0x3F:
        return struct.pack("<H", (code << 6) | len(data)) + data
    return struct.pack("<HI", (code << 6) | 0x3F, len(data)) + data


def _style_array(styles, kind):
    out = bytearray()
    n = len(styles)
    if n < 0xFF:
        out.append(n)
    else:
        out.append(0xFF)
        out += struct.pack("<H", n)
    for s in styles:
        if kind == "fill":
            r, g, b, a = s
            out += bytes((0x00, r, g, b, a))
        else:
            width, (r, g, b, a) = s
            out += struct.pack("<H", width) + bytes((r, g, b, a))
    return bytes(out)


class SWFShape:
    """Collects polylines / filled polygons for one DefineShape3."""

    def __init__(self):
        self.line_styles = []   # list of (width_twips, rgba)
        self.fill_styles = []   # list of rgba
        self._ls_index = {}
        self._fs_index = {}
        self.items = []         # (kind, style_idx(1-based), [(x,y)...] twips)
        self.edges = 0

    def _ls(self, width, rgba):
        key = (width, rgba)
        if key not in self._ls_index:
            self.line_styles.append(key)
            self._ls_index[key] = len(self.line_styles)
        return self._ls_index[key]

    def _fs(self, rgba):
        if rgba not in self._fs_index:
            self.fill_styles.append(rgba)
            self._fs_index[rgba] = len(self.fill_styles)
        return self._fs_index[rgba]

    def add_polyline(self, pts, width_twips, rgba):
        if len(pts) < 2:
            return
        self.items.append(("line", self._ls(width_twips, rgba), pts))
        self.edges += len(pts)

    def add_fill(self, rings, rgba):
        rings = [r for r in rings if len(r) >= 3]
        if not rings:
            return
        idx = self._fs(rgba)
        for r in rings:
            self.items.append(("fill", idx, r))
            self.edges += len(r) + 1

    def bounds(self):
        xs, ys = [], []
        for _, _, pts in self.items:
            for x, y in pts:
                xs.append(x)
                ys.append(y)
        if not xs:
            return (0, 20, 0, 20)
        pad = max(w for w, _ in self.line_styles) if self.line_styles else 0
        return (min(xs) - pad, max(xs) + pad, min(ys) - pad, max(ys) + pad)

    def encode(self, shape_id):
        nfb = ubits(len(self.fill_styles)) if self.fill_styles else 0
        nlb = ubits(len(self.line_styles)) if self.line_styles else 0
        head = bytearray(struct.pack("<H", shape_id))
        head += rect(*self.bounds())
        head += _style_array(self.fill_styles, "fill")
        head += _style_array(self.line_styles, "line")
        bw = BitWriter()
        bw.ub(nfb, 4)
        bw.ub(nlb, 4)
        cur_ls = cur_fs = 0
        cx = cy = 0
        # Fills first so lines draw on top.
        ordered = [i for i in self.items if i[0] == "fill"] + [i for i in self.items if i[0] == "line"]
        for kind, idx, pts in ordered:
            want_ls = idx if kind == "line" else 0
            want_fs = idx if kind == "fill" else 0
            x0, y0 = pts[0]
            # StyleChangeRecord
            bw.ub(0, 1)                       # TypeFlag
            bw.ub(0, 1)                       # StateNewStyles
            bw.ub(1 if want_ls != cur_ls else 0, 1)
            bw.ub(0, 1)                       # StateFillStyle1
            bw.ub(1 if want_fs != cur_fs else 0, 1)
            bw.ub(1, 1)                       # StateMoveTo
            mb = sbits(x0, y0)
            bw.ub(mb, 5)
            bw.sb(x0, mb)
            bw.sb(y0, mb)
            if want_fs != cur_fs:
                bw.ub(want_fs, nfb)
                cur_fs = want_fs
            if want_ls != cur_ls:
                bw.ub(want_ls, nlb)
                cur_ls = want_ls
            cx, cy = x0, y0
            seq = pts[1:] + ([pts[0]] if kind == "fill" and pts[-1] != pts[0] else [])
            for x, y in seq:
                dx, dy = x - cx, y - cy
                if dx == 0 and dy == 0:
                    continue
                n = max(2, sbits(dx, dy))
                if n > 17:  # split long edges
                    steps = (max(abs(dx), abs(dy)) // 60000) + 1
                    for k in range(1, steps + 1):
                        tx = cx + (x - cx) * 1 // 1 if False else None
                    sx, sy = cx, cy
                    for k in range(1, steps + 1):
                        nx = sx + (dx * k) // steps
                        ny = sy + (dy * k) // steps
                        self._edge(bw, nx - cx, ny - cy)
                        cx, cy = nx, ny
                    continue
                self._edge(bw, dx, dy)
                cx, cy = x, y
        bw.ub(0, 6)  # EndShapeRecord
        return tag(32, bytes(head) + bw.flush())

    @staticmethod
    def _edge(bw, dx, dy):
        if dx == 0 and dy == 0:
            return
        n = max(2, sbits(dx, dy))
        bw.ub(1, 1)          # TypeFlag (edge)
        bw.ub(1, 1)          # StraightFlag
        bw.ub(n - 2, 4)
        if dx != 0 and dy != 0:
            bw.ub(1, 1)      # GeneralLineFlag
            bw.sb(dx, n)
            bw.sb(dy, n)
        else:
            bw.ub(0, 1)
            if dx == 0:
                bw.ub(1, 1)  # vertical
                bw.sb(dy, n)
            else:
                bw.ub(0, 1)
                bw.sb(dx, n)


class SWFDocument:
    MAX_EDGES_PER_SHAPE = 20000

    def __init__(self, width_px, height_px, background=(255, 255, 255), frame_rate=12):
        self.w = int(width_px)
        self.h = int(height_px)
        self.bg = background
        self.fps = frame_rate
        self.shapes = [SWFShape()]

    def _cur(self):
        if self.shapes[-1].edges > self.MAX_EDGES_PER_SHAPE:
            self.shapes.append(SWFShape())
        return self.shapes[-1]

    def add_polyline(self, pts_px, width_px, rgba):
        pts = _to_twips(pts_px)
        if len(pts) >= 2:
            self._cur().add_polyline(pts, max(1, int(round(width_px * TWIPS))), rgba)

    def add_fill(self, rings_px, rgba):
        rings = [_to_twips(r) for r in rings_px]
        self._cur().add_fill(rings, rgba)

    def tobytes(self, compress=True, version=10):
        body = bytearray()
        body += rect(0, self.w * TWIPS, 0, self.h * TWIPS)
        body += struct.pack("<HH", self.fps << 8, 1)
        body += tag(69, struct.pack("<I", 0))                  # FileAttributes
        body += tag(9, bytes(self.bg[:3]))                      # SetBackgroundColor
        depth = 1
        for i, sh in enumerate(self.shapes):
            if not sh.items:
                continue
            cid = i + 1
            body += sh.encode(cid)
            body += tag(26, struct.pack("<BHH", 0x02, depth, cid))  # PlaceObject2
            depth += 1
        body += tag(1, b"")   # ShowFrame
        body += tag(0, b"")   # End
        total = 8 + len(body)
        if compress:
            return b"CWS" + bytes([version]) + struct.pack("<I", total) + zlib.compress(bytes(body), 9)
        return b"FWS" + bytes([version]) + struct.pack("<I", total) + bytes(body)


def _to_twips(pts):
    out = []
    last = None
    for x, y in pts:
        p = (int(round(x * TWIPS)), int(round(y * TWIPS)))
        if p != last:
            out.append(p)
            last = p
    return out
