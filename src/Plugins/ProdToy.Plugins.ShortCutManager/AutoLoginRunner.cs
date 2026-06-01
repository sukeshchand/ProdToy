using Microsoft.Playwright;
using ProdToy.Sdk;

namespace ProdToy.Plugins.ShortCutManager;

/// <summary>Root for per-shortcut Playwright user data dirs. Set once at
/// plugin Initialize so the runner can locate it without a context.</summary>
static class AutoLoginPaths
{
    public static string ProfilesRoot { get; private set; } = "";
    public static void Initialize(string scopedDataDir)
        => ProfilesRoot = Path.Combine(scopedDataDir, "browser-profiles");
}

/// <summary>
/// Spins up a Playwright-controlled Edge instance with a persistent per-shortcut
/// profile (so cookies are cached between runs) and signs the user in:
///   1. Open the Home URL with the cached cookies.
///   2. If it loads without redirecting to the Login URL (and shows no login
///      form), the session is still valid — stop, leave the browser open.
///   3. Otherwise open the Login URL, fill the username + password, submit, and
///      wait to be returned to the home page. Cookies persist for next time.
///
/// The browser is launched against the system-installed Edge via
/// <c>Channel = "msedge"</c> so we don't have to ship Chromium. Sessions are
/// kept alive in a static list because Playwright disposes the browser when the
/// handle is collected; we want the window to stay open until the user closes it
/// (or ProdToy exits).
/// </summary>
static class AutoLoginRunner
{
    // Holds onto live sessions so they don't get GC'd while the user is
    // still in the browser. Cleaned up when the browser process disconnects.
    private static readonly List<(IPlaywright Pw, IBrowserContext Ctx)> _sessions = new();
    private static readonly object _lock = new();

    public static void RunInBackground(Shortcut s) => RunInBackground(s, null);

    /// <summary><paramref name="report"/> receives human-readable progress lines
    /// (skip reasons, waiting, signed-in, errors) so a caller can surface them in
    /// its own UI/console. It is also mirrored to the plugin log.</summary>
    public static void RunInBackground(Shortcut s, Action<string>? report)
    {
        if (!s.AutoLoginEnabled) return;

        void Report(string m)
        {
            try { report?.Invoke(m); } catch { }
            PluginLog.Info($"AutoLogin '{s.Name}': {m}");
        }

        // Home target: HomeUrl only. Status URL is reserved for the Group
        // Launcher's liveness probe and isn't a navigation target — landing
        // an auto-login session on a status endpoint can hit non-HTML routes
        // or land the user somewhere they didn't ask for.
        string home = s.HomeUrl?.Trim() ?? "";
        if (string.IsNullOrWhiteSpace(home))
        {
            Report("skipped — Home URL is not set.");
            return;
        }

        string password = CredentialProtector.Decrypt(s.LoginPasswordEncrypted);
        if (string.IsNullOrEmpty(s.LoginUsername) || string.IsNullOrEmpty(password))
        {
            Report("skipped — username or password not set.");
            return;
        }

        Report("starting — will open the home page with cached cookies.");
        // Fire-and-forget. Playwright is fully async; trying to await it on
        // the launcher's caller would block the UI for several seconds.
        _ = Task.Run(() => RunAsync(s.Id, s.Name, home, s.LoginUrl?.Trim() ?? "", s.LoginUsername, password, s.LoggedInSelector?.Trim() ?? "", Report));
    }

    private static async Task RunAsync(string shortcutId, string shortcutName, string homeUrl, string loginUrl, string username, string password, string loggedInSelector, Action<string> report)
    {
        IPlaywright? pw = null;
        IBrowserContext? ctx = null;
        try
        {
            pw = await Playwright.CreateAsync();

            // Per-shortcut persistent profile: cookies + localStorage stick
            // across launches (this is the "cache" — Playwright writes them to
            // this dir automatically) so once logged in, subsequent runs land
            // on the home page without re-prompting.
            string userDataDir = string.IsNullOrEmpty(AutoLoginPaths.ProfilesRoot)
                ? Path.Combine(Path.GetTempPath(), "ProdToyShortcutsBrowser", shortcutId)
                : Path.Combine(AutoLoginPaths.ProfilesRoot, shortcutId);
            Directory.CreateDirectory(userDataDir);

            ctx = await pw.Chromium.LaunchPersistentContextAsync(userDataDir, new BrowserTypeLaunchPersistentContextOptions
            {
                Channel = "msedge",
                Headless = false,
                ViewportSize = ViewportSize.NoViewport,
                // Hide the "browser is being controlled by automation" banner
                // and the navigator.webdriver flag so sites don't bail.
                Args = new[] { "--disable-blink-features=AutomationControlled" },
                IgnoreDefaultArgs = new[] { "--enable-automation" },
            });

            var page = ctx.Pages.Count > 0 ? ctx.Pages[0] : await ctx.NewPageAsync();

            // 1. Open the home page (with cached cookies). A freshly-launched
            //    dotnet/npm server may not be listening yet, so retry until it
            //    responds or we time out — otherwise the browser just hits a
            //    connection error and nothing appears to happen.
            bool opened = false;
            var deadline = DateTime.UtcNow.AddSeconds(90);
            int waitNotices = 0;
            while (!opened)
            {
                try
                {
                    await page.GotoAsync(homeUrl, new PageGotoOptions
                    {
                        WaitUntil = WaitUntilState.DOMContentLoaded,
                        Timeout = 15_000,
                    });
                    opened = true;
                }
                catch (Exception ex)
                {
                    if (DateTime.UtcNow >= deadline)
                    {
                        report($"gave up waiting for {homeUrl} — {ex.Message.Split('\n')[0]}");
                        TrackSession(pw, ctx);
                        return;
                    }
                    if (waitNotices++ == 0) report("waiting for the server to come up…");
                    await Task.Delay(2_000);
                }
            }
            await SettleAsync(page);

            // 2. Still signed in? Selector-based check first when configured,
            //    fall back to the URL + visible-password heuristic.
            if (await IsLoggedInAsync(page, loginUrl, loggedInSelector))
            {
                report("home opened with cached session — already signed in. Done.");
                TrackSession(pw, ctx);
                return;
            }

            // 3. Not signed in — go to the login page (if a specific one was given
            //    and we're not already there; otherwise fill on the current page).
            report("not signed in — opening the login page…");
            if (!string.IsNullOrEmpty(loginUrl) && !UrlIsLoginPage(page.Url, loginUrl))
            {
                try
                {
                    await page.GotoAsync(loginUrl, new PageGotoOptions
                    {
                        WaitUntil = WaitUntilState.DOMContentLoaded,
                        Timeout = 20_000,
                    });
                }
                catch (Exception ex) { report($"couldn't open login URL — {ex.Message.Split('\n')[0]}"); }
                await SettleAsync(page);
            }

            // 4. Fill credentials + submit.
            report("filling credentials…");
            bool filled = await FillLoginFormAsync(page, username, password);
            if (!filled)
            {
                report("no login form found — leaving the browser as-is.");
                TrackSession(pw, ctx);
                return;
            }

            // 5. Wait to be taken away from the login page (back to home / returnUrl).
            try
            {
                await page.WaitForURLAsync(u => !UrlIsLoginPage(u, loginUrl),
                    new PageWaitForURLOptions { Timeout = 15_000 });
            }
            catch { /* SPA logins may not change the URL — fine */ }

            // 6. Land on the user's Home URL — the app's default post-login
            //    redirect may point at a different page (account confirmation,
            //    status URL, store dashboard, etc.), but the user picked
            //    Home URL as the page they want to see when launching this
            //    shortcut. Skip the navigation if we're already there to
            //    avoid an unnecessary reload.
            if (!UrlIsUnder(page.Url, homeUrl))
            {
                try
                {
                    await page.GotoAsync(homeUrl, new PageGotoOptions
                    {
                        WaitUntil = WaitUntilState.DOMContentLoaded,
                        Timeout = 20_000,
                    });
                }
                catch { }
            }

            report("signed in — returned to the app. Cookies cached for next time.");
            TrackSession(pw, ctx);
        }
        catch (Exception ex)
        {
            report($"error — {ex.Message.Split('\n')[0]}");
            try { if (ctx != null) await ctx.CloseAsync(); } catch { }
            try { pw?.Dispose(); } catch { }
        }
    }

    /// <summary>Keep the session alive so the browser window stays open; drop the
    /// reference when the user closes it.</summary>
    private static void TrackSession(IPlaywright pw, IBrowserContext ctx)
    {
        lock (_lock) _sessions.Add((pw, ctx));
        ctx.Close += (_, _) =>
        {
            lock (_lock) _sessions.RemoveAll(t => t.Ctx == ctx);
            try { pw?.Dispose(); } catch { }
        };
    }

    /// <summary>Best-effort wait for the page to settle after a navigation.</summary>
    private static async Task SettleAsync(IPage page)
    {
        try { await page.WaitForLoadStateAsync(LoadState.NetworkIdle, new PageWaitForLoadStateOptions { Timeout = 8_000 }); }
        catch { /* timeout is fine — DOMContentLoaded already fired */ }
    }

    /// <summary>Decide whether the page is in a signed-in state.
    /// <para>
    /// When <paramref name="loggedInSelector"/> is non-empty, it's authoritative:
    /// if Playwright's locator resolves to at least one visible element the
    /// user is signed in. This is the only reliable check for storefronts
    /// where the home URL renders for both anonymous and authed users
    /// (e.g. Lindex / SFB Bokhandeln — "Mina sidor" is visible only when
    /// signed in).
    /// </para>
    /// <para>
    /// With no selector configured, fall back to the heuristic: we're signed
    /// in if we're not on the login page AND no visible password field shows.
    /// </para>
    /// </summary>
    private static async Task<bool> IsLoggedInAsync(IPage page, string loginUrl, string loggedInSelector)
    {
        if (!string.IsNullOrEmpty(loggedInSelector))
        {
            try
            {
                // Locator(...).First.WaitForAsync with a short timeout would
                // miss the case "selector exists but invisible". Use Count
                // on a visible-only locator chain: in Playwright .NET the
                // ":visible" pseudo isn't available for arbitrary selectors,
                // so use the Locator's own count + IsVisibleAsync probe.
                var loc = page.Locator(loggedInSelector);
                int count = await loc.CountAsync();
                for (int i = 0; i < count; i++)
                {
                    if (await loc.Nth(i).IsVisibleAsync()) return true;
                }
                return false;
            }
            catch { /* bad selector → fall through to the heuristic */ }
        }

        if (!string.IsNullOrEmpty(loginUrl) && UrlIsLoginPage(page.Url, loginUrl)) return false;
        return !await HasVisiblePasswordAsync(page);
    }

    private static async Task<bool> HasVisiblePasswordAsync(IPage page)
    {
        try { return await page.Locator("input[type='password']:visible").CountAsync() > 0; }
        catch { return false; }
    }

    /// <summary>True when <paramref name="current"/> is on the same host and under
    /// the login URL's path (ignoring query/fragment, so returnUrl params match).</summary>
    private static bool UrlIsLoginPage(string current, string loginUrl)
    {
        if (string.IsNullOrEmpty(loginUrl) || string.IsNullOrEmpty(current)) return false;
        try
        {
            var l = new Uri(loginUrl);
            var c = new Uri(current);
            return string.Equals(l.Host, c.Host, StringComparison.OrdinalIgnoreCase)
                && c.AbsolutePath.TrimEnd('/').StartsWith(l.AbsolutePath.TrimEnd('/'), StringComparison.OrdinalIgnoreCase);
        }
        catch { return current.StartsWith(loginUrl, StringComparison.OrdinalIgnoreCase); }
    }

    /// <summary>True when <paramref name="current"/> is on the same host AND
    /// the same (or a sub-) path of <paramref name="target"/>. Used post-login
    /// to skip the "navigate to home" step when the app already redirected
    /// the user under the home URL. Query string + fragment are ignored.</summary>
    private static bool UrlIsUnder(string current, string target)
    {
        if (string.IsNullOrEmpty(target) || string.IsNullOrEmpty(current)) return false;
        try
        {
            var t = new Uri(target);
            var c = new Uri(current);
            if (!string.Equals(t.Host, c.Host, StringComparison.OrdinalIgnoreCase)) return false;
            string tp = t.AbsolutePath.TrimEnd('/');
            string cp = c.AbsolutePath.TrimEnd('/');
            if (tp.Length == 0) return true;
            return string.Equals(cp, tp, StringComparison.OrdinalIgnoreCase)
                || cp.StartsWith(tp + "/", StringComparison.OrdinalIgnoreCase);
        }
        catch { return current.StartsWith(target, StringComparison.OrdinalIgnoreCase); }
    }

    /// <summary>Heuristic form filler. Tries the most common patterns in order;
    /// the first that lands a value for both fields wins. Returns true if a
    /// password field was found and filled (i.e. a login form was present).</summary>
    private static async Task<bool> FillLoginFormAsync(IPage page, string username, string password)
    {
        // Username candidates — order matters; the first locator with a
        // visible match is used.
        string[] usernameSelectors =
        {
            "input[type='email']:visible",
            "input[name='username']:visible",
            "input[name='user']:visible",
            "input[name='email']:visible",
            "input[autocomplete='username']:visible",
            "input[autocomplete='email']:visible",
            "input[id*='user' i]:visible",
            "input[id*='email' i]:visible",
            "input[type='text']:visible",
        };
        string[] passwordSelectors =
        {
            "input[type='password']:visible",
            "input[name='password']:visible",
            "input[autocomplete='current-password']:visible",
        };
        string[] submitSelectors =
        {
            "button[type='submit']:visible",
            "input[type='submit']:visible",
            "button:has-text('Sign in'):visible",
            "button:has-text('Log in'):visible",
            "button:has-text('Login'):visible",
        };

        await TryFillFirstAsync(page, usernameSelectors, username);
        bool pwFilled = await TryFillFirstAsync(page, passwordSelectors, password);
        if (!pwFilled) return false; // no password field — abort rather than mis-click

        foreach (var sel in submitSelectors)
        {
            try
            {
                var btn = page.Locator(sel).First;
                if (await btn.CountAsync() == 0) continue;
                await btn.ClickAsync(new LocatorClickOptions { Timeout = 5_000 });
                return true;
            }
            catch { }
        }

        // No submit button matched — many forms submit on Enter in the password box.
        try { await page.Locator("input[type='password']:visible").First.PressAsync("Enter"); }
        catch { }
        return true;
    }

    private static async Task<bool> TryFillFirstAsync(IPage page, string[] selectors, string value)
    {
        foreach (var sel in selectors)
        {
            try
            {
                var input = page.Locator(sel).First;
                if (await input.CountAsync() == 0) continue;
                await input.FillAsync(value, new LocatorFillOptions { Timeout = 5_000 });
                return true;
            }
            catch { }
        }
        return false;
    }
}
