using System.Globalization;
using System.Security.Cryptography;

namespace Lumi.Services.Remote;

internal sealed class RemoteWebAssetProvider
{
    private readonly Dictionary<string, RemoteWebAsset> _assets;

    internal RemoteWebAssetProvider(string root)
    {
        Root = root;
        _assets = Directory
            .EnumerateFiles(root, "*", SearchOption.AllDirectories)
            .Where(path => !path.EndsWith(".br", StringComparison.OrdinalIgnoreCase)
                           && !path.EndsWith(".gz", StringComparison.OrdinalIgnoreCase))
            .ToDictionary(
                path => NormalizeRelativePath(Path.GetRelativePath(root, path)),
                CreateAsset,
                StringComparer.Ordinal);
    }

    public string Root { get; }

    public static RemoteWebAssetProvider? TryCreate()
    {
        var configured = Environment.GetEnvironmentVariable("LUMI_REMOTE_WEB_ROOT");
        var root = string.IsNullOrWhiteSpace(configured)
            ? ResolveDefaultRoot(Environment.ProcessPath, AppContext.BaseDirectory)
            : configured;
        if (!Directory.Exists(root))
            return null;

        try
        {
            return new RemoteWebAssetProvider(Path.GetFullPath(root));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            System.Diagnostics.Trace.TraceWarning($"[Remote] Could not load PWA assets: {ex.Message}");
            return null;
        }
    }

    internal static string ResolveDefaultRoot(
        string? processPath,
        string appContextBaseDirectory)
    {
        var executableDirectory = string.IsNullOrWhiteSpace(processPath)
            ? null
            : Path.GetDirectoryName(processPath);
        return Path.Combine(executableDirectory ?? appContextBaseDirectory, "remote-web");
    }

    public bool TryGet(string relativePath, RemoteHttpRequest request, out RemoteWebAsset asset)
    {
        asset = null!;
        if (!TryNormalizeRequestPath(relativePath, out var key)
            || !_assets.TryGetValue(key, out var original))
        {
            return false;
        }

        if (request.AcceptsBrotli() && File.Exists(original.FullPath + ".br"))
        {
            asset = original.WithEncodedFile(original.FullPath + ".br", "br");
            return true;
        }

        if (request.AcceptsGzip() && File.Exists(original.FullPath + ".gz"))
        {
            asset = original.WithEncodedFile(original.FullPath + ".gz", "gzip");
            return true;
        }

        asset = original;
        return true;
    }

    private static RemoteWebAsset CreateAsset(string path)
    {
        var info = new FileInfo(path);
        var extension = Path.GetExtension(path).ToLowerInvariant();
        var fileName = info.Name;
        var cacheControl =
            string.Equals(fileName, "index.html", StringComparison.OrdinalIgnoreCase)
            || extension is ".webmanifest"
                ? "no-cache"
                : HasContentHash(fileName)
                    ? "public, max-age=31536000, immutable"
                    : "no-cache";
        var etagSource = string.Create(
            CultureInfo.InvariantCulture,
            $"{info.Length:x}-{info.LastWriteTimeUtc.Ticks:x}");
        var etag = $"\"{Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(etagSource)))[..24]}\"";
        return new RemoteWebAsset(
            path,
            ContentType(extension),
            cacheControl,
            etag,
            ContentEncoding: null,
            info.Length);
    }

    private static bool HasContentHash(string fileName)
    {
        var segments = fileName.Split('.', StringSplitOptions.RemoveEmptyEntries);
        return segments.Any(segment =>
            segment.Length is >= 8 and <= 20
            && segment.Any(char.IsDigit)
            && segment.All(static character =>
                character is >= 'a' and <= 'z'
                || character is >= '0' and <= '9'));
    }

    private static string NormalizeRelativePath(string path) =>
        path.Replace(Path.DirectorySeparatorChar, '/');

    private static bool TryNormalizeRequestPath(string path, out string normalized)
    {
        normalized = "";
        try
        {
            path = Uri.UnescapeDataString(path);
        }
        catch (UriFormatException)
        {
            return false;
        }

        if (path.IndexOfAny(['\\', '\0']) >= 0)
            return false;

        var segments = path
            .Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Any(segment => segment is "." or ".."))
            return false;

        normalized = string.Join('/', segments);
        return normalized.Length > 0;
    }

    private static string ContentType(string extension) => extension switch
    {
        ".html" => "text/html; charset=utf-8",
        ".js" or ".mjs" => "text/javascript; charset=utf-8",
        ".css" => "text/css; charset=utf-8",
        ".wasm" => "application/wasm",
        ".json" => "application/json; charset=utf-8",
        ".webmanifest" => "application/manifest+json",
        ".woff2" => "font/woff2",
        ".png" => "image/png",
        ".jpg" or ".jpeg" => "image/jpeg",
        ".svg" => "image/svg+xml",
        ".webp" => "image/webp",
        ".ico" => "image/x-icon",
        _ => "application/octet-stream"
    };
}

internal sealed record RemoteWebAsset(
    string FullPath,
    string ContentType,
    string CacheControl,
    string ETag,
    string? ContentEncoding,
    long Length)
{
    public RemoteWebAsset WithEncodedFile(string path, string encoding)
    {
        var info = new FileInfo(path);
        return this with
        {
            FullPath = path,
            ContentEncoding = encoding,
            Length = info.Length
        };
    }
}
