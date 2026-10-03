using System.IO.Compression;

// Draws the BigFiles icon: a rounded square with a blue→indigo vertical gradient and four white
// rounded bars of decreasing width ("sorted biggest first"), each a bit more transparent than the one above.
// Writes a multi-size .ico (16–256 px) and optionally a 256 px .png.

var icoPath = args.Length > 0 ? args[0] : "BigFiles.ico";
var pngPath = args.Length > 1 ? args[1] : null;
int[] sizes = { 16, 20, 24, 32, 40, 48, 64, 128, 256 };

var images = sizes.Select(s => (Size: s, Data: s >= 64 ? Png(s, Render(s)) : Dib(s, Render(s)))).ToList();
using (var f = File.Create(icoPath))
using (var w = new BinaryWriter(f))
{
    w.Write((ushort)0);
    w.Write((ushort)1);
    w.Write((ushort)images.Count);
    int offset = 6 + 16 * images.Count;
    foreach (var (s, data) in images)
    {
        w.Write((byte)(s >= 256 ? 0 : s));
        w.Write((byte)(s >= 256 ? 0 : s));
        w.Write((byte)0);
        w.Write((byte)0);
        w.Write((ushort)1);
        w.Write((ushort)32);
        w.Write(data.Length);
        w.Write(offset);
        offset += data.Length;
    }
    foreach (var (_, data) in images) w.Write(data);
}
if (pngPath != null) File.WriteAllBytes(pngPath, Png(256, Render(256)));
Console.WriteLine($"Wrote {icoPath}" + (pngPath != null ? $" and {pngPath}" : ""));

// ---------------------------------------------------------------------------------------------

// Returns straight-alpha RGBA, top row first.
static byte[] Render(int s)
{
    double m = s <= 24 ? 0 : s <= 48 ? Math.Round(s * 0.04) : s * 0.04; // margin around the square (whole pixels when small)
    double inner = s - 2 * m;
    double radius = inner * 0.22;
    (double R, double G, double B) top = (59, 130, 246), bottom = (79, 70, 229);

    double h = inner * 0.105, gap = inner * 0.07, x0 = m + inner * 0.2;
    double[] widths = { 0.60, 0.47, 0.34, 0.21 };
    double[] alphas = { 1.0, 0.82, 0.64, 0.46 };
    if (s <= 32)
    {
        // Snap to whole pixels so the bars stay crisp at small sizes.
        h = Math.Max(2, Math.Round(h));
        gap = Math.Max(1, Math.Round(gap));
        x0 = Math.Round(x0);
    }
    double total = 4 * h + 3 * gap;
    double y0 = (s - total) / 2;
    if (s <= 32) y0 = Math.Floor(y0);
    var bars = widths.Select((wf, i) =>
    {
        double bw = inner * wf;
        if (s <= 32) bw = Math.Max(h, Math.Round(bw));
        return (X: x0, Y: y0 + i * (h + gap), W: bw, H: h, A: alphas[i]);
    }).ToArray();

    const int ss = 8; // 8×8 supersampling
    var px = new byte[s * s * 4];
    for (int y = 0; y < s; y++)
    for (int x = 0; x < s; x++)
    {
        double bgCov = 0, r = 0, g = 0, b = 0;
        for (int sy = 0; sy < ss; sy++)
        for (int sx = 0; sx < ss; sx++)
        {
            double fx = x + (sx + 0.5) / ss, fy = y + (sy + 0.5) / ss;
            if (RoundBox(fx, fy, m, m, inner, inner, radius) > 0) continue;
            double t = Math.Clamp((fy - m) / inner, 0, 1);
            double cr = top.R + (bottom.R - top.R) * t, cg = top.G + (bottom.G - top.G) * t, cb = top.B + (bottom.B - top.B) * t;
            foreach (var bar in bars)
                if (RoundBox(fx, fy, bar.X, bar.Y, bar.W, bar.H, bar.H / 2) <= 0)
                {
                    cr += (255 - cr) * bar.A;
                    cg += (255 - cg) * bar.A;
                    cb += (255 - cb) * bar.A;
                }
            bgCov++;
            r += cr; g += cg; b += cb;
        }
        int i = (y * s + x) * 4;
        if (bgCov == 0) continue;
        px[i] = (byte)Math.Round(r / bgCov);
        px[i + 1] = (byte)Math.Round(g / bgCov);
        px[i + 2] = (byte)Math.Round(b / bgCov);
        px[i + 3] = (byte)Math.Round(255 * bgCov / (ss * ss));
    }
    return px;
}

// Signed distance to a rounded rectangle (x, y = top-left corner).
static double RoundBox(double px, double py, double x, double y, double w, double h, double r)
{
    double cx = x + w / 2, cy = y + h / 2;
    double qx = Math.Abs(px - cx) - (w / 2 - r), qy = Math.Abs(py - cy) - (h / 2 - r);
    double outside = Math.Sqrt(Math.Pow(Math.Max(qx, 0), 2) + Math.Pow(Math.Max(qy, 0), 2));
    return outside + Math.Min(Math.Max(qx, qy), 0) - r;
}

// Classic 32-bit BMP icon image (best compatibility for small sizes).
static byte[] Dib(int s, byte[] rgba)
{
    int maskStride = ((s + 31) / 32) * 4;
    using var ms = new MemoryStream();
    using var w = new BinaryWriter(ms);
    w.Write(40); w.Write(s); w.Write(s * 2); w.Write((ushort)1); w.Write((ushort)32);
    w.Write(0); w.Write(s * s * 4 + maskStride * s); w.Write(0); w.Write(0); w.Write(0); w.Write(0);
    for (int y = s - 1; y >= 0; y--)
        for (int x = 0; x < s; x++)
        {
            int i = (y * s + x) * 4;
            w.Write(rgba[i + 2]); w.Write(rgba[i + 1]); w.Write(rgba[i]); w.Write(rgba[i + 3]);
        }
    for (int y = s - 1; y >= 0; y--)
    {
        var row = new byte[maskStride];
        for (int x = 0; x < s; x++)
            if (rgba[(y * s + x) * 4 + 3] == 0) row[x / 8] |= (byte)(0x80 >> (x % 8));
        w.Write(row);
    }
    return ms.ToArray();
}

static byte[] Png(int s, byte[] rgba)
{
    using var raw = new MemoryStream();
    using (var z = new ZLibStream(raw, CompressionLevel.SmallestSize, leaveOpen: true))
        for (int y = 0; y < s; y++)
        {
            z.WriteByte(0);
            z.Write(rgba, y * s * 4, s * 4);
        }
    using var ms = new MemoryStream();
    ms.Write(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A });
    var ihdr = new byte[13];
    BE(ihdr, 0, s); BE(ihdr, 4, s);
    ihdr[8] = 8; ihdr[9] = 6;
    Chunk(ms, "IHDR", ihdr);
    Chunk(ms, "IDAT", raw.ToArray());
    Chunk(ms, "IEND", Array.Empty<byte>());
    return ms.ToArray();
}

static void Chunk(Stream s, string type, byte[] data)
{
    var len = new byte[4];
    BE(len, 0, data.Length);
    s.Write(len);
    var td = System.Text.Encoding.ASCII.GetBytes(type).Concat(data).ToArray();
    s.Write(td);
    var crc = new byte[4];
    BE(crc, 0, (int)Crc32(td));
    s.Write(crc);
}

static void BE(byte[] b, int o, int v)
{
    b[o] = (byte)(v >> 24); b[o + 1] = (byte)(v >> 16); b[o + 2] = (byte)(v >> 8); b[o + 3] = (byte)v;
}

static uint Crc32(byte[] data)
{
    uint c = 0xFFFFFFFF;
    foreach (var d in data)
    {
        c ^= d;
        for (int k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320 ^ (c >> 1) : c >> 1;
    }
    return c ^ 0xFFFFFFFF;
}
