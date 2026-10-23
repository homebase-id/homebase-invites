namespace Raven.Hosting;

/// <summary>
/// The settings under their names from before the service was Raven Hosting (homebase-invites), still read so a
/// deploy that sets them keeps working until it is changed. A setting given under its new name wins; given under
/// both names with different values, the service refuses to start rather than guess.
/// </summary>
public static class RenamedSettings
{
    /// <summary>Each setting's name now, and its name before</summary>
    public static readonly (string Key, string Old)[] Renamed =
    [
        ("RavenHosting:DatabasePath", "InvitationCodes:DatabasePath"),
        ("RavenHosting:SignupApiKey", "InvitationCodes:SignupApiKey"),
        ("RavenHosting:CreateApiKey", "InvitationCodes:CreateApiKey"),
        ("RavenHosting:RequestsPerMinutePerIp", "InvitationCodes:RequestsPerMinutePerIp"),
        ("RavenHosting:IdentityHosts", "PlanService:Hosts"),
        ("RavenHosting:AccountServiceApiKey", "PlanService:ApiKey"),
        ("RavenHosting:GrantRetryIntervalSeconds", "PlanService:RetryIntervalSeconds"),
        ("RavenHosting:ReconcileIntervalHours", "PlanService:ReconcileIntervalHours"),
        ("RavenHosting:PublicDomain", "PlanService:PublicDomain"),
        ("RavenHosting:DisplayName", "PlanService:DisplayName"),
    ];

    /// <summary>
    /// Gives each setting left out under its new name the value of its old one
    /// </summary>
    /// <returns>The old names that were used, to warn about</returns>
    public static List<string> Apply(ConfigurationManager configuration)
    {
        var used = new List<string>();
        var values = new Dictionary<string, string?>();
        foreach (var (key, old) in Renamed)
        {
            var before = Values(configuration.GetSection(old));
            if (before.Count == 0)
            {
                continue;
            }

            var now = Values(configuration.GetSection(key));
            if (now.Count == 0)
            {
                foreach (var (path, value) in before)
                {
                    values[path == "" ? key : $"{key}:{path}"] = value;
                }
                used.Add(old);
            }
            else if (now.Count != before.Count || now.Any(v => !before.TryGetValue(v.Key, out var b) || b != v.Value))
            {
                throw new InvalidOperationException(
                    $"{key} and {old}, its name before, are both set, to different values; remove {old}");
            }
        }

        if (values.Count > 0)
        {
            configuration.AddInMemoryCollection(values);
        }
        return used;
    }

    // A setting's value, or a list's items by index ("0", "1"...), by path below the section
    private static Dictionary<string, string> Values(IConfigurationSection section)
    {
        var values = section.AsEnumerable(makePathsRelative: true)
            .Where(v => v.Value != null && v.Key != "")
            .ToDictionary(v => v.Key, v => v.Value!);
        if (section.Value != null)
        {
            values[""] = section.Value;
        }
        return values;
    }
}
