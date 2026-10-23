using System.Net;
using System.Net.Http.Json;
using Microsoft.Extensions.Configuration;

namespace Raven.Hosting.Tests;

/// <summary>
/// The settings under their names from before the rename (homebase-invites), which a deploy may still set
/// </summary>
public class RenamedSettingsTests
{
    [Test]
    public void AnOldNameFillsInForANewOneLeftOut()
    {
        var configuration = Configuration(new()
        {
            ["InvitationCodes:DatabasePath"] = "/data/invitation-codes.db",
            ["PlanService:Hosts:0"] = "provisioning.one.example",
            ["PlanService:Hosts:1"] = "provisioning.two.example",
            ["RavenHosting:PublicDomain"] = "ravenhosting.cloud",
        });

        var used = RenamedSettings.Apply(configuration);

        Assert.That(used, Is.EquivalentTo(new[] { "InvitationCodes:DatabasePath", "PlanService:Hosts" }));
        Assert.That(configuration["RavenHosting:DatabasePath"], Is.EqualTo("/data/invitation-codes.db"));
        Assert.That(configuration.GetSection("RavenHosting:IdentityHosts").Get<string[]>(),
            Is.EqualTo(new[] { "provisioning.one.example", "provisioning.two.example" }));
        Assert.That(configuration["RavenHosting:PublicDomain"], Is.EqualTo("ravenhosting.cloud"));
    }

    [Test]
    public void BothNamesWithTheSameValueAreFine()
    {
        var configuration = Configuration(new()
        {
            ["InvitationCodes:DatabasePath"] = "/data/invitation-codes.db",
            ["RavenHosting:DatabasePath"] = "/data/invitation-codes.db",
        });

        Assert.That(RenamedSettings.Apply(configuration), Is.Empty);
        Assert.That(configuration["RavenHosting:DatabasePath"], Is.EqualTo("/data/invitation-codes.db"));
    }

    [Test]
    public void BothNamesWithDifferentValuesAreRefused()
    {
        var configuration = Configuration(new()
        {
            ["InvitationCodes:DatabasePath"] = "/data/invitation-codes.db",
            ["RavenHosting:DatabasePath"] = "/data/raven-hosting.db",
        });

        var refused = Assert.Throws<InvalidOperationException>(() => RenamedSettings.Apply(configuration));
        Assert.That(refused!.Message, Does.Contain("remove InvitationCodes:DatabasePath"));
    }

    [Test]
    public void ListsThatDifferAreRefused()
    {
        var configuration = Configuration(new()
        {
            ["PlanService:Hosts:0"] = "provisioning.one.example",
            ["PlanService:Hosts:1"] = "provisioning.two.example",
            ["RavenHosting:IdentityHosts:0"] = "provisioning.one.example",
        });

        Assert.Throws<InvalidOperationException>(() => RenamedSettings.Apply(configuration));
    }

    [Test]
    public async Task TheServiceRunsOnTheOldNames()
    {
        using var server = new InvitationCodeServer(oldSettingNames: true);
        using var admin = server.ClientWithKey(InvitationCodeServer.CreateKey);
        server.IdentityHosts.Serve(InvitationCodeServer.Hosts[0], "frodo.dotyou.cloud");

        var code = await server.CreateCodeAsync();
        var grant = await admin.PostAsJsonAsync("/api/v1/plans/grants/",
            new { identity = "frodo.dotyou.cloud", planId = "veterans" });
        using var browser = server.Browser();
        var client = await browser.GetFromJsonAsync<Dictionary<string, object>>("/.well-known/youauth-client.json");

        Assert.That(code.PlanId, Is.EqualTo("free"));
        Assert.That(grant.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        Assert.That((await grant.Content.ReadFromJsonAsync<PlanGrant>())!.Status, Is.EqualTo(PlanGrantStatus.Applied),
            "the old host list and account service key were used");
        Assert.That(client!["name"].ToString(), Is.EqualTo("Test Hosting"));
    }

    private static ConfigurationManager Configuration(Dictionary<string, string?> values)
    {
        var configuration = new ConfigurationManager();
        configuration.AddInMemoryCollection(values);
        return configuration;
    }
}
