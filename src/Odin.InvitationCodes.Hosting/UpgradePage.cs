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
/// <c>/upgrade?id=&lt;identity&gt;&amp;host=&lt;provisioning domain&gt;</c>; they sign in with YouAuth, see their
/// plan and the plans for sale, and buy one. Buying is a stand-in for payment: it makes a purchase grant, which
/// sets the plan on the identity's host.
/// </summary>
public static class UpgradePage
{
    public const string Root = "/upgrade";

    public static void Map(WebApplication app)
    {
        app.MapGet("/.well-known/youauth-client.json", (UpgradeOptions options) =>
            options.Enabled
                ? Results.Json(new { name = options.DisplayName, redirect_uris = new[] { options.CallbackUrl } })
                : Results.NotFound());

        var page = app.MapGroup(Root);

        // id and host are hints anyone can write into a link: id only says whom to sign in, host is used only if it
        // is a host this service knows
        page.MapGet("", async (HttpContext context, string? id, string? host, UpgradeOptions options,
            UpgradeSession session, YouAuthLogin login, PlanService plans, PlanHosts hosts, IAntiforgery antiforgery) =>
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
                return await SignedInPageAsync(context, signedIn with { Host = signedIn.Host ?? hint }, options, session,
                    plans, antiforgery);
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

        // A stand-in for payment: buying makes a purchase grant, which sets the plan on the identity's host
        page.MapPost("/buy", async (HttpContext context, [FromForm] string? planId, UpgradeOptions options,
            UpgradeSession session, PlanService plans) =>
        {
            if (!options.Enabled)
            {
                return Results.NotFound();
            }

            var signedIn = session.Read(context);
            if (signedIn == null)
            {
                return Page(StatusCodes.Status401Unauthorized, "Not signed in",
                    P("Your session has ended. Open this page again from your Homebase owner console."));
            }

            var plan = options.ForSale(planId);
            if (plan == null)
            {
                return Page(StatusCodes.Status400BadRequest, "Not for sale", P("That plan is not for sale."));
            }

            var grant = await plans.GrantAsync(signedIn.Identity, plan.Id, PlanGrantSource.Purchase,
                $"purchase-{Guid.NewGuid():N}", signedIn.Host);
            if (grant.Host != null && grant.Host != signedIn.Host)
            {
                session.Write(context, signedIn with { Host = grant.Host });
            }

            var body = grant.Status switch
            {
                PlanGrantStatus.Applied => P($"{signedIn.Identity} is now on {plan.Name}."),
                PlanGrantStatus.Pending => P($"Your purchase of {plan.Name} is recorded. We could not apply it to " +
                                             $"{signedIn.Identity} yet, and will keep trying; it usually takes a few minutes."),
                _ => P($"We could not apply {plan.Name} to {signedIn.Identity}: your host does not offer it. " +
                       $"Please contact us with reference {grant.Reference}."),
            };
            return Page(StatusCodes.Status200OK, grant.Status == PlanGrantStatus.Applied ? "Done" : "Thank you",
                body + $"""<p><a href="{Root}">Back to your plan</a></p>""");
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

    private static async Task<IResult> SignedInPageAsync(HttpContext context, UpgradeSession.Data signedIn,
        UpgradeOptions options, UpgradeSession session, PlanService plans, IAntiforgery antiforgery)
    {
        var located = await plans.LocateAsync(signedIn.Identity, signedIn.Host);
        if (located.Host == null)
        {
            return Page(StatusCodes.Status404NotFound, "Not found",
                P($"We could not find {signedIn.Identity} on our hosts just now. Please try again in a few minutes."));
        }
        if (located.Host != signedIn.Host)
        {
            session.Write(context, signedIn with { Host = located.Host });
        }

        var tokens = antiforgery.GetAndStoreTokens(context);
        var antiforgeryField =
            $"""<input type="hidden" name="{Encode(tokens.FormFieldName)}" value="{Encode(tokens.RequestToken!)}">""";

        var current = located.PlanId == null ? null : options.ForSale(located.PlanId);
        var html = new StringBuilder();
        html.Append(P($"Signed in as {signedIn.Identity}."));
        html.Append($"""<p>Your plan: <strong>{Encode(current?.Name ?? located.PlanId ?? "none")}</strong></p>""");

        if (options.PlansForSale.Count == 0)
        {
            html.Append(P("There are no plans for sale right now."));
        }
        else
        {
            html.Append("<h2>Plans</h2><ul class=\"plans\">");
            foreach (var plan in options.PlansForSale)
            {
                var isCurrent = string.Equals(plan.Id, located.PlanId, StringComparison.OrdinalIgnoreCase);
                html.Append("<li>");
                html.Append($"<h3>{Encode(plan.Name)}</h3>");
                if (plan.Description != "")
                {
                    html.Append(P(plan.Description));
                }
                if (plan.Price != "")
                {
                    html.Append($"""<p class="price">{Encode(plan.Price)}</p>""");
                }
                html.Append(isCurrent
                    ? "<p><em>Your current plan</em></p>"
                    : $"""
                       <form method="post" action="{Root}/buy">{antiforgeryField}
                       <input type="hidden" name="planId" value="{Encode(plan.Id)}">
                       <button type="submit">Buy {Encode(plan.Name)}</button></form>
                       """);
                html.Append("</li>");
            }
            html.Append("</ul>");
        }

        html.Append($"""<form method="post" action="{Root}/sign-out">{antiforgeryField}<button type="submit" class="secondary">Sign out</button></form>""");
        return Page(StatusCodes.Status200OK, "Get more space", html.ToString());
    }

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
          body { font-family: system-ui, sans-serif; max-width: 40rem; margin: 2rem auto; padding: 0 1rem; color: #1e293b; line-height: 1.5; }
          h1 { font-size: 1.5rem; } h2 { font-size: 1.2rem; margin-top: 2rem; } h3 { margin: 0 0 .25rem; font-size: 1.05rem; }
          ul.plans { list-style: none; padding: 0; }
          ul.plans li { border: 1px solid #cbd5e1; border-radius: .5rem; padding: 1rem; margin-bottom: 1rem; }
          .price { font-weight: 600; }
          button { font: inherit; padding: .5rem 1rem; border-radius: .375rem; border: 0; background: #4f46e5; color: #fff; cursor: pointer; }
          button:focus-visible, a:focus-visible { outline: 3px solid #a5b4fc; outline-offset: 2px; }
          button.secondary { background: #e2e8f0; color: #1e293b; margin-top: 1rem; }
          a { color: #4f46e5; }
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
