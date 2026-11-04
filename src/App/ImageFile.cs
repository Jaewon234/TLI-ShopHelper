using Windows.Graphics.Imaging;
using Windows.Security.Cryptography;
using Windows.Storage;
using Windows.Storage.Streams;

namespace ShopHelper;

/// <summary>PNG 불러오기/저장 (Windows 이미지 코덱).</summary>
static class ImageFile
{
    public static Frame Load(string path)
    {
        var file = StorageFile.GetFileFromPathAsync(Path.GetFullPath(path)).AsTask().GetAwaiter().GetResult();
        using var s = file.OpenAsync(FileAccessMode.Read).AsTask().GetAwaiter().GetResult();
        var dec = BitmapDecoder.CreateAsync(s).AsTask().GetAwaiter().GetResult();
        using var sb = dec.GetSoftwareBitmapAsync(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Ignore).AsTask().GetAwaiter().GetResult();
        var buf = new Windows.Storage.Streams.Buffer((uint)(sb.PixelWidth * sb.PixelHeight * 4));
        sb.CopyToBuffer(buf);
        CryptographicBuffer.CopyToByteArray(buf, out byte[] px);
        return new Frame(px, sb.PixelWidth, sb.PixelHeight);
    }

    public static void SavePng(Frame f, string path)
    {
        using var ms = new InMemoryRandomAccessStream();
        var enc = BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, ms).AsTask().GetAwaiter().GetResult();
        var px = (byte[])f.Px.Clone();
        for (int i = 3; i < px.Length; i += 4) px[i] = 255;
        enc.SetPixelData(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Ignore, (uint)f.W, (uint)f.H, 96, 96, px);
        enc.FlushAsync().AsTask().GetAwaiter().GetResult();
        var reader = new DataReader(ms.GetInputStreamAt(0));
        reader.LoadAsync((uint)ms.Size).AsTask().GetAwaiter().GetResult();
        var bytes = new byte[ms.Size];
        reader.ReadBytes(bytes);
        File.WriteAllBytes(path, bytes);
    }
}
