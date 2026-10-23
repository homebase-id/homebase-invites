using System.Globalization;

namespace Raven.Hosting;

/// <summary>
/// How full a plan is, in words people read without converting units: the share used as a percentage of the plan's
/// size ("12% of 500 GB"), and what is used and free in the plan's own unit ("60 GB used · 440 GB free"), never
/// "85.24 MB of 500.00 GB". The same rules as the owner console (odin-js planStorageText) and the app (chat-kmp
/// PlanStorage.kt).
/// </summary>
/// <param name="Percent">Whole percent used: 0 when nothing is, 99 until it is actually full, 100 when it is; null for
/// under 1%</param>
/// <param name="Size">The plan's size: "500 GB", "1 TB", "1.5 TB"</param>
/// <param name="Used">Used, in the plan's unit: "0.1 GB"</param>
/// <param name="UsedIsUnder">More than nothing is used, but it rounds to nothing: Used is the smallest step, to say
/// "under" it</param>
/// <param name="Free">Free, in the plan's unit: "499.9 GB"; "0 GB" when it is full</param>
public record StorageUse(int? Percent, string Size, string Used, bool UsedIsUnder, string Free)
{
    private static readonly string[] Units = ["B", "KB", "MB", "GB", "TB", "PB"];

    public string PercentText => Percent == null ? "Less than 1%" : $"{Percent}%";

    public string UsedText => UsedIsUnder ? $"under {Used}" : Used;

    public static StorageUse Of(long usedBytes, long quotaBytes)
    {
        var unit = 0;
        while (unit < Units.Length - 1 && quotaBytes >= Math.Pow(1024, unit + 1))
        {
            unit++;
        }
        var scale = Math.Pow(1024, unit);
        // Two decimals for a plan under 10 of its unit (1 TB), so 0.29 TB is not 0.3 TB; one otherwise
        var decimals = unit == 0 ? 0 : quotaBytes / scale < 10 ? 2 : 1;
        string Amount(double bytes, int places) =>
            Math.Round(bytes / scale, places, MidpointRounding.AwayFromZero)
                .ToString("0." + new string('#', places), CultureInfo.InvariantCulture) + " " + Units[unit];

        var used = Math.Max(0, usedBytes);
        var roundsToNothing = used > 0 && Math.Round(used / scale, decimals, MidpointRounding.AwayFromZero) == 0;
        return new StorageUse(
            PercentUsed(used, quotaBytes),
            Amount(quotaBytes, 1),
            Amount(roundsToNothing ? Math.Pow(10, -decimals) * scale : used, decimals),
            roundsToNothing,
            Amount(Math.Max(0, quotaBytes - used), decimals));
    }

    private static int? PercentUsed(long used, long quota)
    {
        if (quota <= 0 || used >= quota)
        {
            return 100;
        }
        if (used == 0)
        {
            return 0;
        }
        var percent = (double)used / quota * 100;
        return percent < 1 ? null : Math.Min(99, (int)Math.Round(percent, MidpointRounding.AwayFromZero));
    }
}
