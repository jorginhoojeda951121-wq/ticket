using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using OpenQA.Selenium;
using OpenQA.Selenium.Chrome;
using OpenQA.Selenium.Support.UI;
using IRCTCTatkalBot;
using IRCTCTatkalBot.Helpers;
using IRCTCTatkalBot.Models;

namespace IRCTCTatkalBot.Services
{
    /// <summary>
    /// Manages a single Selenium WebDriver session for one account.
    /// Responsible for:
    ///   - Creating and configuring the ChromeDriver
    ///   - Handling login (username/password + captcha)
    ///   - Safe element interaction
    ///   - Screenshot capture for debugging
    /// </summary>
    public class SessionManager : IDisposable
    {
        private readonly Account _account;
        private readonly AccountManager _accountManager;
        private readonly ICaptchaSolver _captchaSolver;
        private IWebDriver? _driver;
        private bool _disposed;
        private bool _loginVerifiedOnce;

        public IWebDriver Driver
        {
            get
            {
                if (_driver == null)
                    throw new InvalidOperationException("Driver not initialized. Call StartDriver() first.");
                return _driver;
            }
        }

        /// <summary>Captcha solver used for login and forwarded to booking steps.</summary>
        public ICaptchaSolver CaptchaSolver => _captchaSolver;

        public SessionManager(Account account, AccountManager accountManager, ICaptchaSolver captchaSolver)
        {
            _account = account ?? throw new ArgumentNullException(nameof(account));
            _accountManager = accountManager ?? throw new ArgumentNullException(nameof(accountManager));
            _captchaSolver = captchaSolver ?? throw new ArgumentNullException(nameof(captchaSolver));
        }

        // ── Driver Lifecycle ──────────────────────────────────────────

        /// <summary>
        /// Creates and configures a new ChromeDriver instance.
        /// </summary>
        public void StartDriver()
        {
            if (_driver != null)
                throw new InvalidOperationException("Driver already started. Dispose first before starting a new one.");

            try
            {
                var options = new ChromeOptions();

                // Suppress notifications and popups
                options.AddArgument("--disable-notifications");
                options.AddArgument("--disable-popup-blocking");
                options.AddArgument("--disable-extensions");

                if (!AppSettings.Instance.ShowBrowserWindows)
                    options.AddArgument("--headless=new");

                // Isolated profile per account (prevents session collisions between parallel logins)
                string profileDir = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "IRCTCTatkalBot", "profiles", _account.Id.ToString("N"));
                Directory.CreateDirectory(profileDir);
                options.AddArgument($"--user-data-dir={profileDir}");

                // Use proxy if configured
                if (!string.IsNullOrWhiteSpace(_account.ProxyAddress))
                {
                    options.AddArgument($"--proxy-server={_account.ProxyAddress}");
                }

                string chromePath = (AppSettings.Instance.ChromeBinaryPath ?? string.Empty).Trim();
                if (chromePath.Length > 0 && File.Exists(chromePath))
                    options.BinaryLocation = chromePath;

                _driver = ChromeDriverFactory.Create(options);
                // Explicit WebDriverWaits only — large implicit wait slows full-page searches.
                _driver.Manage().Timeouts().ImplicitWait = TimeSpan.Zero;

                Logger.Info($"SessionManager: ChromeDriver started for {_account.Username}");
            }
            catch (Exception ex)
            {
                Logger.Error(ex, "SessionManager.StartDriver");
                throw;
            }
        }

        // ── Login Flow ────────────────────────────────────────────────

        /// <summary>
        /// Navigates to IRCTC and logs in with the account credentials + captcha.
        /// </summary>
        public async Task<bool> LoginAsync(CancellationToken ct = default)
        {
            if (_driver == null)
            {
                Logger.Error("SessionManager.LoginAsync: Driver not initialized");
                return false;
            }

            try
            {
                Logger.Info($"SessionManager: Logging in {_account.Username}...");

                var wait = new WebDriverWait(_driver, TimeSpan.FromSeconds(30));

                // Keep an open booking/results tab: do not navigate away if we're already mid-flow.
                if (IrctcSessionProbe.AppearsLoggedIn(_driver))
                {
                    Logger.Info($"SessionManager: ✓ Already logged in for {_account.Username} (skipping login).");
                    _loginVerifiedOnce = true;
                    await EnsureReadyForTrainSearchAsync(_driver, ct);
                    return true;
                }

                if (_loginVerifiedOnce && LooksLikeActiveBookingFlowInternal(_driver))
                {
                    Logger.Info($"SessionManager: ✓ Active booking/results session for {_account.Username} — skipping re-login and navigation.");
                    return true;
                }

                // Current IRCTC UX: landing page is train-search; login fields live in a modal opened via "LOGIN / REGISTER".
                _driver.Navigate().GoToUrl("https://www.irctc.co.in/nget/train-search");
                await Task.Delay(1200, ct);

                // If the reused profile is already logged in, don't force the login modal flow.
                if (IrctcSessionProbe.AppearsLoggedIn(_driver))
                {
                    Logger.Info($"SessionManager: ✓ Already logged in for {_account.Username} (skipping login).");
                    _loginVerifiedOnce = true;
                    await EnsureReadyForTrainSearchAsync(_driver, ct);
                    return true;
                }

                if (!IsUserIdFieldVisible(_driver))
                {
                    Logger.Info("SessionManager: Login form not visible — clicking LOGIN / REGISTER...");
                    ClickIrctcLoginRegisterButton(wait);
                    await Task.Delay(900, ct);
                }

                // Prefer fields inside IRCTC's login host (same scope as working Chrome extension: #divMain > app-login).
                var usernameField = wait.Until(d => FindFirstVisibleInLoginScopes(d,
                    By.CssSelector(
                        "input[type='text'][formcontrolname='userid'], input[type='text'][formControlName='userid'], " +
                        "input[formcontrolname='userid'], input[formControlName='userid']")))
                    ?? throw new WebDriverException("User id field not found.");

                // Fill username
                usernameField.Clear();
                usernameField.SendKeys(_account.Username);
                DispatchAngularInputChange(usernameField);
                await Task.Delay(500, ct);

                var passwordField = FindFirstVisibleInLoginScopes(_driver!,
                    By.CssSelector(
                        "input[type='password'][formcontrolname='password'], input[type='password'][formControlName='password'], " +
                        "input[formcontrolname='password'], input[formControlName='password']"))
                    ?? throw new WebDriverException("Password field not found after opening login.");
                passwordField.Clear();
                passwordField.SendKeys(_accountManager.GetPassword(_account));
                DispatchAngularInputChange(passwordField);
                await Task.Delay(500, ct);

                // Captcha is not always present (depends on IRCTC UX / A/B tests / risk scoring).
                var captchaInput = FindCaptchaInput(_driver);
                string? captchaText;

                if (captchaInput == null)
                {
                    Logger.Warning("SessionManager: Captcha field not found — attempting login without captcha.");
                    captchaText = null;
                }
                else
                {
                    if (_captchaSolver is ManualCaptchaSolver)
                    {
                        captchaText = await ManualCaptchaSolver.WaitForUserEntryAsync(() => FindCaptchaInput(_driver!), ct);
                    }
                    else
                    {
                        var captchaImgWait = new WebDriverWait(_driver, TimeSpan.FromSeconds(15));
                        var captchaElement = captchaImgWait.Until(d =>
                            FindFirstCaptchaImageInLoginScopes(d))
                            ?? throw new WebDriverException("Captcha image not found in login dialog.");
                        string captchaBase64 = GetElementScreenshot(captchaElement);
                        captchaText = await _captchaSolver.SolveImageCaptchaAsync(captchaBase64, ct);
                    }
                }

                if (captchaInput != null && string.IsNullOrWhiteSpace(captchaText))
                {
                    Logger.Warning("SessionManager: Captcha missing or solving failed");
                    TakeScreenshot("captcha_failed");
                    return false;
                }

                if (captchaInput != null && _captchaSolver is not ManualCaptchaSolver)
                {
                    captchaInput.Clear();
                    captchaInput.SendKeys(captchaText);
                    DispatchAngularInputChange(captchaInput);
                    await Task.Delay(500, ct);
                }
                else
                {
                    await Task.Delay(300, ct);
                    // Manual entry: nudge Angular bindings before submit (extension uses input/change events).
                    if (captchaInput != null)
                    {
                        var capAgain = FindCaptchaInput(_driver!);
                        if (capAgain != null)
                            DispatchAngularInputChange(capAgain);
                    }
                }

                // Submit inside modal
                // Click submit button that belongs to the currently opened login modal/form.
                // IRCTC pages often contain multiple "submit" buttons outside the modal.
                // Angular often omits a native <form>; rely on dialog/modal ancestors + Enter fallback.
                try
                {
                    var loginBtn = FindLoginSubmitButtonWithRetry();
                    SafeClick(loginBtn);
                }
                catch (WebDriverException ex) when (
                    ex is WebDriverTimeoutException ||
                    ex.Message.Contains("submit", StringComparison.OrdinalIgnoreCase) ||
                    ex.Message.Contains("login form", StringComparison.OrdinalIgnoreCase))
                {
                    Logger.Warning("SessionManager: Login submit button not resolved — trying Enter on password field.");
                    var pwd = FindFirstVisibleInLoginScopes(_driver!,
                        By.CssSelector(
                            "input[type='password'][formcontrolname='password'], input[type='password'][formControlName='password'], " +
                            "input[formcontrolname='password'], input[formControlName='password']"));
                    pwd?.SendKeys(Keys.Enter);
                }

                // Post-login: IRCTC frequently redirects or re-renders the header.
                wait.Until(d => IrctcSessionProbe.AppearsLoggedIn(d));
                _loginVerifiedOnce = true;
                await EnsureReadyForTrainSearchAsync(_driver, ct);
                Logger.Info($"SessionManager: ✓ Login successful for {_account.Username}");
                return true;
            }
            catch (OperationCanceledException)
            {
                Logger.Warning("SessionManager.LoginAsync: Cancelled");
                return false;
            }
            catch (Exception ex)
            {
                Logger.Error(ex, "SessionManager.LoginAsync");
                TakeScreenshot("login_error");
                return false;
            }
        }

        /// <summary>
        /// Fast check for an existing authenticated session in the current tab.
        /// Once login has succeeded once in this session, it will return true immediately without re-checking
        /// unless the driver state suggests we've been logged out.
        /// </summary>
        public bool IsLoggedIn()
        {
            if (_driver == null) return false;

            // If we've successfully logged in before, verify we're still logged in or check if UI has changed
            if (_loginVerifiedOnce)
            {
                if (IrctcSessionProbe.AppearsLoggedIn(_driver))
                    return true;

                // Train-list often hides Welcome/Logout in the header but the booking strip still works.
                if (LooksLikeActiveBookingFlowInternal(_driver))
                {
                    Logger.Info("SessionManager.IsLoggedIn: On train-list / booking flow — keeping session (header may show LOGIN).");
                    return true;
                }

                Logger.Warning("SessionManager.IsLoggedIn: Previously logged in but session appears to have ended.");
                _loginVerifiedOnce = false;
                return false;
            }

            // Fresh driver / never completed LoginAsync in this session: never skip login based on heuristics
            // (false "logged in" on train-search broke immediate mode without pre-login).
            return false;
        }

        /// <summary>
        /// After login, land on train-search so SearchTrainAsync runs on the expected shell (same as booking retry path).
        /// Does not navigate away from train-list / booking pages.
        /// </summary>
        private static async Task EnsureReadyForTrainSearchAsync(IWebDriver driver, CancellationToken ct)
        {
            string u = driver.Url ?? "";
            if (u.Contains("train-list", StringComparison.OrdinalIgnoreCase) ||
                u.Contains("/nget/booking/", StringComparison.OrdinalIgnoreCase))
                return;

            if (!u.Contains("train-search", StringComparison.OrdinalIgnoreCase))
            {
                Logger.Info("SessionManager: Navigating to train-search after login.");
                driver.Navigate().GoToUrl("https://www.irctc.co.in/nget/train-search");
                await Task.Delay(800, ct);
            }
        }

        /// <summary>
        /// Train-list / booking flow marker (also covered by <see cref="IrctcSessionProbe"/>; kept for explicit session persistence).
        /// </summary>
        private static bool LooksLikeActiveBookingFlowInternal(IWebDriver driver)
        {
            try
            {
                string url = driver.Url ?? "";
                if (!url.Contains("train-list", StringComparison.OrdinalIgnoreCase) &&
                    !url.Contains("/nget/booking/", StringComparison.OrdinalIgnoreCase))
                    return false;

                foreach (var by in new[]
                         {
                             By.XPath("//*[contains(translate(normalize-space(.),'ABCDEFGHIJKLMNOPQRSTUVWXYZ','abcdefghijklmnopqrstuvwxyz'),'modify search')]"),
                             By.XPath("//*[contains(translate(normalize-space(.),'ABCDEFGHIJKLMNOPQRSTUVWXYZ','abcdefghijklmnopqrstuvwxyz'),'results for')]"),
                             By.CssSelector("app-train-list"),
                         })
                {
                    foreach (var el in driver.FindElements(by))
                    {
                        try
                        {
                            if (el.Displayed)
                                return true;
                        }
                        catch
                        {
                            /* stale */
                        }
                    }
                }
            }
            catch
            {
                /* ignore */
            }

            return false;
        }

        private static IWebElement? FindCaptchaInput(IWebDriver driver)
        {
            var bys = new[]
            {
                By.CssSelector(
                    "input[type='text'][formcontrolname='captcha'], input[type='text'][formControlName='captcha']"),
                By.CssSelector("input[formcontrolname='captcha'], input[formControlName='captcha']"),
                By.CssSelector("input[placeholder*='captcha' i]"),
                By.CssSelector("input[id*='captcha' i], input[name*='captcha' i]"),
            };

            foreach (var by in bys)
            {
                var el = FindFirstVisibleInLoginScopes(driver, by);
                if (el != null)
                    return el;
            }

            return null;
        }

        private static bool IsUserIdFieldVisible(IWebDriver driver)
        {
            return FindFirstVisibleInLoginScopes(driver,
                       By.CssSelector(
                           "input[type='text'][formcontrolname='userid'], input[type='text'][formControlName='userid'], " +
                           "input[formcontrolname='userid'], input[formControlName='userid']"))
                   != null;
        }

        /// <summary>IRCTC shows "LOGIN / REGISTER" on the header; the Angular login form opens in a modal.</summary>
        private void ClickIrctcLoginRegisterButton(WebDriverWait wait)
        {
            var trigger = wait.Until(driver =>
            {
                foreach (var by in new[]
                         {
                             By.XPath("//button[contains(normalize-space(.), 'LOGIN')]"),
                             By.XPath("//a[contains(normalize-space(.), 'LOGIN')]"),
                             By.PartialLinkText("LOGIN"),
                             By.XPath("//*[@role='button' and contains(., 'LOGIN')]"),
                         })
                {
                    try
                    {
                        foreach (var el in driver.FindElements(by))
                        {
                            try
                            {
                                if (el.Displayed && el.Enabled)
                                    return el;
                            }
                            catch
                            {
                                /* stale */
                            }
                        }
                    }
                    catch
                    {
                        /* ignore */
                    }
                }

                return null;
            });

            ArgumentNullException.ThrowIfNull(trigger);
            SafeClick(trigger);
        }

        private void DispatchAngularInputChange(IWebElement el)
        {
            try
            {
                var js = (IJavaScriptExecutor)Driver;
                js.ExecuteScript(
                    "var el = arguments[0]; if (!el) return; " +
                    "el.dispatchEvent(new Event('input', {bubbles:true})); " +
                    "el.dispatchEvent(new Event('change', {bubbles:true}));", el);
            }
            catch (Exception ex)
            {
                Logger.Warning($"SessionManager: DispatchAngularInputChange: {ex.Message}");
            }
        }

        /// <summary>IRCTC login fields live under <c>app-login</c> (extension uses <c>#divMain &gt; app-login</c>).</summary>
        private static List<ISearchContext> GetLoginFieldSearchContexts(IWebDriver driver)
        {
            var list = new List<ISearchContext>();
            foreach (var rootBy in new[] { By.CssSelector("#divMain > app-login"), By.CssSelector("app-login") })
            {
                foreach (var root in driver.FindElements(rootBy))
                {
                    try
                    {
                        if (root.Displayed)
                            list.Add(root);
                    }
                    catch
                    {
                        /* stale */
                    }
                }
            }

            list.Add(driver);
            return list;
        }

        private static IWebElement? FindFirstVisibleInLoginScopes(IWebDriver driver, By by)
        {
            foreach (ISearchContext ctx in GetLoginFieldSearchContexts(driver))
            {
                IWebElement? el = FindFirstVisible(ctx, by);
                if (el != null)
                    return el;
            }

            return null;
        }

        private static IWebElement? FindFirstCaptchaImageInLoginScopes(IWebDriver driver)
        {
            By[] bys =
            {
                By.CssSelector(".captcha-img"),
                By.CssSelector("img.captcha-img"),
                By.CssSelector("img[src*='captcha']"),
                By.CssSelector("img[src*='Captcha']"),
            };

            foreach (ISearchContext ctx in GetLoginFieldSearchContexts(driver))
            {
                foreach (var by in bys)
                {
                    IWebElement? el = FindFirstVisibleImage(ctx, by);
                    if (el != null)
                        return el;
                }
            }

            return null;
        }

        private static IWebElement? FindFirstVisible(ISearchContext context, By by)
        {
            foreach (var el in context.FindElements(by))
            {
                try
                {
                    if (el.Displayed)
                        return el;
                }
                catch
                {
                    /* stale */
                }
            }

            return null;
        }

        private static IWebElement? FindFirstVisibleImage(ISearchContext context, By by)
        {
            foreach (var el in context.FindElements(by))
            {
                try
                {
                    if (el.Displayed && el.TagName.Equals("img", StringComparison.OrdinalIgnoreCase))
                        return el;
                }
                catch
                {
                    /* stale */
                }
            }

            return null;
        }

        private IWebElement FindLoginSubmitButtonWithRetry()
        {
            // Modal fields can appear slowly; password may briefly detach during captcha UX.
            var waitSubmit = new WebDriverWait(_driver, TimeSpan.FromSeconds(22));
            try
            {
                return waitSubmit.Until(driver =>
                {
                    try
                    {
                        IWebElement? root = FindFirstVisibleInLoginScopes(driver,
                            By.CssSelector(
                                "input[type='password'][formcontrolname='password'], input[type='password'][formControlName='password'], " +
                                "input[formcontrolname='password'], input[formControlName='password']"))
                            ?? FindFirstVisibleInLoginScopes(driver,
                                By.CssSelector(
                                    "input[type='text'][formcontrolname='userid'], input[type='text'][formControlName='userid'], " +
                                    "input[formcontrolname='userid'], input[formControlName='userid']"));
                        if (root == null)
                            return null;

                        return FindLoginSubmitButton(root);
                    }
                    catch (StaleElementReferenceException)
                    {
                        return null;
                    }
                    catch (WebDriverException)
                    {
                        return null;
                    }
                })!;
            }
            catch (WebDriverTimeoutException)
            {
                IWebElement? root = FindFirstVisibleInLoginScopes(_driver!,
                    By.CssSelector(
                        "input[type='password'][formcontrolname='password'], input[type='password'][formControlName='password'], " +
                        "input[formcontrolname='password'], input[formControlName='password']"))
                    ?? FindFirstVisibleInLoginScopes(_driver!,
                        By.CssSelector(
                            "input[type='text'][formcontrolname='userid'], input[type='text'][formControlName='userid'], " +
                            "input[formcontrolname='userid'], input[formControlName='userid']"));
                if (root == null)
                    throw new WebDriverException("Login form not found for submit button.");

                return FindLoginSubmitButton(root);
            }
        }

        private IWebElement FindLoginSubmitButton(IWebElement root)
        {
            // 1) Prefer buttons inside app-login (IRCTC extension scope), then form, Material dialog, or generic modal.
            string[] containerXPaths =
            {
                "ancestor::app-login[1]",
                "ancestor::form[1]",
                "ancestor::*[@role='dialog'][1]",
                "ancestor::mat-dialog-container[1]",
                "ancestor::div[contains(concat(' ', normalize-space(@class), ' '), ' modal ')][1]",
                "ancestor::div[contains(translate(@class,'ABCDEFGHIJKLMNOPQRSTUVWXYZ','abcdefghijklmnopqrstuvwxyz'),'login')][1]",
                "ancestor::div[contains(translate(@class,'ABCDEFGHIJKLMNOPQRSTUVWXYZ','abcdefghijklmnopqrstuvwxyz'),'mdc-dialog')][1]",
            };

            string[] buttonXPaths =
            {
                "//button[@type='submit']",
                "//button[contains(translate(normalize-space(.),'ABCDEFGHIJKLMNOPQRSTUVWXYZ','abcdefghijklmnopqrstuvwxyz'),'sign in')]",
                "//button[contains(translate(normalize-space(.),'ABCDEFGHIJKLMNOPQRSTUVWXYZ','abcdefghijklmnopqrstuvwxyz'),'login')]",
                "//button[contains(translate(normalize-space(.),'ABCDEFGHIJKLMNOPQRSTUVWXYZ','abcdefghijklmnopqrstuvwxyz'),'log in')]",
                "//input[@type='submit' or contains(@value,'LOGIN') or contains(@value,'Sign') or contains(@value,'SIGN')]",
            };

            foreach (string cont in containerXPaths)
            {
                foreach (string btn in buttonXPaths)
                {
                    foreach (var el in root.FindElements(By.XPath(cont + btn)))
                    {
                        try
                        {
                            if (el.Displayed && el.Enabled)
                                return el;
                        }
                        catch
                        {
                            /* stale */
                        }
                    }
                }
            }

            // 2) Legacy: nearest form only (narrower XPaths used before dialog-aware paths).
            foreach (var by in new[]
                     {
                         By.XPath("ancestor::form[1]//button[@type='submit']"),
                         By.XPath("ancestor::form[1]//button[(@type='button' or @type='submit') and (contains(translate(normalize-space(.),'ABCDEFGHIJKLMNOPQRSTUVWXYZ','abcdefghijklmnopqrstuvwxyz'),'login') or contains(translate(normalize-space(.),'ABCDEFGHIJKLMNOPQRSTUVWXYZ','abcdefghijklmnopqrstuvwxyz'),'log in'))]"),
                         By.XPath("ancestor::form[1]//input[@type='submit' or @role='button' or @value='LOGIN' or @value='Log In' or @value='Log in']")
                     })
            {
                foreach (var el in root.FindElements(by))
                {
                    try
                    {
                        if (el.Displayed && el.Enabled)
                            return el;
                    }
                    catch
                    {
                        /* stale */
                    }
                }
            }

            // 3) Fallback: scan entire DOM (less reliable, but better than failing hard).
            foreach (var el in _driver!.FindElements(By.CssSelector("button[type='submit'], input[type='submit']")))
            {
                try
                {
                    if (el.Displayed && el.Enabled)
                        return el;
                }
                catch
                {
                    /* stale */
                }
            }

            foreach (var el in _driver!.FindElements(By.XPath("//button[contains(translate(normalize-space(.),'ABCDEFGHIJKLMNOPQRSTUVWXYZ','abcdefghijklmnopqrstuvwxyz'),'login') or contains(translate(normalize-space(.),'ABCDEFGHIJKLMNOPQRSTUVWXYZ','abcdefghijklmnopqrstuvwxyz'),'log in')]")))
            {
                try
                {
                    if (el.Displayed && el.Enabled)
                        return el;
                }
                catch
                {
                    /* stale */
                }
            }

            throw new WebDriverException("Could not find clickable login submit button.");
        }

        // ── Helper Methods ────────────────────────────────────────────

        /// <summary>
        /// Safely clicks an element, scrolling it into view if necessary.
        /// </summary>
        public void SafeClick(IWebElement element)
        {
            try
            {
                var js = (IJavaScriptExecutor)Driver;
                js.ExecuteScript("arguments[0].scrollIntoView(true);", element);
                Task.Delay(200).Wait();
                element.Click();
            }
            catch (Exception firstClickEx) when (
                firstClickEx is ElementNotInteractableException ||
                firstClickEx is ElementClickInterceptedException ||
                firstClickEx is StaleElementReferenceException)
            {
                // Fallback to JavaScript click, then Enter fallback.
                try
                {
                    var js = (IJavaScriptExecutor)Driver;
                    js.ExecuteScript("arguments[0].click();", element);
                }
                catch
                {
                    // Sometimes JS click still doesn't trigger the handler; try keyboard submit.
                    try
                    {
                        element.SendKeys(Keys.Enter);
                    }
                    catch { /* ignore */ }
                }
            }
        }

        /// <summary>
        /// Captures a screenshot and saves it to the app data folder.
        /// </summary>
        public string TakeScreenshot(string label = "screenshot")
        {
            try
            {
                string screenshotDir = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                    "IRCTCTatkalBot", "Screenshots");
                Directory.CreateDirectory(screenshotDir);

                string filename = $"{label}_{DateTime.Now:yyyyMMdd_HHmmss}.png";
                string filePath = Path.Combine(screenshotDir, filename);

                var screenshot = ((ITakesScreenshot)Driver).GetScreenshot();
                screenshot.SaveAsFile(filePath);

                Logger.Info($"SessionManager: Screenshot saved to {filePath}");
                return filePath;
            }
            catch (Exception ex)
            {
                Logger.Error(ex, "SessionManager.TakeScreenshot");
                return string.Empty;
            }
        }

        /// <summary>
        /// Gets a screenshot of a specific element (for captcha solving).
        /// </summary>
        private string GetElementScreenshot(IWebElement element)
        {
            try
            {
                var screenshot = ((ITakesScreenshot)element).GetScreenshot();
                return Convert.ToBase64String(screenshot.AsByteArray);
            }
            catch (Exception ex)
            {
                Logger.Error(ex, "SessionManager.GetElementScreenshot");
                return string.Empty;
            }
        }

        // ── IDisposable ───────────────────────────────────────────────

        public void Dispose()
        {
            if (_disposed) return;

            try
            {
                _driver?.Quit();
                _driver?.Dispose();
                Logger.Info("SessionManager: Driver disposed");
            }
            catch (Exception ex)
            {
                Logger.Warning($"SessionManager.Dispose: {ex.Message}");
            }

            _disposed = true;
            GC.SuppressFinalize(this);
        }

        ~SessionManager() => Dispose();
    }
}
