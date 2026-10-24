using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace GameColorGuard.Core;

/// <summary>
/// Uzak görseller (oyuncu fotoğrafı, takım logosu, resmi oyun ikonu): yalnız izin verilen sitelerden indirilir,
/// %LocalAppData%\CS2ColorGuard\cache altında saklanır ve bir daha indirilmez. Depoya hiçbir görsel konmaz.
/// </summary>
internal static class ImageCache
{
    private static readonly string[] AllowedHosts = { "prosettings.net", "www.counter-strike.net", "cmsassets.rgpub.io" };
    private static readonly string Folder = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CS2ColorGuard", "cache");
    private static readonly HttpClient Http = CreateClient();
    private static readonly SemaphoreSlim Gate = new(4);
    private static readonly Dictionary<string, Task<ImageSource?>> Loads = new();
    private static readonly Dictionary<string, ImageSource?> Memory = new();

    /// <summary>Bellekteki çözülmüş görselleri bırakır (pencere tepsiye inince); disk önbelleği kalır.</summary>
    public static void ReleaseMemory()
    {
        lock (Memory) Memory.Clear();
        lock (Loads)
        {
            foreach (string key in Loads.Where(p => p.Value.IsCompleted).Select(p => p.Key).ToList()) Loads.Remove(key);
        }
    }

    /// <summary>Önbellekte varsa hemen döner (çizim testinde ve hızlı açılışta kullanılır); yoksa null.</summary>
    public static ImageSource? TryCached(string? url, int size)
    {
        if (string.IsNullOrEmpty(url)) return null;
        string key = url + "|" + size;
        lock (Memory)
        {
            if (Memory.TryGetValue(key, out ImageSource? hit)) return hit;
        }
        string file = CachePath(url);
        if (!File.Exists(file)) return null;
        ImageSource? image = Decode(File.ReadAllBytes(file), size);
        lock (Memory) Memory[key] = image;
        return image;
    }

    /// <summary>Görseli indirir (en fazla 4 eşzamanlı), önbelleğe yazar ve çözer. Hata durumunda null.</summary>
    public static Task<ImageSource?> GetAsync(string? url, int size)
    {
        if (string.IsNullOrEmpty(url) || !Uri.TryCreate(url, UriKind.Absolute, out Uri? uri) || uri.Scheme != Uri.UriSchemeHttps ||
            !AllowedHosts.Any(h => uri.Host.Equals(h, StringComparison.OrdinalIgnoreCase)))
            return Task.FromResult<ImageSource?>(null);

        string key = url + "|" + size;
        lock (Loads)
        {
            if (Loads.TryGetValue(key, out Task<ImageSource?>? running)) return running;
            Task<ImageSource?> task = Task.Run(async () =>
            {
                try
                {
                    if (TryCached(url, size) is ImageSource cached) return cached;
                    await Gate.WaitAsync().ConfigureAwait(false);
                    byte[] bytes;
                    try { bytes = await Http.GetByteArrayAsync(uri).ConfigureAwait(false); }
                    finally { Gate.Release(); }
                    ImageSource? image = Decode(bytes, size);
                    if (image is null) return null;
                    Directory.CreateDirectory(Folder);
                    await File.WriteAllBytesAsync(CachePath(url), bytes).ConfigureAwait(false);
                    lock (Memory) Memory[key] = image;
                    return image;
                }
                catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException or NotSupportedException)
                {
                    return null;   // çevrimdışı ya da görsel çözülemedi: yedek rozet kalır
                }
            });
            Loads[key] = task;
            return task;
        }
    }

    /// <summary>WIC ile çözer (PNG, JPEG, WebP, ICO); çok çerçeveli ikonlarda en büyüğünü seçer ve boyuta indirir.</summary>
    private static ImageSource? Decode(byte[] bytes, int size)
    {
        if (SvgLite.LooksLikeSvg(bytes)) return SvgLite.Render(bytes);
        try
        {
            using var stream = new MemoryStream(bytes);
            BitmapDecoder decoder = BitmapDecoder.Create(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
            BitmapSource frame = decoder.Frames.OrderByDescending(f => f.PixelWidth).First();
            double target = size * 2.0;   // yüksek DPI ekranlar için iki kat
            if (frame.PixelWidth > target)
            {
                double scale = target / frame.PixelWidth;
                frame = new TransformedBitmap(frame, new ScaleTransform(scale, scale));
            }
            frame.Freeze();
            return frame;
        }
        catch
        {
            return null;
        }
    }

    private static string CachePath(string url)
    {
        string hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(url)))[..32].ToLowerInvariant();
        string extension = Path.GetExtension(new Uri(url).AbsolutePath);
        if (extension.Length is 0 or > 5) extension = ".img";
        return Path.Combine(Folder, hash + extension);
    }

    private static HttpClient CreateClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
        // Cloudflare, tarayıcı biçiminde olmayan istemcileri doğrulama sayfasına yönlendiriyor (403); uygulama adı yine de görünür.
        client.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) GameColorGuard/3.2");
        return client;
    }
}

/// <summary>Resmi oyun ikonları: önce kurulu oyunun exe'sinden (64 px), olmazsa oyunun resmi sitesinden.</summary>
internal static class GameIcons
{
    private static readonly Dictionary<string, ImageSource?> FromFiles = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Bilinen oyunların resmi site ikonu (oyun kurulu değilse kullanılır).</summary>
    public static string? OfficialIconUrl(string executable) => executable.ToLowerInvariant() switch
    {
        "cs2.exe" => "https://www.counter-strike.net/favicon.ico",
        "valorant-win64-shipping.exe" or "valorant.exe" =>
            "https://cmsassets.rgpub.io/sanity/images/dsfx7636/news/cbf4460132cdfeb2a97fad5f9dd25ba0bc058f76-128x128.png?accountingTag=VAL",
        _ => null
    };

    /// <summary>Oyunun kurulu exe yolu: öğrenilmiş yol, yoksa CS2 için Steam, VALORANT için Riot kaydı.</summary>
    public static string? InstalledPath(string executable, IReadOnlyDictionary<string, string> knownPaths)
    {
        if (knownPaths.TryGetValue(executable, out string? known) && File.Exists(known)) return known;
        return executable.ToLowerInvariant() switch
        {
            "cs2.exe" => ProConfig.FindCs2Executable(null),
            "valorant-win64-shipping.exe" => ProConfig.FindValorantExecutable(null),
            _ => null
        };
    }

    public static ImageSource? FromExe(string? path, int size = 64)
    {
        if (string.IsNullOrEmpty(path)) return null;
        lock (FromFiles)
        {
            if (FromFiles.TryGetValue(path + "|" + size, out ImageSource? cached)) return cached;
        }
        ImageSource? image = null;
        try
        {
            using System.Drawing.Icon? icon = System.Drawing.Icon.ExtractIcon(path, 0, size)
                                              ?? System.Drawing.Icon.ExtractAssociatedIcon(path);
            if (icon is not null)
            {
                BitmapSource source = Imaging.CreateBitmapSourceFromHIcon(icon.Handle, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
                source.Freeze();
                image = source;
            }
        }
        catch
        {
            image = null;
        }
        lock (FromFiles) FromFiles[path + "|" + size] = image;
        return image;
    }

    /// <summary>Hemen gösterilebilecek ikon (exe ya da önbellek); yoksa null ve <paramref name="pending"/> indirmeyi başlatır.</summary>
    public static ImageSource? Resolve(string executable, IReadOnlyDictionary<string, string> knownPaths, int size, out Task<ImageSource?>? pending)
    {
        pending = null;
        if (FromExe(InstalledPath(executable, knownPaths), Math.Max(32, size * 2)) is ImageSource local) return local;
        string? url = OfficialIconUrl(executable);
        if (url is null) return null;
        if (ImageCache.TryCached(url, size) is ImageSource cached) return cached;
        pending = ImageCache.GetAsync(url, size);
        return null;
    }
}
