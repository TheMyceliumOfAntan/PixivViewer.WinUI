using System;
using System.IO;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Threading.Tasks;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;

namespace Pixiv_Viewer_WinUI.Services.Manga;

/// <summary>BGRA8 位图（WIC 解码/编码）。像素操作在托管数组上做，不依赖额外图形库。</summary>
internal sealed class MangaBitmap
{
    public MangaBitmap(int width, int height, byte[] pixels)
    {
        Width = width;
        Height = height;
        Pixels = pixels;
    }

    public int Width { get; }

    public int Height { get; }

    public byte[] Pixels { get; }

    /// <summary>解码为 BGRA8，超过 <paramref name="maxDimension"/> 时等比缩小（速度优先）。</summary>
    public static async Task<MangaBitmap> DecodeAsync(byte[] bytes, int maxDimension = 2000)
    {
        using var stream = new InMemoryRandomAccessStream();
        using (var writer = new DataWriter(stream.GetOutputStreamAt(0)))
        {
            writer.WriteBytes(bytes);
            await writer.StoreAsync();
            writer.DetachStream();
        }

        stream.Seek(0);
        var decoder = await BitmapDecoder.CreateAsync(stream);

        var transform = new BitmapTransform();
        var longest = Math.Max(decoder.PixelWidth, decoder.PixelHeight);
        if (maxDimension > 0 && longest > maxDimension)
        {
            var ratio = (double)maxDimension / longest;
            transform.ScaledWidth = (uint)Math.Round(decoder.PixelWidth * ratio);
            transform.ScaledHeight = (uint)Math.Round(decoder.PixelHeight * ratio);
            transform.InterpolationMode = BitmapInterpolationMode.Linear;
        }

        var data = await decoder.GetPixelDataAsync(
            BitmapPixelFormat.Bgra8,
            BitmapAlphaMode.Ignore,
            transform,
            ExifOrientationMode.IgnoreExifOrientation,
            ColorManagementMode.DoNotColorManage);

        var pixels = data.DetachPixelData();
        var width = transform.ScaledWidth > 0 ? (int)transform.ScaledWidth : (int)decoder.PixelWidth;
        var height = transform.ScaledHeight > 0 ? (int)transform.ScaledHeight : (int)decoder.PixelHeight;
        return new MangaBitmap(width, height, pixels);
    }

    public byte[] Crop(int x, int y, int width, int height)
    {
        var dst = new byte[width * height * 4];
        for (var row = 0; row < height; row++)
        {
            var sourceIndex = ((y + row) * Width + x) * 4;
            System.Buffer.BlockCopy(Pixels, sourceIndex, dst, row * width * 4, width * 4);
        }

        return dst;
    }

    public async Task SavePngAsync(string path, byte[] pixels)
    {
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        await File.WriteAllBytesAsync(path, await ToPngBytesAsync(Width, Height, pixels));
    }

    public static async Task<byte[]> ToPngBytesAsync(int width, int height, byte[] pixels)
    {
        using var stream = new InMemoryRandomAccessStream();
        var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, stream);
        encoder.SetPixelData(
            BitmapPixelFormat.Bgra8,
            BitmapAlphaMode.Ignore,
            (uint)width,
            (uint)height,
            96,
            96,
            pixels);
        await encoder.FlushAsync();

        stream.Seek(0);
        using var buffer = new MemoryStream();
        await stream.AsStreamForRead().CopyToAsync(buffer);
        return buffer.ToArray();
    }
}
