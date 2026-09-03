namespace Lumi.Mobile.Services;

public sealed record MobileConnectionLaunchRequest(string BaseUrl)
{
    public static bool TryParse(string? uriText, out MobileConnectionLaunchRequest? request)
    {
        request = null;
        if (!Uri.TryCreate(uriText, UriKind.Absolute, out var uri)
            || !string.Equals(uri.Scheme, "lumi", StringComparison.OrdinalIgnoreCase)
            || !string.Equals(uri.Host, "connect", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var values = ParseQuery(uri.Query);
        if (!values.TryGetValue("server", out var server)
            || !Uri.TryCreate(server, UriKind.Absolute, out var serverUri)
            || (serverUri.Scheme != Uri.UriSchemeHttp
                && serverUri.Scheme != Uri.UriSchemeHttps)
            || serverUri.UserInfo.Length > 0
            || serverUri.IsLoopback)
        {
            return false;
        }

        request = new MobileConnectionLaunchRequest(
            LumiRemoteClient.NormalizeBaseUrl(serverUri.AbsoluteUri));
        return true;
    }

    private static Dictionary<string, string> ParseQuery(string query)
    {
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var pair in query.TrimStart('?').Split(
                     '&',
                     StringSplitOptions.RemoveEmptyEntries))
        {
            var separator = pair.IndexOf('=');
            var key = separator < 0 ? pair : pair[..separator];
            var value = separator < 0 ? "" : pair[(separator + 1)..];
            values[Uri.UnescapeDataString(key.Replace('+', ' '))] =
                Uri.UnescapeDataString(value.Replace('+', ' '));
        }

        return values;
    }
}
