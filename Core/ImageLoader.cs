using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using ImageMagick;

namespace Pinshot.Core;

/// <summary>加载结果：GDI 位图（OCR/保存/静态复制用）+ 动画 GIF 原始字节（有则为动图）。</summary>
public sealed record LoadedImage(System.Drawing.Bitmap Bitmap, byte[]? GifBytes)
{
    public bool IsAnimated => GifBytes != null;
}

/// <summary>
/// 贴图图片来源加载：本地文件（JPEG/PNG/GIF/BMP/ICO/TIFF 原生，WebP/PSD/TGA/SVG 走 ImageMagick）
/// 与网页图片 URL 下载。
/// </summary>
public static class ImageLoader
{
    private const long MaxDownloadBytes = 30 * 1024 * 1024;

    public static LoadedImage LoadFromFile(string path)
    {
        var bytes = File.ReadAllBytes(path);
        return LoadFromBytes(bytes);
    }

    public static LoadedImage LoadFromBytes(byte[] bytes)
    {
        // 先试 WPF 原生解码（PNG/JPEG/BMP/GIF/ICO/TIFF）
        try
        {
            using var stream = new MemoryStream(bytes);
            var decoder = System.Windows.Media.Imaging.BitmapDecoder.Create(
                stream, System.Windows.Media.Imaging.BitmapCreateOptions.None,
                System.Windows.Media.Imaging.BitmapCacheOption.OnLoad);
            var mime = decoder.CodecInfo.MimeTypes?.Split(',')[0]?.ToLowerInvariant();
            var isGif = mime == "image/gif";
            var frame = decoder.Frames[0];
            frame.Freeze();

            var gdi = BitmapFromSource(frame);
            if (isGif)
            {
                // GIF：静态帧用于 OCR/保存兜底，动画交给 XamlAnimatedGif 播放
                try
                {
                    using var magick = new MagickImage(bytes);
                    if (magick.AnimationDelay > 0 || HasMultipleFrames(bytes))
                        return new LoadedImage(gdi, bytes);
                }
                catch
                {
                    // 单帧 GIF 按静态处理
                }
            }
            return new LoadedImage(gdi, null);
        }
        catch
        {
            // 原生不支持，转 ImageMagick（WebP/PSD/TGA/SVG 等）
        }

        using var image = new MagickImage(bytes)
        {
            BackgroundColor = MagickColors.Transparent,
        };
        using var pngStream = new MemoryStream();
        image.Write(pngStream, MagickFormat.Png);
        pngStream.Position = 0;
        return new LoadedImage(new System.Drawing.Bitmap(pngStream), null);
    }

    /// <summary>
    /// 从 URL 下载图片。仅允许 http/https，且拒绝 localhost / 环回 / 私有 / 保留地址，
    /// 防止拖拽网页内容时被诱导访问内网。
    /// </summary>
    public static async Task<LoadedImage> DownloadAsync(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
            throw new InvalidOperationException("仅支持 http/https 图片链接。");
        ValidateHost(uri.Host);

        using var handler = new HttpClientHandler
        {
            AutomaticDecompression = DecompressionMethods.All,
            AllowAutoRedirect = true,
            MaxAutomaticRedirections = 5,
        };
        using var http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(20) };
        http.DefaultRequestHeaders.Add("User-Agent", "Mozilla/5.0 (compatible; Pinshot/0.1)");
        using var response = await http.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead);
        response.EnsureSuccessStatusCode();
        if (response.Content.Headers.ContentLength is > MaxDownloadBytes)
            throw new InvalidOperationException("图片超过 30MB 上限。");

        var contentType = response.Content.Headers.ContentType?.MediaType?.ToLowerInvariant();
        if (contentType != null && !contentType.StartsWith("image/"))
            throw new InvalidOperationException($"目标不是图片（Content-Type: {contentType}）。");

        await using var stream = await response.Content.ReadAsStreamAsync();
        using var buffered = new MemoryStream();
        await stream.CopyToAsync(buffered);
        if (buffered.Length > MaxDownloadBytes)
            throw new InvalidOperationException("图片超过 30MB 上限。");
        return LoadFromBytes(buffered.ToArray());
    }

    private static void ValidateHost(string host)
    {
        if (string.IsNullOrWhiteSpace(host) ||
            host.Equals("localhost", StringComparison.OrdinalIgnoreCase) ||
            host.EndsWith(".localhost", StringComparison.OrdinalIgnoreCase) ||
            host.Equals("intranet", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("拒绝访问内网地址。");

        // 主机名解析后再校验 IP（重定向后的实际地址由 handler 控制，此处校验初始目标）
        IPAddress[] addresses;
        try
        {
            addresses = Dns.GetHostAddresses(host);
        }
        catch (SocketException ex)
        {
            throw new InvalidOperationException($"无法解析主机 {host}：{ex.Message}");
        }
        foreach (var address in addresses)
        {
            if (IsBlockedAddress(address))
                throw new InvalidOperationException("拒绝访问内网 / 保留地址。");
        }
    }

    internal static bool IsBlockedAddress(IPAddress address)
    {
        if (address.IsIPv6LinkLocal || address.IsIPv6SiteLocal)
            return true;
        var bytes = address.GetAddressBytes();
        if (address.AddressFamily == AddressFamily.InterNetwork)
        {
            // 0.0.0.0/8, 10/8, 127/8, 169.254/16, 172.16/12, 192.168/16, 224/4, 240/4
            return bytes[0] == 0 || bytes[0] == 10 || bytes[0] == 127 ||
                   (bytes[0] == 169 && bytes[1] == 254) ||
                   (bytes[0] == 172 && bytes[1] >= 16 && bytes[1] <= 31) ||
                   (bytes[0] == 192 && bytes[1] == 168) ||
                   bytes[0] >= 224;
        }
        if (address.AddressFamily == AddressFamily.InterNetworkV6)
        {
            // ::, ::1, fc00::/7 (唯一本地), fe80::/10 (链路本地)
            if (address.Equals(IPAddress.IPv6Any) || address.Equals(IPAddress.IPv6Loopback))
                return true;
            return (bytes[0] & 0xFE) == 0xFC || (bytes[0] == 0xFE && (bytes[1] & 0xC0) == 0x80);
        }
        return true;
    }

    private static bool HasMultipleFrames(byte[] gifBytes)
    {
        // 粗略统计 GIF 的图像分隔符 0x2C 数量
        var count = 0;
        for (var i = 0; i < gifBytes.Length - 1; i++)
        {
            if (gifBytes[i] == 0x21 && gifBytes[i + 1] == 0xF9) // Graphic Control Extension（每帧一个）
                count++;
        }
        return count > 1;
    }

    /// <summary>从字节生成小缩略图（贴图列表/回收站用），失败返回 null。</summary>
    public static System.Windows.Media.Imaging.BitmapSource? ThumbnailOf(byte[] bytes)
    {
        try
        {
            using var stream = new MemoryStream(bytes);
            var decoder = System.Windows.Media.Imaging.BitmapDecoder.Create(
                stream,
                System.Windows.Media.Imaging.BitmapCreateOptions.None,
                System.Windows.Media.Imaging.BitmapCacheOption.OnLoad);
            var frame = decoder.Frames[0];
            frame.Freeze();
            var scale = Math.Min(1.0, 96.0 / Math.Max(1, Math.Max(frame.PixelWidth, frame.PixelHeight)));
            var thumbnail = new System.Windows.Media.Imaging.TransformedBitmap(
                frame, new System.Windows.Media.ScaleTransform(scale, scale));
            thumbnail.Freeze();
            return thumbnail;
        }
        catch
        {
            return null;
        }
    }

    internal static System.Drawing.Bitmap BitmapFromSource(System.Windows.Media.Imaging.BitmapSource source)
    {
        var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
        encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(source));
        using var stream = new MemoryStream();
        encoder.Save(stream);
        stream.Position = 0;
        return new System.Drawing.Bitmap(stream);
    }
}
