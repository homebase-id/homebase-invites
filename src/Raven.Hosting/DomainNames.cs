using System.Text.RegularExpressions;

namespace Raven.Hosting;

/// <summary>
/// Domain names as the service takes them: identities, code owners, and the identity hosts' provisioning domains
/// </summary>
public static class DomainNames
{
    // Two or more labels of letters, digits and inner hyphens, each 1-63 long
    private static readonly Regex DomainName =
        new(@"^(?:[a-z0-9](?:[a-z0-9-]{0,61}[a-z0-9])?\.)+[a-z0-9](?:[a-z0-9-]{0,61}[a-z0-9])?$", RegexOptions.Compiled);

    /// <summary>
    /// Trimmed and lower-cased; null when it cannot be a domain name
    /// </summary>
    public static string? Normalize(string? name)
    {
        var normalized = name?.Trim().ToLowerInvariant();
        return normalized is { Length: > 0 and <= 253 } && DomainName.IsMatch(normalized) ? normalized : null;
    }
}
