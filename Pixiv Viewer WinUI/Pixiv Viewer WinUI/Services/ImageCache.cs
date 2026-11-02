using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Storage.Streams;

namespace Pixiv_Viewer_WinUI.Services;

/// <summary>
/// 图片缓存：内存（BitmapImage）+ 磁盘（%LOCALAPPDATA%\PixivViewer\images）。
/// 同一个 URL 只请求一次反代；解码时按 <c>decodeWidth</c> 降分辨率，避免大图拖慢滚动。
/// </summary>
public static class ImageCache
{
    /// <summary>图片下载客户端，由 <see cref="AppServices"/> 按当前接口方式注入。</summary>
    public static HttpClient Client { get; set; } = new() { Timeout = TimeSpan.FromSeconds(45) };

    private static readonly Dictionary<string, BitmapImage> Memory = new();
    private static readonly Dictionary<string, Task<BitmapImage?>> Pending = new();

    private static readonly string Root = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "PixivViewer",
        "images");

    /// <summary>缓存目录（设置页展示占用用）。</summary>
    public static string Folder => Root;

    public static long GetCacheSize()
    {
        try
        {
            return Directory.Exists(Root)
                ? new DirectoryInfo(Root).EnumerateFiles().Sum(f => f.Length)
                : 0;
        }
        catch (Exception)
        {
            return 0;
        }
    }

    /// <summary>清空内存 + 磁盘缓存。</summary>
    public static void Clear()
    {
        lock (Memory)
        {
            Memory.Clear();
        }

        try
        {
            if (Directory.Exists(Root))
            {
                Directory.Delete(Root, true);
            }
        }
        catch (Exception)
        {
            // 文件被占用时忽略，下次再清。
        }
    }

    public static async Task<BitmapImage?> GetAsync(string? url, int decodeWidth = 480)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            return null;
        }

        var key = $"{url}#{decodeWidth}";

        lock (Memory)
        {
            if (Memory.TryGetValue(key, out var cached))
            {
                return cached;
            }
        }

        Task<BitmapImage?> task;
        lock (Pending)
        {
            if (!Pending.TryGetValue(key, out task!))
            {
                task = LoadAsync(url, decodeWidth);
                Pending[key] = task;
            }
        }

        var bitmap = await task;

        lock (Pending)
        {
            Pending.Remove(key);
        }

        return bitmap;
    }

    private static async Task<BitmapImage?> LoadAsync(string url, int decodeWidth)
    {
        // 缓存文件可能损坏（半截下载 / 代理错误页），失败时删掉重下一次
        for (var attempt = 0; attempt < 2; attempt++)
        {
            var file = Path.Combine(Root, Hash(url) + ".img");

            try
            {
                if (attempt > 0 || !File.Exists(file) || new FileInfo(file).Length == 0)
                {
                    Directory.CreateDirectory(Root);
                    var bytes = await Client.GetByteArrayAsync(url);
                    await File.WriteAllBytesAsync(file, bytes);
                }

                var bitmap = new BitmapImage
                {
                    DecodePixelType = DecodePixelType.Logical,
                    DecodePixelWidth = decodeWidth > 0 ? decodeWidth : 480,
                };

                using (var stream = File.OpenRead(file))
                using (var random = stream.AsRandomAccessStream())
                {
                    await bitmap.SetSourceAsync(random);
                }

                lock (Memory)
                {
                    if (Memory.Count > 120)
                    {
                        Memory.Clear();
                    }

                    Memory[$"{url}#{decodeWidth}"] = bitmap;
                }

                return bitmap;
            }
            catch (Exception)
            {
                try
                {
                    if (File.Exists(file))
                    {
                        File.Delete(file);
                    }
                }
                catch (Exception)
                {
                    // 删除失败就算了，下一轮会重新下载覆盖
                }
            }
        }

        return null;
    }

    private static string Hash(string url)
    {
        var bytes = SHA1.HashData(Encoding.UTF8.GetBytes(url));
        return Convert.ToHexString(bytes)[..24].ToLowerInvariant();
    }
}
