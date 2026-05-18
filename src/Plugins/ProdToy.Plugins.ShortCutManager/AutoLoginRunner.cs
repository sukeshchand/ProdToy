using Microsoft.Playwright;
using ProdToy.Sdk;

namespace ProdToy.Plugins.ShortCutManager;

/// <summary>
/// Spins up a Playwright-controlled Edge instance, navigates to a shortcut's
/// Status URL, fills the first visible username + password input pair, clicks
/// the first plausible submit button, and leaves the browser open so the
/// user can pick up where the automation left off.
///
/// The browser is launched against the system-installed Edge via
/// <c>Channel = "msedge"</c> so we don't have to ship Chromium. Sessions are
/// kept alive in a static list because Playwright disposes the browser when
/// the IBrowser handle is collected; we want the window to stay open until
/// the user closes it (or ProdToy exits).
/// </summary>
static class AutoLoginRunner
{
    // Holds onto live sessions so they don't get GC'd while the user is
    // still in the browser. Cleaned up when the browser process disconnects.
    private static readonly List<(IPlaywright Pw, IBrowser Browser)> _sessions = new();
    private static readonly object _lock = new();

    public static void RunInBackground(Shortcut s)
    {
        if (!s.AutoLoginEnabled) return;
        if (string.IsNullOrWhiteSpace(s.StatusUrl))
        {
            PluginLog.Warn($"AutoLogin: shortcut '{s.Name}' has auto-login on but no StatusUrl — skipping.");
            return;
        }

        string password = CredentialProtector.Decrypt(s.LoginPasswordEncrypted);
        if (string.IsNullOrEmpty(s.LoginUsername) || string.IsNullOrEmpty(password))
        {
            PluginLog.Warn($"AutoLogin: shortcut '{s.Name}' missing username or password — skipping.");
            return;
        }

        // Fire-and-forget. Playwright is fully async; trying to await it on
        // the launcher's caller would block the UI for several seconds.
        _ = Task.Run(() => RunAsync(s.Name, s.StatusUrl, s.LoginUsername, password));
    }

    private static async Task RunAsync(string shortcutName, string url, string username, string password)
    {
        IPlaywright? pw = null;
        IBrowser? browser = null;
        try
        {
            pw = await Playwright.CreateAsync();
            browser = await pw.Chromium.LaunchAsync(new BrowserTypeLaunchOptions
            {
                Channel = "msedge",
                Headless = false,
            });
            var context = await browser.NewContextAsync(new BrowserNewContextOptions
            {
                ViewportSize = ViewportSize.NoViewport,
            });
            var page = await context.NewPageAsync();

            await page.GotoAsync(url, new PageGotoOptions
            {
                WaitUntil = WaitUntilState.DOMContentLoaded,
                Timeout = 20_000,
            });

            await FillLoginFormAsync(page, username, password);

            // Track the session so the browser stays open. Hook Disconnected
            // so we drop the reference when the user closes the window.
            lock (_lock) _sessions.Add((pw, browser));
            browser.Disconnected += (_, _) =>
            {
                lock (_lock) _sessions.RemoveAll(t => t.Browser == browser);
                try { pw?.Dispose(); } catch { }
            };
        }
        catch (Exception ex)
        {
            PluginLog.Warn($"AutoLogin '{shortcutName}': {ex.Message}");
            try { if (browser != null) await browser.CloseAsync(); } catch { }
            try { pw?.Dispose(); } catch { }
        }
    }

    /// <summary>Heuristic form filler. Tries the most common patterns in
    /// order; the first one that lands a value for both fields wins.</summary>
    private static async Task FillLoginFormAsync(IPage page, string username, string password)
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
        if (!pwFilled) return; // no password field — abort silently rather than mis-click

        foreach (var sel in submitSelectors)
        {
            try
            {
                var btn = page.Locator(sel).First;
                if (await btn.CountAsync() == 0) continue;
                await btn.ClickAsync(new LocatorClickOptions { Timeout = 5_000 });
                return;
            }
            catch { }
        }
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
