using System.Reflection;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace ShopHelper;

/// <summary>
/// 프로그램에 넣은 OCR (PaddleOCR PP-OCRv4, ONNX). Windows OCR 이 없는 PC 에서도 동작.
///  - det.onnx        : 글자 줄 위치 찾기
///  - rec_korean.onnx : 한국어 (상품 이름)
///  - rec_en.onnx     : 영어·숫자 (가격, 개수)
/// 모델 출처: PaddleOCR (Apache-2.0), Sdcb.PaddleOCR.Models.LocalV4 에서 ONNX 로 변환.
/// </summary>
public sealed class PaddleOcr : IOcr, IDisposable
{
    readonly InferenceSession det, recKo, recEn;
    readonly string[] dictKo, dictEn;
    public int MaxDim => 6000;
    /// <summary>글자 위치 찾기에 쓰는 이미지의 긴 변 최대 크기 (클수록 정확하지만 느림)</summary>
    public int DetLimit = 1600;

    public PaddleOcr()
    {
        var so = new SessionOptions
        {
            IntraOpNumThreads = Math.Clamp(Environment.ProcessorCount / 2, 1, 4),
            GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL,
        };
        det = new InferenceSession(Res("det.onnx"), so);
        recKo = new InferenceSession(Res("rec_korean.onnx"), so);
        recEn = new InferenceSession(Res("rec_en.onnx"), so);
        dictKo = Dict("korean_dict.txt");
        dictEn = Dict("en_dict.txt");
    }

    static byte[] Res(string name)
    {
        using var s = Assembly.GetExecutingAssembly().GetManifestResourceStream(name)
                      ?? throw new FileNotFoundException("내장 OCR 모델이 없음: " + name);
        using var ms = new MemoryStream();
        s.CopyTo(ms);
        return ms.ToArray();
    }

    static string[] Dict(string name)
    {
        var text = System.Text.Encoding.UTF8.GetString(Res(name)).Replace("\r", "");
        var lines = text.Split('\n').ToList();
        if (lines.Count > 0 && lines[^1].Length == 0) lines.RemoveAt(lines.Count - 1);
        lines.Add(" ");   // 마지막 클래스 = 공백
        return lines.ToArray();
    }

    public void Dispose() { det.Dispose(); recKo.Dispose(); recEn.Dispose(); }

    public List<List<OcrWord>> Recognize(Frame img, Box src, double scale) => Run(img, english: false);
    public List<List<OcrWord>>? RecognizeDigits(Frame img, Box src, double scale) => Run(img, english: true);

    // 같은 이미지를 한국어 → 영어 순서로 읽는 경우가 많아서, 글자 위치와 한국어 결과를 기억해 둠
    Frame? lastImg;
    List<Box>? lastBoxes;
    string[]? lastKo;
    static readonly System.Text.RegularExpressions.Regex Hangul = new("[가-힣]");

    List<List<OcrWord>> Run(Frame img, bool english)
    {
        List<Box> boxes;
        string[]? ko = null;
        if (ReferenceEquals(img, lastImg) && lastBoxes != null) { boxes = lastBoxes; ko = lastKo; }
        else { boxes = Detect(img); lastImg = img; lastBoxes = boxes; lastKo = null; }
        var lines = new List<List<OcrWord>>();
        var texts = new string[boxes.Count];
        for (int i = 0; i < boxes.Count; i++)
        {
            var b = boxes[i];
            string text;
            if (english && ko != null && Hangul.IsMatch(ko[i]) && !ko[i].Any(char.IsAsciiDigit) && ko[i].IndexOfAny(new[] { 'X', 'x', '×' }) < 0)
                text = ko[i];   // 숫자·X 가 없는 한글 줄(상품 이름)은 영어로 다시 읽을 필요 없음
            else
                text = Read(img, english ? WhiteOnly(img, b) : b, english);
            texts[i] = text;
            if (text.Length == 0) continue;
            lines.Add(new List<OcrWord> { new(text, b.X1, b.Y1, b.W, b.H) });
        }
        if (!english) lastKo = texts;
        return lines;
    }

    // ---------------- 글자 위치 찾기 (DB) ----------------
    public List<Box> Detect(Frame img)
    {
        double r = Math.Max(img.W, img.H) > DetLimit ? (double)DetLimit / Math.Max(img.W, img.H) : 1.0;
        int nw = Math.Max(32, (int)Math.Round(img.W * r / 32) * 32), nh = Math.Max(32, (int)Math.Round(img.H * r / 32) * 32);
        var small = Resize(img, nw, nh);
        var x = new DenseTensor<float>(new[] { 1, 3, nh, nw });
        float[] mean = { 0.485f, 0.456f, 0.406f }, std = { 0.229f, 0.224f, 0.225f };
        var px = small.Px;
        for (int y = 0; y < nh; y++)
            for (int xx = 0; xx < nw; xx++)
            {
                int o = (y * nw + xx) * 4;
                for (int c = 0; c < 3; c++) x[0, c, y, xx] = (px[o + c] / 255f - mean[c]) / std[c];   // BGR 순서 그대로 (PaddleOCR 과 같음)
            }
        using var res = det.Run(new[] { NamedOnnxValue.CreateFromTensor(det.InputMetadata.Keys.First(), x) });
        var prob = res.First().AsTensor<float>();
        int ph = prob.Dimensions[2], pw = prob.Dimensions[3];
        var p = new float[ph * pw];
        for (int y = 0; y < ph; y++) for (int xx = 0; xx < pw; xx++) p[y * pw + xx] = prob[0, 0, y, xx];

        // 연결된 영역(글자 줄) 찾기
        var seen = new bool[p.Length];
        var boxes = new List<Box>();
        var stack = new Stack<int>();
        double sx = (double)img.W / pw, sy = (double)img.H / ph;
        for (int i = 0; i < p.Length; i++)
        {
            if (seen[i] || p[i] <= 0.3f) continue;
            int x0 = int.MaxValue, y0 = int.MaxValue, x1 = -1, y1 = -1, area = 0; double sum = 0;
            stack.Push(i); seen[i] = true;
            while (stack.Count > 0)
            {
                int k = stack.Pop(), ky = k / pw, kx = k % pw;
                area++; sum += p[k];
                if (kx < x0) x0 = kx; if (kx > x1) x1 = kx; if (ky < y0) y0 = ky; if (ky > y1) y1 = ky;
                for (int dy = -1; dy <= 1; dy++)
                    for (int dx = -1; dx <= 1; dx++)
                    {
                        int ny = ky + dy, nx = kx + dx;
                        if (nx < 0 || ny < 0 || nx >= pw || ny >= ph) continue;
                        int n = ny * pw + nx;
                        if (!seen[n] && p[n] > 0.3f) { seen[n] = true; stack.Push(n); }
                    }
            }
            if (area < 6 || sum / area < 0.5) continue;
            double bw = x1 - x0 + 1, bh = y1 - y0 + 1;
            double d = bw * bh * 2.0 / (2 * (bw + bh));           // 넓히기 (unclip 2.0)
            boxes.Add(new Box(Math.Max(0, (x0 - d) * sx), Math.Max(0, (y0 - d) * sy),
                              Math.Min(img.W, (x1 + 1 + d) * sx), Math.Min(img.H, (y1 + 1 + d) * sy)));
        }
        return boxes.OrderBy(b => b.Y1).ThenBy(b => b.X1).ToList();
    }

    /// <summary>숫자 읽기용: 흰색(밝고 채도 낮은) 글자 부분만 → 가격 앞 보라색 보석 아이콘을 뺌.</summary>
    static Box WhiteOnly(Frame img, Box b)
    {
        int x0 = (int)b.X1, x1 = (int)b.X2, y0 = (int)b.Y1, y1 = (int)b.Y2;
        int first = -1, last = -1;
        for (int x = x0; x < x1; x++)
            for (int y = y0; y < y1; y++)
            {
                int o = (y * img.W + x) * 4;
                int mx = Math.Max(img.Px[o], Math.Max(img.Px[o + 1], img.Px[o + 2]));
                int mn = Math.Min(img.Px[o], Math.Min(img.Px[o + 1], img.Px[o + 2]));
                if (mx > 140 && mx - mn < 60) { if (first < 0) first = x; last = x; break; }
            }
        if (first < 0) return b;
        double pad = b.H * 0.25;
        return new Box(Math.Max(b.X1, first - pad), b.Y1, Math.Min(b.X2, last + 1 + pad), b.Y2);
    }

    // ---------------- 글자 읽기 (CTC) ----------------
    string Read(Frame img, Box b, bool english)
    {
        int bw = (int)b.W, bh = (int)b.H;
        if (bw < 2 || bh < 2) return "";
        var crop = img.Crop((int)b.X1, (int)b.Y1, bw, bh);
        int nw = Math.Clamp((int)Math.Ceiling(48.0 * bw / bh), 8, 1600);
        var r = Resize(crop, nw, 48);
        var x = new DenseTensor<float>(new[] { 1, 3, 48, nw });
        var px = r.Px;
        for (int y = 0; y < 48; y++)
            for (int xx = 0; xx < nw; xx++)
            {
                int o = (y * nw + xx) * 4;
                for (int c = 0; c < 3; c++) x[0, c, y, xx] = (px[o + c] / 255f - 0.5f) / 0.5f;
            }
        var sess = english ? recEn : recKo;
        var dict = english ? dictEn : dictKo;
        using var res = sess.Run(new[] { NamedOnnxValue.CreateFromTensor(sess.InputMetadata.Keys.First(), x) });
        var o2 = res.First().AsTensor<float>();
        int steps = o2.Dimensions[1], classes = o2.Dimensions[2];
        var sb = new System.Text.StringBuilder();
        int prev = -1;
        for (int t = 0; t < steps; t++)
        {
            int best = 0; float bv = float.MinValue;
            for (int k = 0; k < classes; k++) { float v = o2[0, t, k]; if (v > bv) { bv = v; best = k; } }
            if (best != prev && best != 0 && best - 1 < dict.Length) sb.Append(dict[best - 1]);
            prev = best;
        }
        return sb.ToString().Trim();
    }

    static Frame Resize(Frame f, int w, int h)
    {
        if (f.W == w && f.H == h) return f;
        // Frame.Scale 은 같은 배율만 → 가로·세로 따로 맞출 때는 직접 (쌍선형)
        var o = new Frame(w, h);
        double fx = (double)f.W / w, fy = (double)f.H / h;
        for (int y = 0; y < h; y++)
        {
            double syy = Math.Max(0, (y + 0.5) * fy - 0.5);
            int y0 = Math.Min((int)syy, f.H - 1), y1 = Math.Min(y0 + 1, f.H - 1); double ty = syy - y0;
            for (int x = 0; x < w; x++)
            {
                double sxx = Math.Max(0, (x + 0.5) * fx - 0.5);
                int x0 = Math.Min((int)sxx, f.W - 1), x1 = Math.Min(x0 + 1, f.W - 1); double tx = sxx - x0;
                int a = (y0 * f.W + x0) * 4, b = (y0 * f.W + x1) * 4, c = (y1 * f.W + x0) * 4, d = (y1 * f.W + x1) * 4, oo = (y * w + x) * 4;
                for (int ch = 0; ch < 3; ch++)
                {
                    double top = f.Px[a + ch] + (f.Px[b + ch] - f.Px[a + ch]) * tx;
                    double bot = f.Px[c + ch] + (f.Px[d + ch] - f.Px[c + ch]) * tx;
                    o.Px[oo + ch] = (byte)(top + (bot - top) * ty + 0.5);
                }
                o.Px[oo + 3] = 255;
            }
        }
        return o;
    }
}
