using System.IO.Compression;
using System.Text;

namespace DwgToPngPoC;

/// <summary>
/// Minimal vector SWF writer using DefineShape3 + PlaceObject2.
/// Curves must be flattened by the caller. Coordinates passed here are pixels.
/// </summary>
internal sealed class DirectSwfDocument
{
    private const int TwipsPerPixel = 20;
    private const int MaxEdgesPerShape = 20_000;

    private readonly int _width;
    private readonly int _height;
    private readonly SwfRgba _background;
    private readonly int _frameRate;
    private readonly List<DirectSwfShape> _backShapes = [new DirectSwfShape()];
    private readonly List<DirectSwfShape> _shapes = [new DirectSwfShape()];

    public DirectSwfDocument(int widthPx, int heightPx, SwfRgba background, int frameRate = 12)
    {
        _width = Math.Max(1, widthPx);
        _height = Math.Max(1, heightPx);
        _background = background;
        _frameRate = Math.Clamp(frameRate, 1, 120);
    }

    public int ShapeCount => _backShapes.Count(s => s.HasItems) + _shapes.Count(s => s.HasItems);
    public int EdgeCount => _backShapes.Sum(s => s.EdgeCount) + _shapes.Sum(s => s.EdgeCount);

    public bool TryGetContentBoundsPx(out SwfGeometryBounds bounds)
    {
        var shapes = _backShapes.Concat(_shapes).Where(s => s.HasItems).ToArray();
        var xmin = int.MaxValue;
        var xmax = int.MinValue;
        var ymin = int.MaxValue;
        var ymax = int.MinValue;

        foreach (var shape in shapes)
        {
            if (!shape.TryGetGeometryBounds(out var b))
                continue;
            xmin = Math.Min(xmin, b.XMin);
            xmax = Math.Max(xmax, b.XMax);
            ymin = Math.Min(ymin, b.YMin);
            ymax = Math.Max(ymax, b.YMax);
        }

        if (xmin == int.MaxValue || xmax <= xmin || ymax <= ymin)
        {
            bounds = default;
            return false;
        }

        bounds = new SwfGeometryBounds(
            xmin / (double)TwipsPerPixel,
            ymin / (double)TwipsPerPixel,
            (xmax - xmin) / (double)TwipsPerPixel,
            (ymax - ymin) / (double)TwipsPerPixel);
        return true;
    }

    public void AddPolyline(IReadOnlyList<SwfPoint> pointsPx, double widthPx, SwfRgba color)
    {
        var points = ToTwips(pointsPx);
        if (points.Count < 2)
            return;

        ShapeForIncoming(_shapes, points.Count).AddPolyline(
            points, Math.Max(1, (int)Math.Round(widthPx * TwipsPerPixel)), color);
    }

    public void AddFill(IEnumerable<IReadOnlyList<SwfPoint>> ringsPx, SwfRgba color)
    {
        AddFillCore(_shapes, ringsPx, color);
    }

    /// <summary>
    /// Adds a fill behind every normal shape. This is used for reconstructed closed regions
    /// discovered after all CAD linework has already been scanned.
    /// </summary>
    public void AddBackFill(IEnumerable<IReadOnlyList<SwfPoint>> ringsPx, SwfRgba color)
    {
        AddFillCore(_backShapes, ringsPx, color);
    }

    /// <summary>
    /// Fits the actual generated vector bounds to the exact requested content rectangle,
    /// then positions that rectangle inside the SWF workspace. Geometry is scaled after
    /// collection, so DWG header EXTMIN/EXTMAX outliers do not control final placement.
    /// </summary>
    public SwfContentFitResult FitContentToWorkspace(
        int contentWidthPx,
        int contentHeightPx,
        HorizontalPlacement horizontalPlacement,
        VerticalPlacement verticalPlacement,
        ContentFitMode fitMode)
    {
        var shapes = _backShapes.Concat(_shapes).Where(s => s.HasItems).ToArray();
        if (shapes.Length == 0)
            return new SwfContentFitResult(false, 0, 0, 0, 0, 0, 0, 0, 0);

        var xmin = int.MaxValue;
        var xmax = int.MinValue;
        var ymin = int.MaxValue;
        var ymax = int.MinValue;
        foreach (var shape in shapes)
        {
            if (!shape.TryGetGeometryBounds(out var b))
                continue;
            xmin = Math.Min(xmin, b.XMin);
            xmax = Math.Max(xmax, b.XMax);
            ymin = Math.Min(ymin, b.YMin);
            ymax = Math.Max(ymax, b.YMax);
        }

        if (xmin == int.MaxValue || xmax <= xmin || ymax <= ymin)
            return new SwfContentFitResult(false, 0, 0, 0, 0, 0, 0, 0, 0);

        var sourceWidth = xmax - (double)xmin;
        var sourceHeight = ymax - (double)ymin;
        var maxWidth = Math.Max(1, contentWidthPx) * (double)TwipsPerPixel;
        var maxHeight = Math.Max(1, contentHeightPx) * (double)TwipsPerPixel;

        var fitScaleX = maxWidth / sourceWidth;
        var fitScaleY = maxHeight / sourceHeight;
        var (scaleX, scaleY) = fitMode switch
        {
            ContentFitMode.Fill => (Math.Max(fitScaleX, fitScaleY), Math.Max(fitScaleX, fitScaleY)),
            ContentFitMode.Stretch => (fitScaleX, fitScaleY),
            _ => (Math.Min(fitScaleX, fitScaleY), Math.Min(fitScaleX, fitScaleY))
        };

        if (!double.IsFinite(scaleX) || !double.IsFinite(scaleY) || scaleX <= 0 || scaleY <= 0)
            return new SwfContentFitResult(false, 0, 0, 0, 0, 0, 0, 0, 0);

        var actualWidth = sourceWidth * scaleX;
        var actualHeight = sourceHeight * scaleY;
        var workspaceWidth = _width * (double)TwipsPerPixel;
        var workspaceHeight = _height * (double)TwipsPerPixel;
        var freeX = workspaceWidth - actualWidth;
        var freeY = workspaceHeight - actualHeight;

        var targetX = horizontalPlacement switch
        {
            HorizontalPlacement.Left => 0.0,
            HorizontalPlacement.Right => freeX,
            _ => freeX / 2.0
        };
        var targetY = verticalPlacement switch
        {
            VerticalPlacement.Top => 0.0,
            VerticalPlacement.Bottom => freeY,
            _ => freeY / 2.0
        };

        foreach (var shape in shapes)
            shape.TransformGeometry(xmin, ymin, scaleX, scaleY, targetX, targetY);

        return new SwfContentFitResult(
            true,
            sourceWidth / TwipsPerPixel,
            sourceHeight / TwipsPerPixel,
            targetX / TwipsPerPixel,
            targetY / TwipsPerPixel,
            actualWidth / TwipsPerPixel,
            actualHeight / TwipsPerPixel,
            scaleX,
            scaleY);
    }

    private static void AddFillCore(List<DirectSwfShape> target, IEnumerable<IReadOnlyList<SwfPoint>> ringsPx, SwfRgba color)
    {
        var rings = ringsPx
            .Select(ToTwips)
            .Where(r => r.Count >= 3)
            .ToArray();
        if (rings.Length == 0)
            return;

        var incomingEdges = rings.Sum(r => r.Count + 1);
        ShapeForIncoming(target, incomingEdges).AddFill(rings, color);
    }

    public byte[] ToBytes(bool compress = true, byte version = 10)
    {
        using var body = new MemoryStream();
        WriteBytes(body, SwfEncoding.Rect(0, _width * TwipsPerPixel, 0, _height * TwipsPerPixel));
        WriteUInt16(body, (ushort)(_frameRate << 8));
        WriteUInt16(body, 1); // FrameCount
        WriteTag(body, 69, BitConverter.GetBytes(0u)); // FileAttributes
        WriteTag(body, 9, [_background.R, _background.G, _background.B]); // SetBackgroundColor

        ushort depth = 1;
        ushort characterId = 1;
        foreach (var shape in _backShapes.Concat(_shapes))
        {
            if (!shape.HasItems)
                continue;

            WriteBytes(body, shape.Encode(characterId));
            using var place = new MemoryStream();
            place.WriteByte(0x02); // PlaceFlagHasCharacter
            WriteUInt16(place, depth);
            WriteUInt16(place, characterId);
            WriteTag(body, 26, place.ToArray()); // PlaceObject2
            depth++;
            characterId++;
        }

        WriteTag(body, 1, []); // ShowFrame
        WriteTag(body, 0, []); // End

        var bodyBytes = body.ToArray();
        var uncompressedLength = checked((uint)(8 + bodyBytes.Length));

        using var output = new MemoryStream();
        if (!compress)
        {
            output.Write(Encoding.ASCII.GetBytes("FWS"));
            output.WriteByte(version);
            WriteUInt32(output, uncompressedLength);
            output.Write(bodyBytes);
            return output.ToArray();
        }

        output.Write(Encoding.ASCII.GetBytes("CWS"));
        output.WriteByte(version);
        WriteUInt32(output, uncompressedLength);
        using (var zlib = new ZLibStream(output, CompressionLevel.SmallestSize, leaveOpen: true))
            zlib.Write(bodyBytes);
        return output.ToArray();
    }

    private static DirectSwfShape ShapeForIncoming(List<DirectSwfShape> target, int incomingEdges)
    {
        var current = target[^1];
        if (current.HasItems && current.EdgeCount + Math.Max(0, incomingEdges) > MaxEdgesPerShape)
        {
            current = new DirectSwfShape();
            target.Add(current);
        }
        return current;
    }

    private static List<SwfIntPoint> ToTwips(IReadOnlyList<SwfPoint> points)
    {
        var result = new List<SwfIntPoint>(points.Count);
        SwfIntPoint? last = null;
        foreach (var point in points)
        {
            var p = new SwfIntPoint(
                (int)Math.Round(point.X * TwipsPerPixel),
                (int)Math.Round(point.Y * TwipsPerPixel));
            if (last is null || p != last.Value)
            {
                result.Add(p);
                last = p;
            }
        }
        return result;
    }

    internal static byte[] Tag(int code, byte[] data)
    {
        using var ms = new MemoryStream();
        WriteTag(ms, code, data);
        return ms.ToArray();
    }

    internal static void WriteTag(Stream stream, int code, byte[] data)
    {
        if (data.Length < 0x3F)
        {
            WriteUInt16(stream, (ushort)((code << 6) | data.Length));
        }
        else
        {
            WriteUInt16(stream, (ushort)((code << 6) | 0x3F));
            WriteUInt32(stream, (uint)data.Length);
        }
        stream.Write(data);
    }

    internal static void WriteBytes(Stream stream, byte[] data) => stream.Write(data);

    internal static void WriteUInt16(Stream stream, ushort value)
    {
        Span<byte> b = stackalloc byte[2];
        System.Buffers.Binary.BinaryPrimitives.WriteUInt16LittleEndian(b, value);
        stream.Write(b);
    }

    internal static void WriteUInt32(Stream stream, uint value)
    {
        Span<byte> b = stackalloc byte[4];
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(b, value);
        stream.Write(b);
    }
}

internal sealed class DirectSwfShape
{
    private readonly List<(int Width, SwfRgba Color)> _lineStyles = [];
    private readonly List<SwfRgba> _fillStyles = [];
    private readonly Dictionary<(int Width, SwfRgba Color), int> _lineStyleIndexes = [];
    private readonly Dictionary<SwfRgba, int> _fillStyleIndexes = [];
    private readonly List<ShapeItem> _items = [];

    public int EdgeCount { get; private set; }
    public bool HasItems => _items.Count > 0;

    public bool TryGetGeometryBounds(out (int XMin, int XMax, int YMin, int YMax) bounds)
    {
        if (_items.Count == 0)
        {
            bounds = default;
            return false;
        }

        var xmin = int.MaxValue;
        var xmax = int.MinValue;
        var ymin = int.MaxValue;
        var ymax = int.MinValue;
        foreach (var point in _items.SelectMany(i => i.Points))
        {
            xmin = Math.Min(xmin, point.X);
            xmax = Math.Max(xmax, point.X);
            ymin = Math.Min(ymin, point.Y);
            ymax = Math.Max(ymax, point.Y);
        }

        bounds = (xmin, xmax, ymin, ymax);
        return xmin != int.MaxValue;
    }

    public void TransformGeometry(
        int sourceXMin,
        int sourceYMin,
        double scaleX,
        double scaleY,
        double targetX,
        double targetY)
    {
        for (var i = 0; i < _items.Count; i++)
        {
            var item = _items[i];
            var transformed = new List<SwfIntPoint>(item.Points.Count);
            SwfIntPoint? last = null;
            foreach (var p in item.Points)
            {
                var x = checked((int)Math.Round(targetX + (p.X - (double)sourceXMin) * scaleX));
                var y = checked((int)Math.Round(targetY + (p.Y - (double)sourceYMin) * scaleY));
                var np = new SwfIntPoint(x, y);
                if (last is null || np != last.Value)
                {
                    transformed.Add(np);
                    last = np;
                }
            }
            _items[i] = item with { Points = transformed };
        }
    }

    public void AddPolyline(List<SwfIntPoint> points, int widthTwips, SwfRgba color)
    {
        if (points.Count < 2)
            return;
        _items.Add(new ShapeItem(false, GetLineStyle(widthTwips, color), points));
        EdgeCount += points.Count;
    }

    public void AddFill(IEnumerable<List<SwfIntPoint>> rings, SwfRgba color)
    {
        var index = GetFillStyle(color);
        foreach (var ring in rings)
        {
            if (ring.Count < 3)
                continue;
            _items.Add(new ShapeItem(true, index, ring));
            EdgeCount += ring.Count + 1;
        }
    }

    public byte[] Encode(ushort shapeId)
    {
        var fillBits = _fillStyles.Count > 0 ? SwfEncoding.UnsignedBits(_fillStyles.Count) : 0;
        var lineBits = _lineStyles.Count > 0 ? SwfEncoding.UnsignedBits(_lineStyles.Count) : 0;
        if (fillBits > 15 || lineBits > 15)
            throw new InvalidOperationException("SWF Shape의 스타일 수가 허용 범위를 초과했습니다.");

        using var head = new MemoryStream();
        DirectSwfDocument.WriteUInt16(head, shapeId);
        DirectSwfDocument.WriteBytes(head, SwfEncoding.Rect(GetBounds()));
        WriteFillStyleArray(head);
        WriteLineStyleArray(head);

        var bits = new SwfBitWriter();
        bits.Unsigned(fillBits, 4);
        bits.Unsigned(lineBits, 4);

        var currentLine = 0;
        var currentFill = 0;
        var currentX = 0;
        var currentY = 0;

        // Same strategy as the reference implementation: fills first, strokes on top.
        foreach (var item in _items.Where(i => i.IsFill).Concat(_items.Where(i => !i.IsFill)))
        {
            var wantedLine = item.IsFill ? 0 : item.StyleIndex;
            var wantedFill = item.IsFill ? item.StyleIndex : 0;
            var first = item.Points[0];

            bits.Unsigned(0, 1); // TypeFlag = non-edge
            bits.Unsigned(0, 1); // StateNewStyles
            bits.Unsigned(wantedLine != currentLine ? 1 : 0, 1); // StateLineStyle
            bits.Unsigned(0, 1); // StateFillStyle1
            bits.Unsigned(wantedFill != currentFill ? 1 : 0, 1); // StateFillStyle0
            bits.Unsigned(1, 1); // StateMoveTo

            var moveBits = SwfEncoding.SignedBits(first.X, first.Y);
            bits.Unsigned(moveBits, 5);
            bits.Signed(first.X, moveBits);
            bits.Signed(first.Y, moveBits);

            if (wantedFill != currentFill)
            {
                bits.Unsigned(wantedFill, fillBits);
                currentFill = wantedFill;
            }
            if (wantedLine != currentLine)
            {
                bits.Unsigned(wantedLine, lineBits);
                currentLine = wantedLine;
            }

            currentX = first.X;
            currentY = first.Y;

            var sequence = new List<SwfIntPoint>(item.Points.Skip(1));
            if (item.IsFill && item.Points[^1] != item.Points[0])
                sequence.Add(item.Points[0]);

            foreach (var point in sequence)
            {
                WriteEdgePossiblySplit(bits, ref currentX, ref currentY, point.X, point.Y);
            }
        }

        bits.Unsigned(0, 6); // EndShapeRecord
        var payload = head.ToArray().Concat(bits.Flush()).ToArray();
        return DirectSwfDocument.Tag(32, payload); // DefineShape3
    }

    private int GetLineStyle(int width, SwfRgba color)
    {
        var key = (width, color);
        if (_lineStyleIndexes.TryGetValue(key, out var existing))
            return existing;
        _lineStyles.Add(key);
        var index = _lineStyles.Count;
        _lineStyleIndexes[key] = index;
        return index;
    }

    private int GetFillStyle(SwfRgba color)
    {
        if (_fillStyleIndexes.TryGetValue(color, out var existing))
            return existing;
        _fillStyles.Add(color);
        var index = _fillStyles.Count;
        _fillStyleIndexes[color] = index;
        return index;
    }

    private (int XMin, int XMax, int YMin, int YMax) GetBounds()
    {
        if (_items.Count == 0)
            return (0, 20, 0, 20);

        var xmin = int.MaxValue;
        var xmax = int.MinValue;
        var ymin = int.MaxValue;
        var ymax = int.MinValue;
        foreach (var point in _items.SelectMany(i => i.Points))
        {
            xmin = Math.Min(xmin, point.X);
            xmax = Math.Max(xmax, point.X);
            ymin = Math.Min(ymin, point.Y);
            ymax = Math.Max(ymax, point.Y);
        }

        var pad = _lineStyles.Count == 0 ? 0 : _lineStyles.Max(s => s.Width);
        return (xmin - pad, xmax + pad, ymin - pad, ymax + pad);
    }

    private void WriteFillStyleArray(Stream stream)
    {
        WriteStyleCount(stream, _fillStyles.Count);
        foreach (var c in _fillStyles)
        {
            stream.WriteByte(0x00); // solid fill
            stream.WriteByte(c.R);
            stream.WriteByte(c.G);
            stream.WriteByte(c.B);
            stream.WriteByte(c.A);
        }
    }

    private void WriteLineStyleArray(Stream stream)
    {
        WriteStyleCount(stream, _lineStyles.Count);
        foreach (var style in _lineStyles)
        {
            DirectSwfDocument.WriteUInt16(stream, (ushort)Math.Clamp(style.Width, 1, ushort.MaxValue));
            stream.WriteByte(style.Color.R);
            stream.WriteByte(style.Color.G);
            stream.WriteByte(style.Color.B);
            stream.WriteByte(style.Color.A);
        }
    }

    private static void WriteStyleCount(Stream stream, int count)
    {
        if (count < 0xFF)
        {
            stream.WriteByte((byte)count);
        }
        else
        {
            stream.WriteByte(0xFF);
            DirectSwfDocument.WriteUInt16(stream, checked((ushort)count));
        }
    }

    private static void WriteEdgePossiblySplit(SwfBitWriter bits, ref int cx, ref int cy, int x, int y)
    {
        var dx = x - cx;
        var dy = y - cy;
        if (dx == 0 && dy == 0)
            return;

        // StraightEdgeRecord stores NumBits in 4 bits -> max signed width is 17 bits.
        // Keep deltas comfortably below that and split long segments.
        const int safeDelta = 60_000;
        var steps = Math.Max(1, (int)Math.Ceiling(Math.Max(Math.Abs((long)dx), Math.Abs((long)dy)) / (double)safeDelta));
        var sx = cx;
        var sy = cy;
        for (var i = 1; i <= steps; i++)
        {
            var nx = i == steps ? x : sx + (int)Math.Round(dx * (i / (double)steps));
            var ny = i == steps ? y : sy + (int)Math.Round(dy * (i / (double)steps));
            WriteStraightEdge(bits, nx - cx, ny - cy);
            cx = nx;
            cy = ny;
        }
    }

    private static void WriteStraightEdge(SwfBitWriter bits, int dx, int dy)
    {
        if (dx == 0 && dy == 0)
            return;
        var n = Math.Max(2, SwfEncoding.SignedBits(dx, dy));
        if (n > 17)
            throw new InvalidOperationException("SWF edge delta가 너무 큽니다.");

        bits.Unsigned(1, 1); // TypeFlag edge
        bits.Unsigned(1, 1); // StraightFlag
        bits.Unsigned(n - 2, 4);
        if (dx != 0 && dy != 0)
        {
            bits.Unsigned(1, 1); // GeneralLineFlag
            bits.Signed(dx, n);
            bits.Signed(dy, n);
        }
        else
        {
            bits.Unsigned(0, 1);
            if (dx == 0)
            {
                bits.Unsigned(1, 1); // vertical
                bits.Signed(dy, n);
            }
            else
            {
                bits.Unsigned(0, 1); // horizontal
                bits.Signed(dx, n);
            }
        }
    }

    private sealed record ShapeItem(bool IsFill, int StyleIndex, List<SwfIntPoint> Points);
}

internal sealed class SwfBitWriter
{
    private readonly List<byte> _buffer = [];
    private ulong _accumulator;
    private int _bitCount;

    public void Unsigned(int value, int bits) => Unsigned((uint)value, bits);

    public void Unsigned(uint value, int bits)
    {
        if (bits == 0)
            return;
        if (bits < 0 || bits > 32)
            throw new ArgumentOutOfRangeException(nameof(bits));

        var mask = bits == 32 ? uint.MaxValue : (1u << bits) - 1u;
        _accumulator = (_accumulator << bits) | (value & mask);
        _bitCount += bits;
        while (_bitCount >= 8)
        {
            _bitCount -= 8;
            _buffer.Add((byte)((_accumulator >> _bitCount) & 0xFF));
            _accumulator &= _bitCount == 0 ? 0UL : (1UL << _bitCount) - 1UL;
        }
    }

    public void Signed(int value, int bits)
    {
        uint encoded;
        if (bits == 32)
            encoded = unchecked((uint)value);
        else
            encoded = unchecked((uint)value) & ((1u << bits) - 1u);
        Unsigned(encoded, bits);
    }

    public byte[] Flush()
    {
        if (_bitCount > 0)
        {
            _buffer.Add((byte)((_accumulator << (8 - _bitCount)) & 0xFF));
            _accumulator = 0;
            _bitCount = 0;
        }
        return _buffer.ToArray();
    }
}

internal static class SwfEncoding
{
    public static int SignedBits(params int[] values)
    {
        var result = 1;
        foreach (var value in values)
        {
            long v = value;
            var n = v >= 0
                ? BitLength((ulong)v) + 1
                : BitLength((ulong)~v) + 1;
            result = Math.Max(result, n);
        }
        return result;
    }

    public static int UnsignedBits(int value) => Math.Max(1, BitLength((ulong)Math.Max(0, value)));

    public static byte[] Rect((int XMin, int XMax, int YMin, int YMax) bounds) => Rect(bounds.XMin, bounds.XMax, bounds.YMin, bounds.YMax);

    public static byte[] Rect(int xmin, int xmax, int ymin, int ymax)
    {
        var bits = new SwfBitWriter();
        var n = SignedBits(xmin, xmax, ymin, ymax);
        bits.Unsigned(n, 5);
        bits.Signed(xmin, n);
        bits.Signed(xmax, n);
        bits.Signed(ymin, n);
        bits.Signed(ymax, n);
        return bits.Flush();
    }

    private static int BitLength(ulong value)
    {
        if (value == 0)
            return 0;
        var bits = 0;
        while (value != 0)
        {
            bits++;
            value >>= 1;
        }
        return bits;
    }
}

internal readonly record struct SwfGeometryBounds(double X, double Y, double Width, double Height);

internal readonly record struct SwfContentFitResult(
    bool HasContent,
    double SourceWidthPx,
    double SourceHeightPx,
    double TargetXpx,
    double TargetYpx,
    double TargetWidthPx,
    double TargetHeightPx,
    double ScaleX,
    double ScaleY);

internal readonly record struct SwfPoint(double X, double Y);
internal readonly record struct SwfIntPoint(int X, int Y);
internal readonly record struct SwfRgba(byte R, byte G, byte B, byte A = 255);
