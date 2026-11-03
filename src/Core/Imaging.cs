namespace ShopHelper;

/// <summary>BGRA 32비트 이미지 (top-down).</summary>
public sealed class Frame
{
    public readonly byte[] Px;
    public readonly int W, H;
    public Frame(byte[] px, int w, int h) { Px = px; W = w; H = h; }
    public Frame(int w, int h) : this(new byte[w * h * 4], w, h) { }

    public Frame Crop(int x, int y, int w, int h)
    {
        x = Math.Clamp(x, 0, W - 1); y = Math.Clamp(y, 0, H - 1);
        w = Math.Clamp(w, 1, W - x); h = Math.Clamp(h, 1, H - y);
        var f = new Frame(w, h);
        for (int r = 0; r < h; r++)
            Buffer.BlockCopy(Px, ((y + r) * W + x) * 4, f.Px, r * w * 4, w * 4);
        return f;
    }

    /// <summary>쌍선형 확대/축소. 알파는 255로 채움.</summary>
    public Frame Scale(double s)
    {
        int nw = Math.Max(1, (int)(W * s)), nh = Math.Max(1, (int)(H * s));
        var f = new Frame(nw, nh);
        var dst = f.Px;
        double inv = 1.0 / s;
        for (int y = 0; y < nh; y++)
        {
            double sy = Math.Max(0, (y + 0.5) * inv - 0.5);
            int y0 = Math.Min((int)sy, H - 1), y1 = Math.Min(y0 + 1, H - 1);
            double fy = sy - y0;
            int r0 = y0 * W * 4, r1 = y1 * W * 4, o = y * nw * 4;
            for (int x = 0; x < nw; x++)
            {
                double sx = Math.Max(0, (x + 0.5) * inv - 0.5);
                int x0 = Math.Min((int)sx, W - 1), x1 = Math.Min(x0 + 1, W - 1);
                double fx = sx - x0;
                int a = r0 + x0 * 4, b = r0 + x1 * 4, c = r1 + x0 * 4, d = r1 + x1 * 4;
                for (int ch = 0; ch < 3; ch++)
                {
                    double top = Px[a + ch] + (Px[b + ch] - Px[a + ch]) * fx;
                    double bot = Px[c + ch] + (Px[d + ch] - Px[c + ch]) * fx;
                    dst[o + ch] = (byte)(top + (bot - top) * fy + 0.5);
                }
                dst[o + 3] = 255;
                o += 4;
            }
        }
        return f;
    }

    /// <summary>자동 대비: 밝기 하위 2%~상위 98% 를 0~255 로 늘림 (어두워진 칸도 OCR 이 읽게).</summary>
    public Frame AutoContrast()
    {
        var hist = new int[256];
        int n = W * H;
        for (int i = 0; i < Px.Length; i += 4) hist[(Px[i] * 29 + Px[i + 1] * 150 + Px[i + 2] * 77) >> 8]++;
        int lo = 0, hi = 255, acc = 0;
        for (; lo < 255; lo++) { acc += hist[lo]; if (acc > n * 0.02) break; }
        acc = 0;
        for (; hi > 0; hi--) { acc += hist[hi]; if (acc > n * 0.02) break; }
        var f = new Frame(W, H);
        if (hi - lo < 8) { Buffer.BlockCopy(Px, 0, f.Px, 0, Px.Length); return f; }
        var lut = new byte[256];
        for (int v = 0; v < 256; v++) lut[v] = (byte)Math.Clamp((v - lo) * 255 / (hi - lo), 0, 255);
        for (int i = 0; i < Px.Length; i += 4)
        {
            f.Px[i] = lut[Px[i]]; f.Px[i + 1] = lut[Px[i + 1]]; f.Px[i + 2] = lut[Px[i + 2]]; f.Px[i + 3] = 255;
        }
        return f;
    }

    public void DrawRect(Box b, byte r, byte g, byte bl, int t = 3)
    {
        int x1 = (int)b.X1, y1 = (int)b.Y1, x2 = (int)b.X2, y2 = (int)b.Y2;
        for (int k = 0; k < t; k++)
        {
            for (int x = x1; x <= x2; x++) { Put(x, y1 + k, r, g, bl); Put(x, y2 - k, r, g, bl); }
            for (int y = y1; y <= y2; y++) { Put(x1 + k, y, r, g, bl); Put(x2 - k, y, r, g, bl); }
        }
    }

    void Put(int x, int y, byte r, byte g, byte b)
    {
        if (x < 0 || y < 0 || x >= W || y >= H) return;
        int o = (y * W + x) * 4;
        Px[o] = b; Px[o + 1] = g; Px[o + 2] = r; Px[o + 3] = 255;
    }
}

/// <summary>칸 이미지를 작게 줄인 지문. 같은 그림인지 빠르게 비교.</summary>
public static class Fingerprint
{
    public const int Cell = 3;

    public static byte[] Make(Frame f, Box r)
    {
        int x0 = Math.Max(0, (int)r.X1), y0 = Math.Max(0, (int)r.Y1);
        int x1 = Math.Min(f.W, (int)r.X2), y1 = Math.Min(f.H, (int)r.Y2);
        int cw = Math.Max(1, (x1 - x0) / Cell), chh = Math.Max(1, (y1 - y0) / Cell);
        var fp = new byte[cw * chh + 2];
        fp[0] = (byte)Math.Min(255, cw); fp[1] = (byte)Math.Min(255, chh);
        var px = f.Px;
        for (int cy = 0; cy < chh; cy++)
            for (int cx = 0; cx < cw; cx++)
            {
                int sum = 0;
                for (int dy = 0; dy < Cell; dy++)
                {
                    int yy = y0 + cy * Cell + dy;
                    if (yy >= y1) break;
                    int o = (yy * f.W + x0 + cx * Cell) * 4;
                    for (int dx = 0; dx < Cell && x0 + cx * Cell + dx < x1; dx++, o += 4)
                        sum += (px[o] * 29 + px[o + 1] * 150 + px[o + 2] * 77) >> 8;
                }
                fp[2 + cy * cw + cx] = (byte)(sum / (Cell * Cell));
            }
        Normalize(fp);
        return fp;
    }

    /// <summary>
    /// 밝기·대비 정규화 (평균 128, 표준편차 48). 마우스를 올려 카드가 어두워지거나(=밝기/대비만 변함)
    /// 반짝여도 같은 그림으로 봄. 글자/숫자가 바뀌면 모양이 달라져서 그대로 잡힘.
    /// </summary>
    static void Normalize(byte[] fp)
    {
        int n = fp.Length - 2;
        if (n <= 0) return;
        double mean = 0;
        for (int i = 2; i < fp.Length; i++) mean += fp[i];
        mean /= n;
        double var = 0;
        for (int i = 2; i < fp.Length; i++) var += (fp[i] - mean) * (fp[i] - mean);
        double std = Math.Sqrt(var / n);
        for (int i = 2; i < fp.Length; i++)
            fp[i] = std < 2 ? (byte)128 : (byte)Math.Clamp(128 + (fp[i] - mean) / std * 48, 0, 255);
    }

    /// <summary>차이가 큰 셀 개수. 크기가 다르면 int.MaxValue.</summary>
    public static int DiffCells(byte[]? a, byte[]? b, int thr = 40)
    {
        if (a == null || b == null || a.Length != b.Length || a[0] != b[0] || a[1] != b[1]) return int.MaxValue;
        int n = 0;
        for (int i = 2; i < a.Length; i++)
            if (Math.Abs(a[i] - b[i]) > thr) n++;
        return n;
    }

    public static int Cells(byte[] a) => a.Length - 2;
}
