namespace Boo.Providers;

internal static class ProviderEndpoint
{
    internal static readonly Uri Origin = new("https://api.openai.com/");

    internal static bool IsOfficial(Uri uri) => uri.Scheme == Uri.UriSchemeHttps &&
        string.Equals(uri.Host, Origin.Host, StringComparison.OrdinalIgnoreCase) &&
        (uri.IsDefaultPort || uri.Port == 443);
}
