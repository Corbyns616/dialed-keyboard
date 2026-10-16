using System.Net.Http;
using System.Text.Json;

namespace DialedKeyboard.Stickers;

/// <param name="ImageUrl">The full-size picture, hosted by whoever published it.</param>
/// <param name="ThumbnailUrl">A small preview, served by Openverse.</param>
public sealed record WebImage(string Title, string ImageUrl, string ThumbnailUrl, string Creator, string License);

/// <param name="Pages">How many pages this search has. Openverse allows 12 pages of 20 without an account.</param>
public sealed record SearchPage(List<WebImage> Images, int Pages);

/// <summary>
/// Picture search through Openverse (openverse.org), WordPress's free search of openly licensed
/// images. No account or key is needed, and mature results are left out.
/// </summary>
public static class StickerSearch
{
    const long MaxDownload = 25 * 1024 * 1024;

    static readonly HttpClient Http = CreateClient();

    static HttpClient CreateClient()
    {
        var http = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("DialedKeyboard/1.0 (+https://github.com/Corbyns616/dialed-keyboard)");
        return http;
    }

    /// <summary>One page (20 pictures) of results; <paramref name="page"/> counts from 1.</summary>
    public static async Task<SearchPage> SearchAsync(string query, int page = 1, CancellationToken ct = default)
    {
        string url = $"https://api.openverse.org/v1/images/?page_size=20&page={page}&q=" + Uri.EscapeDataString(query);
        using var response = await Http.GetAsync(url, ct);
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);

        var images = new List<WebImage>();
        int pages = doc.RootElement.TryGetProperty("page_count", out var count) && count.TryGetInt32(out int n) ? n : 1;
        if (!doc.RootElement.TryGetProperty("results", out var results)) return new SearchPage(images, pages);
        foreach (var r in results.EnumerateArray())
        {
            string? image = Str(r, "url"), thumb = Str(r, "thumbnail");
            if (!IsWeb(image) || !IsWeb(thumb)) continue;
            images.Add(new WebImage(Str(r, "title") ?? "", image!, thumb!, Str(r, "creator") ?? "", Str(r, "license") ?? ""));
        }
        return new SearchPage(images, pages);
    }

    /// <summary>Where to get a small preview, best first: if one fails, try the next.</summary>
    public static IEnumerable<string> PreviewUrls(WebImage image)
    {
        // Openverse can't fetch previews of Wikimedia pictures, so ask Wikimedia directly.
        if (WikimediaThumbnail(image.ImageUrl, 330) is { } wiki) yield return wiki;
        yield return image.ThumbnailUrl;
        yield return image.ImageUrl;
    }

    /// <summary>Where to get the picture for a sticker, best first.</summary>
    public static IEnumerable<string> FullSizeUrls(WebImage image)
    {
        yield return image.ImageUrl;
        // A drawing (SVG) or an unusual format can't be opened as it is; Wikimedia's own copy can.
        if (WikimediaThumbnail(image.ImageUrl, 960) is { } wiki) yield return wiki;
        yield return image.ThumbnailUrl;
    }

    /// <summary>
    /// Wikimedia Commons' own resized copy of one of its pictures, or null for pictures hosted
    /// elsewhere. Wikimedia only serves its standard widths (such as 330 and 960).
    /// </summary>
    public static string? WikimediaThumbnail(string url, int width)
    {
        const string Commons = "https://upload.wikimedia.org/wikipedia/commons/";
        if (!url.StartsWith(Commons, StringComparison.Ordinal)) return null;
        var parts = url[Commons.Length..].Split('/');
        if (parts.Length != 3 || parts[0] == "thumb") return null;
        string name = parts[2];
        string thumb = $"{width}px-{name}" + (name.EndsWith(".svg", StringComparison.OrdinalIgnoreCase) ? ".png" : "");
        return $"{Commons}thumb/{parts[0]}/{parts[1]}/{name}/{thumb}";
    }

    /// <summary>Downloads a picture (refusing anything huge).</summary>
    public static async Task<byte[]> DownloadAsync(string url, CancellationToken ct = default)
    {
        using var response = await Http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();
        if (response.Content.Headers.ContentLength > MaxDownload) throw new IOException("That picture is too big.");
        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var buffer = new MemoryStream();
        var chunk = new byte[81920];
        int n;
        while ((n = await stream.ReadAsync(chunk, ct)) > 0)
        {
            buffer.Write(chunk, 0, n);
            if (buffer.Length > MaxDownload) throw new IOException("That picture is too big.");
        }
        return buffer.ToArray();
    }

    static string? Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    static bool IsWeb(string? url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var u) && (u.Scheme == Uri.UriSchemeHttps || u.Scheme == Uri.UriSchemeHttp);
}
