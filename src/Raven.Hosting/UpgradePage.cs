using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Mvc;
using Raven.Hosting.Components;

namespace Raven.Hosting;

/// <summary>
/// Who is signed in to the upgrade page: an identity whose owner proved it with YouAuth, and the host it was last
/// seen on. In a data-protected, time-limited cookie scoped to /upgrade.
/// </summary>
public class UpgradeSession(IDataProtectionProvider dataProtection)
{
    public const string CookieName = "raven_upgrade";
    public static readonly TimeSpan Lifetime = TimeSpan.FromHours(1);

    private readonly ITimeLimitedDataProtector _protector =
        dataProtection.CreateProtector("upgrade-session").ToTimeLimitedDataProtector();

    public record Data(string Identity, string? Host);

    /// <summary>The signed-in identity; null when there is no cookie, or it expired or was tampered with</summary>
    public Data? Read(HttpContext context)
    {
        var cookie = context.Request.Cookies[CookieName];
        if (string.IsNullOrEmpty(cookie))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<Data>(_protector.Unprotect(cookie));
        }
        catch (Exception e) when (e is CryptographicException or JsonException or FormatException)
        {
            return null;
        }
    }

    public void Write(HttpContext context, Data data) =>
        context.Response.Cookies.Append(CookieName, _protector.Protect(JsonSerializer.Serialize(data), Lifetime), Options());

    public void Clear(HttpContext context) => context.Response.Cookies.Delete(CookieName, Options());

    private static CookieOptions Options() => new()
    {
        HttpOnly = true,
        Secure = true,
        SameSite = SameSiteMode.Lax,
        Path = UpgradePage.Root,
        MaxAge = Lifetime,
    };
}

/// <summary>
/// Where the upgrade page's stylesheet is, with a version from its content: its URL changes when it does, so browsers
/// can keep it for a year
/// </summary>
public static class SiteAssets
{
    private const string StylesheetPath = "/assets/site.css";

    public static string Stylesheet { get; private set; } = StylesheetPath;

    public static void Load(IWebHostEnvironment environment)
    {
        using var css = environment.WebRootFileProvider.GetFileInfo(StylesheetPath.TrimStart('/')).CreateReadStream();
        Stylesheet = $"{StylesheetPath}?v={Convert.ToHexStringLower(SHA256.HashData(css))[..12]}";
    }
}

/// <summary>
/// The page where an owner gets a bigger plan (README, "Upgrade page"). The owner console sends them to
/// <c>/upgrade?id=&lt;identity&gt;&amp;host=&lt;provisioning domain&gt;</c>; they sign in with YouAuth, then go
/// through four steps: choose a plan (from the plans marked for sale), confirm it, a test checkout, and done. The
/// checkout is a stand-in for payment: completing it makes a purchase grant, which sets the plan, and what it gives,
/// on the identity's host. The pages are Razor components (Components/), rendered on the server.
/// </summary>
public static class UpgradePage
{
    public const string Root = "/upgrade";

    /// <summary>
    /// Where "Back to your Homebase" goes once the plan is changed. The one place to change it.
    /// </summary>
    public static string ReturnUrl(string identity) => $"https://{identity}/owner/settings/plan";

    public static bool SamePlan(string a, string? b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    public static void Map(WebApplication app)
    {
        SiteAssets.Load(app.Environment);

        app.MapGet("/.well-known/youauth-client.json", (UpgradeOptions options) =>
            options.Enabled
                ? Results.Json(new { name = options.DisplayName, redirect_uris = new[] { options.CallbackUrl } })
                : Results.NotFound());

        var page = app.MapGroup(Root);

        // Step 1, choose. id and host are hints anyone can write into a link: id only says whom to sign in, host is
        // used only if it is a host this service knows
        page.MapGet("", async (HttpContext context, string? id, string? host, UpgradeOptions options,
            UpgradeSession session, YouAuthLogin login, PlanService plans, PlanStore planStore, PlanHosts hosts,
            IAntiforgery antiforgery) =>
        {
            if (!options.Enabled)
            {
                return Results.NotFound();
            }

            var requested = id == null ? null : DomainNames.Normalize(id);
            if (id != null && requested == null)
            {
                return Message(StatusCodes.Status400BadRequest, "Not an identity",
                    $"'{id}' is not an identity. Open this page from your Homebase owner console.");
            }

            var hint = DomainNames.Normalize(host);
            if (!hosts.IsKnown(hint))
            {
                hint = null;
            }

            var signedIn = session.Read(context);
            if (signedIn != null && (requested == null || requested == signedIn.Identity))
            {
                var located = await LocateAsync(context, signedIn with { Host = signedIn.Host ?? hint }, session, plans);
                if (located.Error != null)
                {
                    return located.Error;
                }
                return Show<ChoosePage>(new
                {
                    Identity = located.SignedIn!.Identity,
                    CurrentPlanId = located.PlanId,
                    CurrentPlanName = await PlanNameAsync(planStore, located.PlanId),
                    Plans = (await planStore.ListAsync()).Where(p => p.ForSale && p.Prices.Count > 0).ToList(),
                    Antiforgery = antiforgery.GetAndStoreTokens(context),
                });
            }

            if (requested == null)
            {
                return Message(StatusCodes.Status400BadRequest, "Get more space",
                    "Open this page from your Homebase owner console: Settings, Plan & storage, Get more space.");
            }

            return Results.Redirect(await login.BeginAsync(requested, hint));
        });

        page.MapGet("/youauth-callback", async (HttpContext context, [FromQuery] string? state,
            [FromQuery(Name = "public_key")] string? publicKey, [FromQuery] string? salt, [FromQuery] string? error,
            [FromQuery(Name = "error_description")] string? errorDescription, UpgradeOptions options,
            UpgradeSession session, YouAuthLogin login) =>
        {
            if (!options.Enabled)
            {
                return Results.NotFound();
            }

            var result = await login.CompleteAsync(state, publicKey, salt, error, errorDescription);
            if (result.Identity == null)
            {
                return Message(StatusCodes.Status400BadRequest, "Not signed in", result.Error!);
            }

            session.Write(context, new UpgradeSession.Data(result.Identity, result.HostHint));
            return Results.Redirect(Root);
        });

        // Step 2, confirm
        page.MapGet("/confirm", async (HttpContext context, string? plan, UpgradeOptions options,
            UpgradeSession session, PlanService plans, PlanStore planStore) =>
        {
            if (!options.Enabled)
            {
                return Results.NotFound();
            }

            var signedIn = session.Read(context);
            if (signedIn == null)
            {
                return SessionEnded();
            }

            var chosen = await ForSaleAsync(planStore, plan);
            if (chosen == null)
            {
                return NotForSale();
            }
            if (chosen.Prices.Count == 0)
            {
                return NoPriceYet(chosen);
            }

            var located = await LocateAsync(context, signedIn, session, plans);
            if (located.Error != null)
            {
                return located.Error;
            }

            return Show<ConfirmPage>(new
            {
                Plan = chosen,
                CurrentPlanName = await PlanNameAsync(planStore, located.PlanId),
            });
        });

        // Step 3, a test checkout of the term chosen
        page.MapGet("/checkout", async (HttpContext context, string? plan, int? months, UpgradeOptions options,
            UpgradeSession session, PlanStore planStore, IAntiforgery antiforgery) =>
        {
            if (!options.Enabled)
            {
                return Results.NotFound();
            }

            var signedIn = session.Read(context);
            if (signedIn == null)
            {
                return SessionEnded();
            }

            var chosen = await ForSaleAsync(planStore, plan);
            if (chosen == null)
            {
                return NotForSale();
            }
            var price = TermOf(chosen, months);
            if (price == null)
            {
                return ChooseATerm(chosen);
            }

            return Show<CheckoutPage>(new
            {
                Plan = chosen,
                Price = price,
                Identity = signedIn.Identity,
                Antiforgery = antiforgery.GetAndStoreTokens(context),
            });
        });

        // Completing the test checkout makes a purchase grant of the plan and term, which sets the plan on the
        // identity's host. Only planId and months are read; anything else in the form is ignored
        page.MapPost("/checkout", async (HttpContext context, [FromForm] string? planId, [FromForm] int? months,
            UpgradeOptions options, UpgradeSession session, PlanService plans, PlanStore planStore) =>
        {
            if (!options.Enabled)
            {
                return Results.NotFound();
            }

            var signedIn = session.Read(context);
            if (signedIn == null)
            {
                return SessionEnded();
            }

            var plan = await ForSaleAsync(planStore, planId);
            if (plan == null)
            {
                return NotForSale();
            }
            var price = TermOf(plan, months);
            if (price == null)
            {
                return ChooseATerm(plan);
            }

            var grant = await plans.GrantAsync(signedIn.Identity, plan.Id, PlanGrantSource.Purchase,
                $"purchase-{Guid.NewGuid():N}", signedIn.Host, price.Months);
            if (grant.Host != null && grant.Host != signedIn.Host)
            {
                session.Write(context, signedIn with { Host = grant.Host });
            }

            return Results.Redirect($"{Root}/done?grant={grant.Id}");
        });

        // Step 4, done: only the signed-in identity's own purchases. The plan is named even if it is no longer for sale
        page.MapGet("/done", async (HttpContext context, long? grant, UpgradeOptions options, UpgradeSession session,
            PlanGrantStore grants, PlanStore planStore) =>
        {
            if (!options.Enabled)
            {
                return Results.NotFound();
            }

            var signedIn = session.Read(context);
            if (signedIn == null)
            {
                return SessionEnded();
            }

            var purchase = grant == null ? null : await grants.GetAsync(grant.Value);
            if (purchase == null || purchase.Identity != signedIn.Identity)
            {
                return Message(StatusCodes.Status404NotFound, "Not found", "We could not find that purchase.");
            }

            var plan = await planStore.GetAsync(purchase.PlanId);
            return Show<DonePage>(new
            {
                Purchase = purchase,
                Plan = plan,
                PlanName = plan?.Name ?? purchase.PlanId,
                Identity = signedIn.Identity,
            });
        });

        // The form is read only so the antiforgery token is checked
        page.MapPost("/sign-out", (HttpContext context, IFormCollection form, UpgradeOptions options,
            UpgradeSession session) =>
        {
            if (!options.Enabled)
            {
                return Results.NotFound();
            }

            session.Clear(context);
            return Message(StatusCodes.Status200OK, "Signed out", "You are signed out.");
        });
    }

    //

    private record Located(UpgradeSession.Data? SignedIn, string? PlanId, IResult? Error);

    // Where the identity is and its plan, asked of its host; remembers the host in the session when it moved
    private static async Task<Located> LocateAsync(HttpContext context, UpgradeSession.Data signedIn,
        UpgradeSession session, PlanService plans)
    {
        var located = await plans.LocateAsync(signedIn.Identity, signedIn.Host);
        if (located.Host == null)
        {
            return new Located(null, null, Message(StatusCodes.Status404NotFound, "Not found",
                $"We could not find {signedIn.Identity} on our hosts just now. Please try again in a few minutes."));
        }

        if (located.Host != signedIn.Host)
        {
            signedIn = signedIn with { Host = located.Host };
            session.Write(context, signedIn);
        }

        return new Located(signedIn, located.PlanId, null);
    }

    // The plan, if it is one for sale; null otherwise
    private static async Task<Plan?> ForSaleAsync(PlanStore planStore, string? planId) =>
        await planStore.GetAsync(planId) is { ForSale: true } plan ? plan : null;

    // The plan's name; its Id when it is not a plan here
    private static async Task<string> PlanNameAsync(PlanStore planStore, string? planId) =>
        (await planStore.GetAsync(planId))?.Name ?? planId ?? "none";

    // The term chosen; the plan's only one when none was and it has one
    private static PlanPrice? TermOf(Plan plan, int? months) =>
        months == null ? (plan.Prices.Count == 1 ? plan.Prices[0] : null) : plan.PriceFor(months);

    private static RazorComponentResult Show<TPage>(object parameters, int status = StatusCodes.Status200OK)
        where TPage : IComponent => new RazorComponentResult<TPage>(parameters) { StatusCode = status };

    private static RazorComponentResult Message(int status, string title, string message, string? linkText = null,
        string? linkHref = null) =>
        Show<MessagePage>(new { Title = title, Message = message, LinkText = linkText, LinkHref = linkHref }, status);

    private static IResult ChooseATerm(Plan plan) =>
        plan.Prices.Count == 0
            ? NoPriceYet(plan)
            : Message(StatusCodes.Status400BadRequest, "Choose how to pay", $"{plan.Name} is not sold for that term.",
                "Choose how to pay", $"{Root}/confirm?plan={Uri.EscapeDataString(plan.Id)}");

    private static IResult NoPriceYet(Plan plan) =>
        Message(StatusCodes.Status400BadRequest, "Not for sale yet", $"{plan.Name} has no price yet.",
            "Choose another plan", Root);

    private static IResult SessionEnded() => Message(StatusCodes.Status401Unauthorized, "Not signed in",
        "Your session has ended. Open this page again from your Homebase owner console.");

    private static IResult NotForSale() => Message(StatusCodes.Status400BadRequest, "Not for sale",
        "That plan is not for sale.", "Choose another plan", Root);
}
