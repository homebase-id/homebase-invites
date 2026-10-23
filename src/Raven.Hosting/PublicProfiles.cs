using System.Text.Json;

namespace Raven.Hosting;

/// <summary>
/// What an identity shows anyone about itself: the profile card its owner publishes (odin-core
/// <c>GET https://{identity}/pub/profile</c>), and its photo. The card holds only what the owner made public; an identity
/// that never published one answers 404.
/// </summary>
public class PublicProfiles(IHttpClientFactory httpClientFactory, ILogger<PublicProfiles> logger)
{
    // Longer names are cut, so a card cannot fill the page or the session cookie
    private const int LongestName = 80;

    /// <summary>
    /// The photo, for an img tag: the identity serves a placeholder when there is none, so it always shows something
    /// </summary>
    public static string ImageUrl(string identity) => $"https://{identity}/pub/image";

    /// <summary>
    /// The display name on the identity's public card; null when it has none of its own (the card then names the
    /// identity), has no card, or cannot be reached. Best effort: it never throws.
    /// </summary>
    public async Task<string?> NameAsync(string identity)
    {
        try
        {
            using var cancel = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            using var response = await httpClientFactory.CreateClient(YouAuthLogin.HttpClientName)
                .GetAsync($"https://{identity}/pub/profile", cancel.Token);
            if (!response.IsSuccessStatusCode)
            {
                logger.LogDebug("Public profile of {Identity}: {Status}", identity, (int)response.StatusCode);
                return null;
            }

            using var card = JsonDocument.Parse(await response.Content.ReadAsStreamAsync(cancel.Token));
            if (card.RootElement.ValueKind != JsonValueKind.Object ||
                !card.RootElement.TryGetProperty("name", out var nameElement) ||
                nameElement.ValueKind != JsonValueKind.String)
            {
                return null;
            }

            var name = nameElement.GetString()!.Trim();
            if (name == "" || string.Equals(name, identity, StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }
            return name.Length > LongestName ? name[..LongestName].TrimEnd() + "…" : name;
        }
        catch (Exception e) when (e is HttpRequestException or OperationCanceledException or JsonException)
        {
            logger.LogDebug("Public profile of {Identity}: {Error}", identity, e.Message);
            return null;
        }
    }
}
