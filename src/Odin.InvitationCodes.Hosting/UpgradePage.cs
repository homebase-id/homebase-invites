using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Mvc;

namespace Odin.InvitationCodes.Hosting;

/// <summary>
/// Who is signed in to the upgrade page: an identity whose owner proved it with YouAuth, and the host it was last
/// seen on. In a data-protected, time-limited cookie scoped to /upgrade.
/// </summary>
public class UpgradeSession(IDataProtectionProvider dataProtection)
{
    public const string CookieName = "homebase_upgrade";
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
/// The page where an owner gets a bigger plan (README, "Upgrade page"). The owner console sends them to
/// <c>/upgrade?id=&lt;identity&gt;&amp;host=&lt;provisioning domain&gt;</c>; they sign in with YouAuth, then go
/// through four steps: choose a plan (from the plans marked for sale), confirm it, a test checkout, and done. The
/// checkout is a stand-in for payment: completing it makes a purchase grant, which sets the plan, and what it gives,
/// on the identity's host.
/// </summary>
public static class UpgradePage
{
    public const string Root = "/upgrade";

    private static readonly string[] Steps = ["Choose", "Confirm", "Checkout", "Done"];

    /// <summary>
    /// Where "Back to your Homebase" goes once the plan is changed. The one place to change it.
    /// </summary>
    public static string ReturnUrl(string identity) => $"https://{identity}/owner/settings/plan";

    public static void Map(WebApplication app)
    {
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
                return Page(StatusCodes.Status400BadRequest, "Not an identity",
                    P($"'{id}' is not an identity. Open this page from your Homebase owner console."));
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
                return located.Error ?? await ChoosePageAsync(context, located.SignedIn!, located.PlanId, planStore,
                    antiforgery);
            }

            if (requested == null)
            {
                return Page(StatusCodes.Status400BadRequest, "Get more space",
                    P("Open this page from your Homebase owner console: Settings, Plan &amp; storage, Get more space.",
                        encode: false));
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
                return Page(StatusCodes.Status400BadRequest, "Not signed in", P(result.Error!));
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

            var located = await LocateAsync(context, signedIn, session, plans);
            if (located.Error != null)
            {
                return located.Error;
            }

            var current = await PlanNameAsync(planStore, located.PlanId);
            var html = new StringBuilder();
            html.Append(StepsBar(1));
            html.Append($"""
                         <div class="card">
                         <p class="muted">Change your plan from <strong>{Encode(current)}</strong> to</p>
                         <h2>{Encode(chosen.Name)}</h2>
                         {Details(chosen)}
                         </div>
                         <div class="actions">
                         <a class="button" href="{Root}/checkout?plan={Uri.EscapeDataString(chosen.Id)}">Continue to checkout</a>
                         <a class="button secondary" href="{Root}">Back</a>
                         </div>
                         """);
            return Page(StatusCodes.Status200OK, "Confirm your new plan", html.ToString());
        });

        // Step 3, a test checkout: the card fields have no names, so the browser never sends them anywhere
        page.MapGet("/checkout", async (HttpContext context, string? plan, UpgradeOptions options,
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

            var html = new StringBuilder();
            html.Append(StepsBar(2));
            html.Append("""<p class="banner" role="note"><strong>Test checkout</strong> — no payment is taken.</p>""");
            html.Append($"""
                         <div class="card">
                         <p class="muted">Order summary</p>
                         <h2>{Encode(chosen.Name)}</h2>
                         {Details(chosen)}
                         <p class="muted">For {Encode(signedIn.Identity)}</p>
                         </div>
                         <form method="post" action="{Root}/checkout" class="card checkout">
                         {AntiforgeryField(context, antiforgery)}
                         <input type="hidden" name="planId" value="{Encode(chosen.Id)}">
                         <label>Name on card <input autocomplete="off" placeholder="Frodo Baggins"></label>
                         <label>Card number <input autocomplete="off" inputmode="numeric" placeholder="4242 4242 4242 4242"></label>
                         <div class="row">
                         <label>Expiry <input autocomplete="off" placeholder="MM / YY"></label>
                         <label>CVC <input autocomplete="off" inputmode="numeric" placeholder="123"></label>
                         </div>
                         <div class="actions">
                         <button type="submit">Complete purchase</button>
                         <a class="button secondary" href="{Root}/confirm?plan={Uri.EscapeDataString(chosen.Id)}">Back</a>
                         </div>
                         </form>
                         """);
            return Page(StatusCodes.Status200OK, "Checkout", html.ToString());
        });

        // Completing the test checkout makes a purchase grant, which sets the plan on the identity's host. Only
        // planId is read; anything else in the form is ignored
        page.MapPost("/checkout", async (HttpContext context, [FromForm] string? planId, UpgradeOptions options,
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

            var plan = await ForSaleAsync(planStore, planId);
            if (plan == null)
            {
                return NotForSale();
            }

            var grant = await plans.GrantAsync(signedIn.Identity, plan.Id, PlanGrantSource.Purchase,
                $"purchase-{Guid.NewGuid():N}", signedIn.Host);
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
                return Page(StatusCodes.Status404NotFound, "Not found", P("We could not find that purchase."));
            }

            var plan = await planStore.GetAsync(purchase.PlanId);
            var name = plan?.Name ?? purchase.PlanId;
            var html = new StringBuilder();
            html.Append(StepsBar(3));
            html.Append("<div class=\"card\">");
            html.Append(purchase.Status switch
            {
                PlanGrantStatus.Applied => $"<h2>You're on {Encode(name)}</h2>" + (plan == null ? "" : Details(plan)),
                PlanGrantStatus.Pending => $"<h2>Thank you</h2>" +
                                           P($"Your purchase of {name} is recorded; your plan will change shortly."),
                _ => "<h2>Something went wrong</h2>" +
                     P($"We could not change {signedIn.Identity} to {name}. Please contact us with reference " +
                       $"{purchase.Reference}."),
            });
            html.Append("</div>");
            html.Append($"""<div class="actions"><a class="button" href="{Encode(ReturnUrl(signedIn.Identity))}">Back to your Homebase</a></div>""");
            return Page(StatusCodes.Status200OK, purchase.Status == PlanGrantStatus.Applied ? "Done" : "Thank you",
                html.ToString());
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
            return Page(StatusCodes.Status200OK, "Signed out", P("You are signed out."));
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
            return new Located(null, null, Page(StatusCodes.Status404NotFound, "Not found",
                P($"We could not find {signedIn.Identity} on our hosts just now. Please try again in a few minutes.")));
        }

        if (located.Host != signedIn.Host)
        {
            signedIn = signedIn with { Host = located.Host };
            session.Write(context, signedIn);
        }

        return new Located(signedIn, located.PlanId, null);
    }

    private static async Task<IResult> ChoosePageAsync(HttpContext context, UpgradeSession.Data signedIn,
        string? planId, PlanStore planStore, IAntiforgery antiforgery)
    {
        var html = new StringBuilder();
        html.Append(StepsBar(0));
        html.Append($"""<p class="muted">Signed in as <strong>{Encode(signedIn.Identity)}</strong></p>""");

        var forSale = (await planStore.ListAsync()).Where(p => p.ForSale).ToList();
        if (!forSale.Any(p => SamePlan(p.Id, planId)))
        {
            html.Append($"""<p>Your plan: <strong>{Encode(await PlanNameAsync(planStore, planId))}</strong></p>""");
        }

        if (forSale.Count == 0)
        {
            html.Append(P("There are no plans for sale right now."));
        }
        else
        {
            html.Append("<ul class=\"plans\">");
            foreach (var plan in forSale)
            {
                var isCurrent = SamePlan(plan.Id, planId);
                html.Append(isCurrent ? "<li class=\"card current\">" : "<li class=\"card\">");
                if (isCurrent)
                {
                    html.Append("<p class=\"badge\">Your plan</p>");
                }
                html.Append($"<h2>{Encode(plan.Name)}</h2>");
                html.Append(Details(plan));
                if (!isCurrent)
                {
                    html.Append($"""<a class="button" href="{Root}/confirm?plan={Uri.EscapeDataString(plan.Id)}">Choose {Encode(plan.Name)}</a>""");
                }
                html.Append("</li>");
            }
            html.Append("</ul>");
        }

        html.Append($"""<form method="post" action="{Root}/sign-out">{AntiforgeryField(context, antiforgery)}<button type="submit" class="secondary">Sign out</button></form>""");
        return Page(StatusCodes.Status200OK, "Choose a plan", html.ToString());
    }

    private static bool SamePlan(string a, string? b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    // The plan, if it is one for sale; null otherwise
    private static async Task<Plan?> ForSaleAsync(PlanStore planStore, string? planId) =>
        await planStore.GetAsync(planId) is { ForSale: true } plan ? plan : null;

    // The plan's name; its Id when it is not a plan here
    private static async Task<string> PlanNameAsync(PlanStore planStore, string? planId) =>
        (await planStore.GetAsync(planId))?.Name ?? planId ?? "none";

    // The description, then what the plan gives, then its price when it has one
    private static string Details(Plan plan)
    {
        var html = new StringBuilder();
        if (plan.Description != "")
        {
            html.Append(P(plan.Description));
        }
        html.Append($"""<ul class="gives"><li>{Encode(plan.Storage)} of storage</li>""");
        html.Append(plan.PublicWebPresence ? "<li>A public home page</li>" : "<li>No public home page</li>");
        html.Append("</ul>");
        if (plan.Price != "")
        {
            html.Append($"""<p class="price">{Encode(plan.Price)}</p>""");
        }
        return html.ToString();
    }

    private static string AntiforgeryField(HttpContext context, IAntiforgery antiforgery)
    {
        var tokens = antiforgery.GetAndStoreTokens(context);
        return $"""<input type="hidden" name="{Encode(tokens.FormFieldName)}" value="{Encode(tokens.RequestToken!)}">""";
    }

    private static string StepsBar(int current)
    {
        var html = new StringBuilder("<ol class=\"steps\" aria-label=\"Steps\">");
        for (var i = 0; i < Steps.Length; i++)
        {
            html.Append(i == current
                ? $"<li aria-current=\"step\">{Steps[i]}</li>"
                : i < current ? $"<li class=\"done\">{Steps[i]}</li>" : $"<li>{Steps[i]}</li>");
        }
        return html.Append("</ol>").ToString();
    }

    private static IResult SessionEnded() => Page(StatusCodes.Status401Unauthorized, "Not signed in",
        P("Your session has ended. Open this page again from your Homebase owner console."));

    private static IResult NotForSale() =>
        Page(StatusCodes.Status400BadRequest, "Not for sale", P("That plan is not for sale.") +
                                                              $"""<p><a href="{Root}">Choose another plan</a></p>""");

    private static string Encode(string text) => HtmlEncoder.Default.Encode(text);

    private static string P(string text, bool encode = true) => $"<p>{(encode ? Encode(text) : text)}</p>";

    private static IResult Page(int status, string title, string body) => Results.Content(
        $$"""
          <!doctype html>
          <html lang="en">
          <head>
          <meta charset="utf-8">
          <meta name="viewport" content="width=device-width, initial-scale=1">
          <title>{{Encode(title)}}</title>
          <style>
          :root { --ink: #0f172a; --muted: #64748b; --line: #e2e8f0; --brand: #4f46e5; --brand-ink: #fff; --soft: #eef2ff; }
          * { box-sizing: border-box; }
          body { font-family: system-ui, -apple-system, "Segoe UI", sans-serif; margin: 0; background: #f8fafc; color: var(--ink); line-height: 1.5; }
          main { max-width: 56rem; margin: 0 auto; padding: 2rem 1rem 4rem; }
          h1 { font-size: 1.75rem; margin: 0 0 1rem; } h2 { font-size: 1.2rem; margin: 0 0 .25rem; }
          p { margin: .25rem 0 .75rem; }
          .muted { color: var(--muted); }
          .steps { display: flex; flex-wrap: wrap; gap: .5rem; list-style: none; padding: 0; margin: 0 0 1.5rem; font-size: .9rem; color: var(--muted); }
          .steps li::after { content: "·"; margin-left: .5rem; } .steps li:last-child::after { content: ""; }
          .steps li[aria-current] { color: var(--brand); font-weight: 600; } .steps li.done { color: var(--ink); }
          .card { background: #fff; border: 1px solid var(--line); border-radius: .75rem; padding: 1.25rem; box-shadow: 0 1px 2px rgba(15,23,42,.05); margin-bottom: 1rem; }
          ul.plans { list-style: none; padding: 0; display: grid; gap: 1rem; grid-template-columns: repeat(auto-fit, minmax(15rem, 1fr)); }
          ul.plans .card { display: flex; flex-direction: column; margin: 0; }
          ul.plans .card .button { margin-top: auto; align-self: flex-start; }
          .card.current { border: 2px solid var(--brand); background: var(--soft); }
          ul.gives { margin: .25rem 0 .75rem; padding-left: 1.25rem; }
          .price { font-weight: 600; }
          .badge { display: inline-block; font-size: .8rem; font-weight: 600; color: var(--brand); text-transform: uppercase; letter-spacing: .04em; margin: 0 0 .5rem; }
          .banner { background: #fef3c7; border: 1px solid #fcd34d; color: #78350f; border-radius: .5rem; padding: .75rem 1rem; }
          .checkout label { display: block; font-size: .9rem; color: var(--muted); margin-bottom: .75rem; }
          .checkout input { display: block; width: 100%; font: inherit; color: var(--ink); padding: .6rem .75rem; margin-top: .25rem; border: 1px solid #cbd5e1; border-radius: .5rem; }
          .checkout .row { display: grid; grid-template-columns: 1fr 1fr; gap: .75rem; }
          .actions { display: flex; flex-wrap: wrap; gap: .75rem; align-items: center; margin-top: 1rem; }
          button, .button { display: inline-block; font: inherit; font-weight: 600; text-decoration: none; padding: .6rem 1.1rem; border-radius: .5rem; border: 0; background: var(--brand); color: var(--brand-ink); cursor: pointer; }
          button.secondary, .button.secondary { background: var(--line); color: var(--ink); }
          form > button.secondary { margin-top: 1.5rem; }
          button:focus-visible, a:focus-visible, input:focus-visible { outline: 3px solid #a5b4fc; outline-offset: 2px; }
          a { color: var(--brand); }
          </style>
          </head>
          <body>
          <main>
          <h1>{{Encode(title)}}</h1>
          {{body}}
          </main>
          </body>
          </html>
          """, "text/html; charset=utf-8", Encoding.UTF8, status);
}
