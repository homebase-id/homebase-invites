using System.Globalization;
using Microsoft.AspNetCore.Antiforgery;
using Raven.Hosting.Components;
using Raven.Hosting.Components.Admin;

namespace Raven.Hosting;

/// <summary>
/// What every admin page gets: who is signed in, the token its forms carry, and what the last change did
/// </summary>
/// <param name="Identity">The signed-in admin</param>
/// <param name="Name">The name on their public profile card; null when it has none</param>
/// <param name="Done">What the last change did, to say so at the top; null when nothing was just done</param>
/// <param name="Error">Why the last change was refused; null when it was not</param>
public record AdminContext(string Identity, string? Name, AntiforgeryTokenSet Antiforgery, string? Done, string? Error);

/// <summary>
/// The admin pages (/admin): everything raven.sh does with the create key, for admins signed in with their Homebase
/// identity (README, "Admin pages"). Who is an admin is in <see cref="AdminStore"/>, managed with raven.sh admins.
/// Someone not signed in is sent to sign in and back; someone signed in who is not an admin is told there is nothing
/// here. Every change is an antiforgery-checked form post, answered with a redirect back to the page, saying what it
/// did or why it was refused, and logged with who made it.
/// </summary>
public static class AdminPages
{
    public const string Root = "/admin";

    private const string AdminItem = "raven-admin";

    public static void Map(WebApplication app)
    {
        var admin = app.MapGroup(Root).AddEndpointFilter(GuardAsync);

        //
        // Overview
        //

        admin.MapGet("", async (HttpContext context, PlanStore plans, PlanGrantStore grants, InvitationCodeStore codes,
            PurchaseAccessStore purchases, ReportStore reports, AdminStore admins, TimeProvider time) =>
            Show<AdminHome>(context, new
            {
                Plans = await plans.ListAsync(),
                Grants = await grants.ListAsync(null, null),
                Stats = await codes.StatsAsync(null),
                Access = await purchases.GetAsync(),
                RecentReports = (await reports.ListAsync(DaysAgo(time, 7))).Count,
                Admins = await admins.ListAsync(),
            }));

        //
        // Plans
        //

        admin.MapGet("/plans", async (HttpContext context, PlanStore plans) =>
            Show<AdminPlans>(context, new { Plans = await plans.ListAsync() }));

        admin.MapGet("/plans/new", (HttpContext context) => Show<AdminPlanForm>(context, new { }));

        admin.MapPost("/plans/new", async (HttpContext context, IFormCollection form, PlanStore plans) =>
        {
            try
            {
                var prices = new List<PlanPriceRequest>();
                foreach (var months in NewPlanTerms)
                {
                    if (!string.IsNullOrWhiteSpace(form[$"price{months}"]))
                    {
                        prices.Add(new PlanPriceRequest(months, Cents(form[$"price{months}"])));
                    }
                }
                var plan = await plans.CreateAsync(new PlanRequest(
                    Text(form["id"]), Text(form["name"]), Bytes(form["quota"], form["unit"]), Checked(form, "publicWebPresence"),
                    Checked(form, "forSale"), Text(form["description"]) ?? "", prices, Number(form["sortOrder"], "Sort order") ?? 0,
                    Checked(form, "publicEmail"), Number(form["emailAliases"], "Email aliases") ?? 0,
                    Checked(form, "recommended")));
                Log(context, "created plan {PlanId}", plan.Id);
                return Back(PlanUrl(plan.Id), done: $"Plan {plan.Name} created.");
            }
            catch (Exception e) when (IsRefusal(e))
            {
                return Show<AdminPlanForm>(context, new { Form = form }, e.Message, StatusCodes.Status400BadRequest);
            }
        });

        admin.MapGet("/plans/{id}", async (HttpContext context, string id, PlanStore plans, InvitationCodeStore codes,
            PlanGrantStore grants) =>
            await plans.GetAsync(id) is { } plan
                ? Show<AdminPlanForm>(context, new
                {
                    Plan = plan,
                    CodesUsing = await codes.CountWithPlanAsync(plan.Id),
                    GrantsUsing = await grants.CountWithPlanAsync(plan.Id),
                })
                : NotFound(context, "There is no such plan."));

        admin.MapPost("/plans/{id}", async (HttpContext context, string id, IFormCollection form, PlanStore plans,
            InvitationCodeStore codes, PlanGrantStore grants) =>
        {
            try
            {
                var plan = await plans.UpdateAsync(id, new PlanChange(
                    Text(form["name"]) ?? "", Text(form["description"]) ?? "", Bytes(form["quota"], form["unit"]),
                    Checked(form, "publicWebPresence"), Checked(form, "forSale"),
                    Number(form["sortOrder"], "Sort order") ?? 0, Checked(form, "publicEmail"),
                    Number(form["emailAliases"], "Email aliases") ?? 0, Checked(form, "recommended")));
                if (plan == null)
                {
                    return NotFound(context, "There is no such plan.");
                }
                Log(context, "changed plan {PlanId}", plan.Id);
                return Back(PlanUrl(plan.Id), done: "Saved.");
            }
            catch (Exception e) when (IsRefusal(e))
            {
                return await plans.GetAsync(id) is { } plan
                    ? Show<AdminPlanForm>(context, new
                    {
                        Plan = plan,
                        Form = form,
                        CodesUsing = await codes.CountWithPlanAsync(plan.Id),
                        GrantsUsing = await grants.CountWithPlanAsync(plan.Id),
                    }, e.Message, StatusCodes.Status400BadRequest)
                    : NotFound(context, "There is no such plan.");
            }
        });

        admin.MapPost("/plans/{id}/prices", async (HttpContext context, string id, IFormCollection form, PlanStore plans) =>
        {
            try
            {
                var months = Number(form["months"], "Months") ?? throw new InvalidPlanRequestException("Give the months");
                var plan = await plans.SetPriceAsync(id, months, Cents(form["dollars"]));
                if (plan == null)
                {
                    return NotFound(context, "There is no such plan.");
                }
                Log(context, "set plan {PlanId}'s price for {Months} months", plan.Id, months);
                return Back(PlanUrl(plan.Id), done: $"{plan.PriceFor(months)!.Text} set.");
            }
            catch (Exception e) when (IsRefusal(e))
            {
                return Back(PlanUrl(id), error: e.Message);
            }
        });

        admin.MapPost("/plans/{id}/prices/{months:int}/remove", async (HttpContext context, string id, int months,
            IFormCollection form, PlanStore plans) =>
        {
            try
            {
                var (plan, found) = await plans.RemovePriceAsync(id, months);
                if (plan == null)
                {
                    return NotFound(context, "There is no such plan.");
                }
                if (!found)
                {
                    return Back(PlanUrl(plan.Id), error: $"{plan.Name} has no price for {BillingTerm.Name(months)}.");
                }
                Log(context, "removed plan {PlanId}'s price for {Months} months", plan.Id, months);
                return Back(PlanUrl(plan.Id), done: $"The price per {BillingTerm.Name(months)} is removed.");
            }
            catch (Exception e) when (IsRefusal(e))
            {
                return Back(PlanUrl(id), error: e.Message);
            }
        });

        admin.MapPost("/plans/{id}/delete", async (HttpContext context, string id, IFormCollection form, PlanStore plans,
            InvitationCodeStore codes, PlanGrantStore grants) =>
        {
            var (found, conflict) = await DeletePlanAsync(plans, codes, grants, id);
            if (!found)
            {
                return NotFound(context, "There is no such plan.");
            }
            if (conflict != null)
            {
                return Back(PlanUrl(id), error: conflict);
            }
            Log(context, "deleted plan {PlanId}", id);
            return Back($"{Root}/plans", done: $"Plan {id} deleted.");
        });

        //
        // Grants
        //

        admin.MapGet("/grants", async (HttpContext context, string? identity, string? status, PlanGrantStore grants,
            PlanStore plans, PlanHosts hosts) =>
        {
            identity = string.IsNullOrWhiteSpace(identity) ? null : identity.Trim();
            status = string.IsNullOrWhiteSpace(status) ? null : status;
            string? problem = null;
            List<PlanGrant> list;
            try
            {
                list = await grants.ListAsync(identity, status);
            }
            catch (InvalidInvitationCodeRequestException e)
            {
                list = [];
                problem = e.Message;
            }
            return Show<AdminGrants>(context, new
            {
                Grants = list,
                Plans = await plans.ListAsync(),
                Hosts = hosts.Hosts,
                Identity = identity,
                Status = status,
            }, problem);
        });

        admin.MapPost("/grants", async (HttpContext context, IFormCollection form, PlanService service,
            PlanGrantStore grants, PlanStore plans, PlanHosts hosts) =>
        {
            try
            {
                var grant = await service.GrantAsync(Text(form["identity"]), Text(form["planId"]), PlanGrantSource.Admin,
                    Text(form["reference"]), Text(form["host"]));
                Log(context, "granted plan {PlanId} to {Identity} (grant {GrantId})", grant.PlanId, grant.Identity, grant.Id);
                return Back($"{Root}/grants?identity={Uri.EscapeDataString(grant.Identity)}",
                    done: $"Grant {grant.Id}: {grant.PlanId} for {grant.Identity}, {grant.Status}.");
            }
            catch (Exception e) when (IsRefusal(e) || e is PlanGrantConflictException)
            {
                return Show<AdminGrants>(context, new
                {
                    Grants = await grants.ListAsync(null, null),
                    Plans = await plans.ListAsync(),
                    Hosts = hosts.Hosts,
                    Form = form,
                }, e.Message, StatusCodes.Status400BadRequest);
            }
        });

        admin.MapPost("/grants/{id:long}/retry", async (HttpContext context, long id, IFormCollection form,
            PlanService service, PlanGrantStore grants) =>
        {
            var back = Text(form["back"]) is { } b && b.StartsWith($"{Root}/") ? b : $"{Root}/grants";
            var grant = await service.RetryAsync(id);
            if (grant == null)
            {
                return await grants.GetAsync(id) is null
                    ? NotFound(context, "There is no such grant.")
                    : Back(back, error: $"Grant {id} is already applied.");
            }
            Log(context, "retried grant {GrantId}", id);
            return grant.Status == PlanGrantStatus.Applied
                ? Back(back, done: $"Grant {id} is applied on {grant.Host}.")
                : Back(back, error: $"Grant {id} is {grant.Status}: {grant.LastError}");
        });

        //
        // Codes
        //

        admin.MapGet("/codes", async (HttpContext context, string? owner, InvitationCodeStore codes, PlanStore plans,
            PlanHosts hosts) =>
        {
            owner = string.IsNullOrWhiteSpace(owner) ? null : owner.Trim();
            string? problem = null;
            List<InvitationCode> list;
            try
            {
                list = await codes.ListAsync(owner);
            }
            catch (InvalidInvitationCodeRequestException e)
            {
                list = [];
                problem = e.Message;
            }
            list.Reverse();
            return Show<AdminCodes>(context, new
            {
                Codes = list,
                Plans = await plans.ListAsync(),
                Owner = owner,
                SignupHost = SignupHosts(hosts)[0],
            }, problem);
        });

        admin.MapPost("/codes", async (HttpContext context, IFormCollection form, InvitationCodeStore codes, PlanStore plans,
            PlanHosts hosts) =>
        {
            try
            {
                var plan = await ExistingPlanAsync(plans, form["planId"]);
                var code = await codes.CreateAsync(Text(form["owner"]),
                    Number(form["uses"], "Uses") ?? throw new InvalidInvitationCodeRequestException("Give the number of uses"),
                    Text(form["source"]), true, plan.Id);
                Log(context, "created code {Code} for plan {PlanId}", code.Code, plan.Id);
                return Back(CodeUrl(code.Code), done: $"Code {code.Code} created.");
            }
            catch (Exception e) when (IsRefusal(e))
            {
                var list = await codes.ListAsync(null);
                list.Reverse();
                return Show<AdminCodes>(context, new
                {
                    Codes = list,
                    Plans = await plans.ListAsync(),
                    Form = form,
                    SignupHost = SignupHosts(hosts)[0],
                }, e.Message, StatusCodes.Status400BadRequest);
            }
        });

        admin.MapGet("/codes/stats", async (HttpContext context, int? days, InvitationCodeStore codes, TimeProvider time) =>
        {
            days = days is > 0 ? days : null;
            return Show<AdminStats>(context, new
            {
                Stats = await codes.StatsAsync(days == null ? null : DaysAgo(time, days.Value)),
                Days = days,
            });
        });

        admin.MapGet("/codes/{code}", async (HttpContext context, string code, InvitationCodeStore codes, PlanStore plans,
            PlanHosts hosts) =>
            await codes.GetAsync(code) is { } found
                ? Show<AdminCode>(context, new { Code = found, Plans = await plans.ListAsync(), SignupHosts = SignupHosts(hosts) })
                : NotFound(context, "There is no such code."));

        admin.MapPost("/codes/{code}", async (HttpContext context, string code, IFormCollection form,
            InvitationCodeStore codes, PlanStore plans, PlanHosts hosts) =>
        {
            try
            {
                var plan = await ExistingPlanAsync(plans, form["planId"]);
                var updated = await codes.UpdateAsync(code, new InvitationCodeChange(
                    MaxUses: Number(form["uses"], "Uses") ?? throw new InvalidInvitationCodeRequestException("Give the number of uses"),
                    Source: Text(form["source"]) ?? "",
                    Owner: Text(form["owner"]) ?? "",
                    PlanId: plan.Id));
                if (updated == null)
                {
                    return NotFound(context, "There is no such code.");
                }
                Log(context, "changed code {Code}", updated.Code);
                return Back(CodeUrl(updated.Code), done: "Saved.");
            }
            catch (Exception e) when (IsRefusal(e))
            {
                return await codes.GetAsync(code) is { } found
                    ? Show<AdminCode>(context, new
                    {
                        Code = found,
                        Plans = await plans.ListAsync(),
                        Form = form,
                        SignupHosts = SignupHosts(hosts),
                    }, e.Message, StatusCodes.Status400BadRequest)
                    : NotFound(context, "There is no such code.");
            }
        });

        admin.MapPost("/codes/{code}/{action:regex(^(disable|enable)$)}", async (HttpContext context, string code,
            string action, IFormCollection form, InvitationCodeStore codes) =>
        {
            var updated = await codes.UpdateAsync(code, new InvitationCodeChange(Disabled: action == "disable"));
            if (updated == null)
            {
                return NotFound(context, "There is no such code.");
            }
            Log(context, action + "d code {Code}", updated.Code);
            return Back(CodeUrl(updated.Code), done: updated.Disabled ? "Disabled." : "Enabled.");
        });

        // As a signup would, with the signup key: take a use, or give the latest back
        admin.MapPost("/codes/{code}/use", async (HttpContext context, string code, IFormCollection form,
            InvitationCodeStore codes) =>
        {
            if (await codes.GetAsync(code) is not { } found)
            {
                return NotFound(context, "There is no such code.");
            }
            var used = await codes.TryUseAsync(found.Code);
            if (used == null)
            {
                return Back(CodeUrl(found.Code), error: "No use taken: the code is disabled or used up.");
            }
            Log(context, "took a use of code {Code}", found.Code);
            return Back(CodeUrl(found.Code), done: $"A use taken; {used.UsesLeft} left.");
        });

        admin.MapPost("/codes/{code}/undo-use", async (HttpContext context, string code, IFormCollection form,
            InvitationCodeStore codes) =>
        {
            if (await codes.GetAsync(code) is not { } found)
            {
                return NotFound(context, "There is no such code.");
            }
            await codes.UndoUseAsync(found.Code);
            var after = await codes.GetAsync(found.Code);
            Log(context, "gave back a use of code {Code}", found.Code);
            return after!.UsesLeft > found.UsesLeft
                ? Back(CodeUrl(found.Code), done: $"A use given back; {after.UsesLeft} left.")
                : Back(CodeUrl(found.Code), error: "Nothing given back: the code has no uses taken.");
        });

        //
        // Who may buy
        //

        admin.MapGet("/purchases", async (HttpContext context, PurchaseAccessStore purchases) =>
            Show<AdminPurchases>(context, new { Access = await purchases.GetAsync() }));

        admin.MapPost("/purchases/open", async (HttpContext context, IFormCollection form, PurchaseAccessStore purchases) =>
        {
            var open = form["open"] == "true";
            await purchases.SetOpenAsync(open);
            Log(context, open ? "opened purchases" : "closed purchases");
            return Back($"{Root}/purchases",
                done: open ? "Purchases are open: anyone signed in can buy." : "Purchases are closed: only testers can buy.");
        });

        admin.MapPost("/purchases/testers", async (HttpContext context, IFormCollection form,
            PurchaseAccessStore purchases, TimeProvider time) =>
        {
            try
            {
                var identity = DomainNames.Normalize(form["identity"]);
                await purchases.AddTesterAsync(form["identity"], time.GetUtcNow().ToUnixTimeMilliseconds());
                Log(context, "added tester {Identity}", identity);
                return Back($"{Root}/purchases", done: $"{identity} can buy.");
            }
            catch (InvalidInvitationCodeRequestException e)
            {
                return Back($"{Root}/purchases", error: e.Message);
            }
        });

        admin.MapPost("/purchases/testers/{identity}/remove", async (HttpContext context, string identity,
            IFormCollection form, PurchaseAccessStore purchases) =>
        {
            if (await purchases.RemoveTesterAsync(identity) == null)
            {
                return Back($"{Root}/purchases", error: $"{identity} is not a tester.");
            }
            Log(context, "removed tester {Identity}", identity);
            return Back($"{Root}/purchases", done: $"{identity} is no longer a tester.");
        });

        //
        // Content reports
        //

        admin.MapGet("/reports", async (HttpContext context, int? days, ReportStore reports, TimeProvider time) =>
        {
            days = days is > 0 ? days : null;
            return Show<AdminReports>(context, new
            {
                Reports = await reports.ListAsync(days == null ? null : DaysAgo(time, days.Value)),
                Days = days,
            });
        });
    }

    //

    /// <summary>
    /// Deletes a plan no code or grant uses. Found is false when there is no such plan; Conflict says why one in use
    /// was not deleted.
    /// </summary>
    public static async Task<(bool Found, string? Conflict)> DeletePlanAsync(PlanStore plans, InvitationCodeStore codes,
        PlanGrantStore grants, string id)
    {
        if (await plans.GetAsync(id) is not { } plan)
        {
            return (false, null);
        }

        var codesUsing = await codes.CountWithPlanAsync(plan.Id);
        var grantsUsing = await grants.CountWithPlanAsync(plan.Id);
        if (codesUsing > 0 || grantsUsing > 0)
        {
            return (true, $"Plan {plan.Id} is used by {codesUsing} code(s) and {grantsUsing} grant(s); " +
                          "mark it not for sale instead");
        }

        await plans.DeleteAsync(plan.Id);
        return (true, null);
    }

    /// <summary>
    /// The provisioning servers a code can be signed up with, as the sign-up links offer them: the site's own sign-up
    /// server first (SitePages.DefaultSignupUrl), then every identity host the service knows. A code works on any of
    /// them: it is not tied to one.
    /// </summary>
    public static List<string> SignupHosts(PlanHosts hosts) =>
        new[] { new Uri(SitePages.DefaultSignupUrl).Host }.Concat(hosts.Hosts).Distinct().ToList();

    /// <summary>Where someone signs up with the code: the sign-up page, with the code filled in</summary>
    public static string SignupUrl(string host, string code) =>
        $"https://{host}/?invitation-code={Uri.EscapeDataString(code)}";

    public static string PlanUrl(string id) => $"{Root}/plans/{Uri.EscapeDataString(id)}";

    public static string CodeUrl(string code) => $"{Root}/codes/{Uri.EscapeDataString(code)}";

    /// <summary>The terms the new-plan form asks a price for: a month, a year, two years</summary>
    public static readonly int[] NewPlanTerms = [1, 12, 24];

    // Not signed in: to sign in, and back. Signed in, not an admin: nothing here. Otherwise the admin, for the page
    private static async ValueTask<object?> GuardAsync(EndpointFilterInvocationContext invocation,
        EndpointFilterDelegate next)
    {
        var context = invocation.HttpContext;
        var services = context.RequestServices;
        if (!services.GetRequiredService<UpgradeOptions>().Enabled)
        {
            return Results.NotFound();
        }

        var signedIn = services.GetRequiredService<UpgradeSession>().Read(context);
        if (signedIn == null)
        {
            return Results.Redirect(SitePages.SignInForAdmin);
        }
        if (!await services.GetRequiredService<AdminStore>().IsAdminAsync(signedIn.Identity))
        {
            return UpgradePage.Show<MessagePage>(new
            {
                Title = "Not found",
                Message = "There is nothing here.",
                LinkText = "Back to the plans",
                LinkHref = "/",
            }, StatusCodes.Status404NotFound);
        }

        context.Items[AdminItem] = signedIn;
        return await next(invocation);
    }

    private static IResult Show<TPage>(HttpContext context, object parameters, string? error = null,
        int status = StatusCodes.Status200OK) where TPage : Microsoft.AspNetCore.Components.IComponent
    {
        var signedIn = (UpgradeSession.Data)context.Items[AdminItem]!;
        var query = context.Request.Query;
        var admin = new AdminContext(signedIn.Identity, signedIn.Name,
            context.RequestServices.GetRequiredService<IAntiforgery>().GetAndStoreTokens(context),
            context.Request.Method == HttpMethods.Get ? Text(query["done"]) : null,
            error ?? (context.Request.Method == HttpMethods.Get ? Text(query["error"]) : null));

        var values = new Dictionary<string, object?> { ["Admin"] = admin };
        foreach (var property in parameters.GetType().GetProperties())
        {
            values[property.Name] = property.GetValue(parameters);
        }
        return UpgradePage.Show<TPage>(values, status);
    }

    private static IResult NotFound(HttpContext context, string message) =>
        Show<AdminMessage>(context, new { Message = message }, status: StatusCodes.Status404NotFound);

    // Back to a page, saying what was done or why not; the page shows it once
    private static IResult Back(string path, string? done = null, string? error = null)
    {
        var separator = path.Contains('?') ? '&' : '?';
        return done != null ? Results.Redirect($"{path}{separator}done={Uri.EscapeDataString(done)}")
            : error != null ? Results.Redirect($"{path}{separator}error={Uri.EscapeDataString(error)}")
            : Results.Redirect(path);
    }

    private static void Log(HttpContext context, string action, params object?[] args)
    {
        var signedIn = (UpgradeSession.Data)context.Items[AdminItem]!;
        var logger = context.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger("Raven.Hosting.Admin");
        logger.LogInformation("Admin {Admin} " + action, [signedIn.Identity, .. args]);
    }

    // What the stores refuse with a message for the person: bad input, or a change that conflicts
    private static bool IsRefusal(Exception e) =>
        e is InvalidInvitationCodeRequestException or PlanConflictException;

    private static async Task<Plan> ExistingPlanAsync(PlanStore plans, string? planId) =>
        await plans.GetAsync(planId) ??
        throw new InvalidInvitationCodeRequestException(string.IsNullOrWhiteSpace(planId)
            ? "Choose a plan"
            : $"There is no plan '{planId.Trim()}'");

    private static long DaysAgo(TimeProvider time, int days) =>
        time.GetUtcNow().AddDays(-days).ToUnixTimeMilliseconds();

    private static string? Text(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static bool Checked(IFormCollection form, string name) => form[name] == "true";

    private static int? Number(string? value, string what) =>
        string.IsNullOrWhiteSpace(value) ? null
        : int.TryParse(value.Trim(), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var n) ? n
        : throw new InvalidInvitationCodeRequestException($"{what} must be a whole number");

    // Dollars as typed (5, 4.99, $1,200) in cents
    private static long Cents(string? dollars)
    {
        var text = dollars?.Trim().TrimStart('$').Replace(",", "");
        if (string.IsNullOrEmpty(text) ||
            !decimal.TryParse(text, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var amount) ||
            decimal.Round(amount, 2) != amount)
        {
            throw new InvalidPlanRequestException($"'{dollars}' is not an amount in dollars, like 5 or 4.99");
        }
        return (long)(amount * 100);
    }

    // A storage size as typed, in the unit chosen, in bytes (1024-based, like the plans show it)
    private static long Bytes(string? amount, string? unit)
    {
        var multiplier = unit switch
        {
            "TB" => 1L << 40,
            "GB" => 1L << 30,
            "MB" => 1L << 20,
            "bytes" => 1L,
            _ => throw new InvalidPlanRequestException("Choose a storage unit"),
        };
        if (!decimal.TryParse(amount?.Trim(), NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture,
                out var value) || value <= 0)
        {
            throw new InvalidPlanRequestException("Storage must be a number more than 0, like 100 or 1.5");
        }
        return (long)decimal.Round(value * multiplier);
    }
}

/// <summary>
/// Reading back a form that was refused, so the person does not type it all again
/// </summary>
public static class AdminForm
{
    public static string Value(IFormCollection? form, string name, string? fallback) =>
        form != null && form.ContainsKey(name) ? form[name].ToString() : fallback ?? "";

    public static bool Checked(IFormCollection? form, string name, bool fallback) =>
        form != null ? form[name] == "true" : fallback;

    /// <summary>A size as the form shows it: the largest unit it is a whole number of, and that number</summary>
    public static (string Amount, string Unit) Size(long bytes)
    {
        foreach (var (size, unit) in new[] { (1L << 40, "TB"), (1L << 30, "GB"), (1L << 20, "MB") })
        {
            if (bytes % size == 0)
            {
                return ((bytes / size).ToString(CultureInfo.InvariantCulture), unit);
            }
        }
        return (bytes.ToString(CultureInfo.InvariantCulture), "bytes");
    }

    /// <summary>Unix ms as people read it, in UTC</summary>
    public static string When(long? ms) => ms is { } value
        ? DateTimeOffset.FromUnixTimeMilliseconds(value).ToString("yyyy-MM-dd HH:mm 'UTC'", CultureInfo.InvariantCulture)
        : "";

    /// <summary>
    /// Marketing channels offered for a code's source, so the same one is spelled the same way and counts together in
    /// the code stats; any other text is fine too
    /// </summary>
    public static readonly string[] SourceSuggestions =
        ["X", "Instagram", "Facebook", "LinkedIn", "TikTok", "YouTube", "Reddit", "Newsletter", "Event", "Referral", "Website"];

    /// <summary>Cents as the price form shows them: 5, 4.99</summary>
    public static string Dollars(long cents) =>
        (cents / 100m).ToString(cents % 100 == 0 ? "0" : "0.00", CultureInfo.InvariantCulture);
}
