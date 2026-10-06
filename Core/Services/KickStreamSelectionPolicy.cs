namespace Core.Services;

public static class KickStreamSelectionPolicy
{
    private static string? Channel(string? url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)
            || (uri.Scheme != "https" && uri.Scheme != "http")
            || (uri.Host != "kick.com" && uri.Host != "www.kick.com")) return null;
        var parts = uri.AbsolutePath.Trim('/').Split('/');
        if (parts.Length != 1 || string.IsNullOrWhiteSpace(parts[0])) return null;
        var login = parts[0].ToLowerInvariant();
        return login is "category" or "browse" or "directory" or "search" or "videos" or "clips" or "drops" ? null : login;
    }

    public static bool IsSelectedChannelPage(string? pageUrl, string selectedUrl)
        => Channel(selectedUrl) is { } selected && Channel(pageUrl) == selected;

    public static string SelectDirectoryChannel(IEnumerable<string> urls, Func<string, bool> blocked)
        => urls.FirstOrDefault(url => Channel(url) is { } login && !blocked(login)) ?? string.Empty;
}
