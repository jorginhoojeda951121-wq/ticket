using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using OpenQA.Selenium;
using OpenQA.Selenium.Interactions;
using OpenQA.Selenium.Support.UI;
using IRCTCTatkalBot.Helpers;
using IRCTCTatkalBot.Models;

namespace IRCTCTatkalBot.Services
{
    public class BookingEngine
    {
        private readonly SessionManager _session;
        private readonly BookingConfig _config;
        private readonly BookingResult _result;
        private IWebDriver Driver => _session.Driver;
        private IJavaScriptExecutor JS => (IJavaScriptExecutor)Driver;

        // ──────────────────────────────────────────────────────────────
        //  Timeouts – adjust if your connection is slow
        // ──────────────────────────────────────────────────────────────
        private const int SHORT_WAIT  = 10;   // seconds – fast DOM elements
        private const int MEDIUM_WAIT = 15;   // seconds – page navigation / search button
        private const int LONG_WAIT   = 35;   // seconds – train list load
        /// <summary>Train-search Angular shell + station autocomplete; explicit wait only (implicit wait is 0).</summary>
        private const int TRAIN_SEARCH_INPUT_WAIT = 22;
        /// <summary>IRCTC often opens Book Now slowly after class cell click; longer than SHORT_WAIT.</summary>
        private const int BOOK_NOW_WAIT = 22;

        private string? _lastSelectTrainFailureReason;

        public event Action<BookingResult>? OnStatusChanged;

        public BookingEngine(SessionManager session, BookingConfig config)
        {
            _session = session;
            _config  = config;
            _result  = new BookingResult { AccountId = _config.AccountId };
        }

        // ────────────────────────────────────────────────────────────────
        //  ENTRY POINT
        // ────────────────────────────────────────────────────────────────
        public async Task<BookingResult> RunAsync(CancellationToken ct = default)
        {
            try
            {
                // ── BUG-FIX 3: Avoid full re-login on retry ──────────────
                UpdateStatus(BookingStatus.LoggingIn);
                bool alreadyIn = _session.IsLoggedIn() && IrctcSessionProbe.SessionLikelyAlive(Driver);
                if (alreadyIn)
                {
                    Logger.Info($"BookingEngine: Session still active – skipping re-login.");
                    Logger.Info("BookingEngine: Note — IRCTC may still show LOGIN / REGISTER in the header while your session is valid; automation relies on cookies and booking pages, not that chip.");
                    Driver.Navigate().GoToUrl("https://www.irctc.co.in/nget/train-search");
                    await Task.Delay(900, ct);
                }
                else
                {
                    if (!await _session.LoginAsync(ct))
                        return Fail("Login failed");
                }

                UpdateStatus(BookingStatus.Searching);
                if (!await SearchTrainAsync(ct))
                    return Fail("Search failed");

                UpdateStatus(BookingStatus.SelectingTrain);
                _lastSelectTrainFailureReason = null;
                if (!await SelectTrainAndClassAsync(ct))
                    return Fail(_lastSelectTrainFailureReason
                        ?? $"Could not select train/class for train {_config.TrainNumber?.Trim()} class {_config.TrainClass}. url={Driver.Url}");

                UpdateStatus(BookingStatus.FillingPassengers);
                if (!await FillPassengersAsync(ct))
                    return Fail("Passenger fill failed");

                UpdateStatus(BookingStatus.ProcessingPayment);
                if (!await ProceedToPaymentAsync(ct))
                    return Fail("Payment failed");

                _result.Status      = BookingStatus.Completed;
                _result.CompletedAt = DateTime.Now;
                Logger.Info($"BookingEngine: Completed in {_result.ElapsedSeconds:F1}s");
            }
            catch (OperationCanceledException)
            {
                _result.Status       = BookingStatus.Cancelled;
                _result.ErrorMessage = "Cancelled.";
            }
            catch (Exception ex)
            {
                Logger.Error(ex, "BookingEngine.RunAsync");
                _result.ScreenshotPath = _session.TakeScreenshot("error");
                return Fail(ex.Message);
            }
            finally { OnStatusChanged?.Invoke(_result); }
            return _result;
        }

        // ────────────────────────────────────────────────────────────────
        //  SEARCH
        // ────────────────────────────────────────────────────────────────
        private async Task<bool> SearchTrainAsync(CancellationToken ct)
        {
            try
            {
                Logger.Info($"BookingEngine: Searching {_config.FromStation} → {_config.ToStation}");

                await DismissBlockingOverlaysAsync(ct);

                // ── From station ──────────────────────────────────────────
                var fromInput = Wait(TRAIN_SEARCH_INPUT_WAIT).Until(d =>
                    FindFirstVisibleStationInput(d, forOrigin: true));
                ClearAndType(fromInput!, _config.FromStation);
                await Task.Delay(550, ct);
                ClickAutocompleteMatching(_config.FromStation);
                await Task.Delay(280, ct);

                // ── To station ────────────────────────────────────────────
                var toInput = Wait(TRAIN_SEARCH_INPUT_WAIT).Until(d =>
                    FindFirstVisibleStationInput(d, forOrigin: false));
                ClearAndType(toInput!, _config.ToStation);
                await Task.Delay(550, ct);
                ClickAutocompleteMatching(_config.ToStation);
                await Task.Delay(280, ct);

                // ── Journey date – use JS so calendar binding isn't needed ─
                // ── BUG-FIX: "Journey date may not have bound to calendar" ─
                string dateStr = _config.JourneyDate.ToString("dd/MM/yyyy");
                var dateInput = TryFind(Driver, "p-calendar input,input[placeholder*='Date']");
                if (dateInput != null)
                {
                    // Click to open calendar, then set via JS + fire Angular events
                    _session.SafeClick(dateInput);
                    await Task.Delay(400, ct);
                    JS.ExecuteScript(
                        "arguments[0].value = arguments[1]; " +
                        "arguments[0].dispatchEvent(new Event('input', {bubbles:true})); " +
                        "arguments[0].dispatchEvent(new Event('change', {bubbles:true}));",
                        dateInput, dateStr);
                    await Task.Delay(300, ct);
                    // Press Escape to close the calendar picker if open
                    dateInput.SendKeys(Keys.Escape);
                    await Task.Delay(300, ct);
                }
                Logger.Info($"BookingEngine: Journey date set to {dateStr}");

                // ── Journey class ─────────────────────────────────────────
                bool classSet = false;
                try
                {
                    // Attempt 1: ng-select or p-dropdown
                    var classDropdown = TryFind(Driver,
                        "p-dropdown[formcontrolname='journeyClass'], " +
                        "ng-select[formcontrolname='journeyClass']");
                    if (classDropdown != null)
                    {
                        _session.SafeClick(classDropdown);
                        await Task.Delay(500, ct);
                        string[] classAliases = IrctcTrainClass.JourneySearchAliases(_config.TrainClass);
                        var opt = Driver.FindElements(By.CssSelector(
                            ".p-dropdown-item, .ng-option"))
                            .FirstOrDefault(o =>
                                classAliases.Any(a =>
                                    o.Text.Contains(a, StringComparison.OrdinalIgnoreCase)));
                        if (opt != null) { _session.SafeClick(opt); classSet = true; }
                    }
                }
                catch { }
                if (!classSet)
                {
                    try
                    {
                        // Attempt 2: native <select>
                        var sel = TryFind(Driver, "select[aria-label*='Class']");
                        if (sel != null)
                        {
                            foreach (string a in IrctcTrainClass.JourneySearchAliases(_config.TrainClass))
                            {
                                try
                                {
                                    new SelectHelper(sel).SelectByText(a);
                                    classSet = true;
                                    break;
                                }
                                catch { /* try next alias */ }
                            }
                        }
                    }
                    catch { }
                }
                if (classSet) Logger.Info($"BookingEngine: Journey class dropdown updated (match contains '{_config.TrainClass}').");

                // ── Quota ─────────────────────────────────────────────────
                bool quotaSet = false;
                try
                {
                    var quotaDd = TryFind(Driver,
                        "p-dropdown[formcontrolname='quota'], ng-select[formcontrolname='quota']");
                    if (quotaDd != null)
                    {
                        _session.SafeClick(quotaDd);
                        await Task.Delay(500, ct);
                        var opt = Driver.FindElements(By.CssSelector(".p-dropdown-item, .ng-option"))
                            .FirstOrDefault(o =>
                                o.Text.Contains("TATKAL", StringComparison.OrdinalIgnoreCase));
                        if (opt != null) { _session.SafeClick(opt); quotaSet = true; }
                    }
                }
                catch { }
                if (!quotaSet)
                {
                    try
                    {
                        var sel = TryFind(Driver, "select[aria-label*='Quota']");
                        if (sel != null)
                        { new SelectHelper(sel).SelectByText(_config.Quota); quotaSet = true; }
                    }
                    catch { }
                }
                if (quotaSet) Logger.Info($"BookingEngine: Quota dropdown updated (match contains 'TATKAL').");

                // ── Click Search ──────────────────────────────────────────
                var searchBtn = Wait(MEDIUM_WAIT).Until(d =>
                {
                    return FindFirstVisibleClickable(d, new[]
                    {
                        "button.search_btn",
                        "button[class*='train_Search']",
                        "button[title*='Search']",
                        "button[title*='search']",
                        "button.search_btn_train",
                        "button[type='submit'][class*='search']",
                        "button[type='submit'][class*='Search']",
                        "input.search_btn[type='submit']",
                        "button.btn-primary.search_btn",
                        "button[aria-label*='Search']",
                        "button[aria-label*='search']",
                    });
                });
                _session.SafeClick(searchBtn!);

                // ── Wait for train-list URL ────────────────────────────────
                Wait(MEDIUM_WAIT).Until(d => d.Url.Contains("train-list"));
                await WaitForTrainListRowsAsync(ct);
                Logger.Info($"BookingEngine: Search submitted successfully. url={Driver.Url}");
                return true;
            }
            catch (Exception ex)
            {
                Logger.Error(ex, "BookingEngine.SearchTrainAsync");
                _session.TakeScreenshot("search_error");
                return false;
            }
        }

        // ────────────────────────────────────────────────────────────────
        //  SELECT TRAIN & CLASS
        // ────────────────────────────────────────────────────────────────
        private async Task<bool> SelectTrainAndClassAsync(CancellationToken ct)
        {
            _lastSelectTrainFailureReason = null;
            try
            {
                string trainNo = (_config.TrainNumber ?? "").Trim();
                if (string.IsNullOrEmpty(trainNo))
                {
                    _lastSelectTrainFailureReason =
                        "Train number is required. Enter the numeric train number (e.g. 12215) so the correct train row is selected.";
                    Logger.Error($"BookingEngine: {_lastSelectTrainFailureReason}");
                    return false;
                }

                if (!PreFlightValidator.IsValidIrctcTrainNumber(trainNo))
                {
                    _lastSelectTrainFailureReason =
                        $"Train number must be 4–6 digits (got \"{trainNo}\"). Example: 12215.";
                    Logger.Error($"BookingEngine: {_lastSelectTrainFailureReason}");
                    return false;
                }

                string[] classTokens = BuildClassMatchTokens(_config.TrainClass);
                Logger.Info($"BookingEngine: Selecting train {trainNo}, class tokens: {string.Join(", ", classTokens)}");

                IReadOnlyList<IWebElement>? trainRows = null;
                string[] rowSelectors =
                {
                    "app-train-avl-enq",
                    "div.train-avl-holder",
                    "div.train-list",
                    "div.train-brd-dtl",
                    ".train-info-block",
                };

                foreach (string sel in rowSelectors)
                {
                    try
                    {
                        trainRows = Wait(LONG_WAIT).Until(d =>
                        {
                            var rows = d.FindElements(By.CssSelector(sel));
                            return rows.Count > 0 ? rows : null;
                        });
                        if (trainRows?.Count > 0)
                        {
                            Logger.Info($"BookingEngine: Found {trainRows.Count} train row(s) using '{sel}'.");
                            break;
                        }
                    }
                    catch { /* try next selector */ }
                }

                if (trainRows == null || trainRows.Count == 0)
                {
                    _lastSelectTrainFailureReason = "No train rows appeared on the results page.";
                    Logger.Error($"BookingEngine: {_lastSelectTrainFailureReason}");
                    _session.TakeScreenshot("select_error");
                    return false;
                }

                IWebElement? trainRow = FindTrainRowForNumber(trainRows, trainNo);
                if (trainRow == null)
                {
                    _lastSelectTrainFailureReason =
                        $"Train {trainNo} was not found in the search results. Check train number, route, date, and quota.";
                    Logger.Error($"BookingEngine: {_lastSelectTrainFailureReason}");
                    _session.TakeScreenshot("select_error");
                    return false;
                }

                Logger.Info("BookingEngine: Matched train row — scrolling into view.");
                JS.ExecuteScript("arguments[0].scrollIntoView({block:'center'});", trainRow);
                await Task.Delay(400, ct);

                TryExpandTrainRow(trainRow);

                IWebElement? classBtn = FindClassAvailabilityElement(trainRow, classTokens);
                if (classBtn == null)
                {
                    _lastSelectTrainFailureReason =
                        $"Could not find a '{_config.TrainClass}' availability cell for train {trainNo}. The site layout may have changed, or this class is not offered on this train.";
                    Logger.Error($"BookingEngine: {_lastSelectTrainFailureReason}");
                    _session.TakeScreenshot("select_error");
                    return false;
                }

                Logger.Info("BookingEngine: Clicking class / availability cell.");
                _session.SafeClick(classBtn);
                await Task.Delay(600, ct);

                await TryClickAvailabilityRefreshAsync(trainRow, ct);
                await WaitForAvailabilityLoadedAsync(trainRow, ct);
                await DismissBlockingOverlaysAsync(ct);

                // Book Now must belong to this train's card and be enabled (IRCTC shows "please select class" if not).
                var bookBtn = WaitForEnabledBookNowNearTrain(trainNo, trainRow, BOOK_NOW_WAIT + 10)
                              ?? WaitForBookNowButton(8);
                if (bookBtn == null)
                {
                    _lastSelectTrainFailureReason =
                        $"Book Now did not become enabled within {BOOK_NOW_WAIT + 10}s for train {trainNo} class '{_config.TrainClass}'. Select the class box on the site so availability loads (Refresh), then retry.";
                    Logger.Error($"BookingEngine: {_lastSelectTrainFailureReason}");
                    _session.TakeScreenshot("select_error");
                    return false;
                }

                Logger.Info("BookingEngine: Clicking Book Now (scoped + enabled).");
                _session.SafeClick(bookBtn);
                await Task.Delay(1200, ct);
                try
                {
                    new WebDriverWait(Driver, TimeSpan.FromSeconds(12)).Until(d =>
                        (d.Url ?? "").Contains("psgn", StringComparison.OrdinalIgnoreCase)
                        || (d.Url ?? "").Contains("passenger", StringComparison.OrdinalIgnoreCase)
                        || TryFind(d, "app-passenger, app-passenger-input, .passenger-entry") != null);
                }
                catch
                {
                    /* navigation may still be in progress */
                }

                return true;
            }
            catch (Exception ex)
            {
                Logger.Error(ex, "BookingEngine.SelectTrainAndClassAsync");
                _lastSelectTrainFailureReason ??= $"Select train/class error: {ex.Message}";
                _session.TakeScreenshot("select_error");
                return false;
            }
        }

        private static IWebElement? FindTrainRowForNumber(IReadOnlyList<IWebElement> rows, string trainNo)
        {
            foreach (var r in rows)
            {
                try
                {
                    if (!r.Displayed)
                        continue;
                    string inner = "";
                    try { inner = r.GetDomProperty("innerHTML") ?? ""; } catch { /* not all drivers expose */ }
                    string block = (r.Text ?? "") + " " + inner;
                    if (block.Contains(trainNo, StringComparison.Ordinal))
                        return r;
                }
                catch
                {
                    /* stale */
                }
            }

            string[] numSelectors =
            {
                ".train-number", ".trainNo", "h2", "p.train-number",
                "strong", "div.train-brd-dtl p", "span.trainno", "span[class*='train']",
            };

            foreach (var r in rows)
            {
                try
                {
                    if (!r.Displayed)
                        continue;
                    foreach (string s in numSelectors)
                    {
                        try
                        {
                            var el = r.FindElement(By.CssSelector(s));
                            if ((el.Text ?? "").Contains(trainNo, StringComparison.Ordinal))
                                return r;
                        }
                        catch
                        {
                            /* no such sub-element */
                        }
                    }
                }
                catch
                {
                    /* stale row */
                }
            }

            return null;
        }

        /// <summary>IRCTC sometimes collapses rows — best-effort expand before picking class.</summary>
        private static void TryExpandTrainRow(IWebElement row)
        {
            try
            {
                var toggles = row.FindElements(By.CssSelector(
                    "[class*='expand'],[class*='accordion'],[class*='toggle'],[class*='down-arrow'],i.fa-chevron-down,mat-icon"));
                foreach (var t in toggles)
                {
                    try
                    {
                        if (t.Displayed && t.Enabled)
                        {
                            t.Click();
                            break;
                        }
                    }
                    catch { /* ignore */ }
                }
            }
            catch { /* ignore */ }
        }

        private static string[] BuildClassMatchTokens(string trainClass)
        {
            string c = IrctcTrainClass.NormalizeToCanonical(trainClass ?? "").Trim();
            var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (c.Length == 0)
                return Array.Empty<string>();

            set.Add(c);
            switch (c)
            {
                case "1AC":
                case "1A":
                    set.Add("1A");
                    set.Add("1AC");
                    set.Add("FIRST");
                    set.Add("AC FIRST");
                    set.Add("(1A)");
                    break;
                case "2AC":
                case "2A":
                    set.Add("2A");
                    set.Add("2AC");
                    set.Add("AC 2 TIER");
                    set.Add("(2A)");
                    break;
                case "3AC":
                case "3A":
                    set.Add("3A");
                    set.Add("3AC");
                    set.Add("AC 3 TIER");
                    set.Add("3 TIER");
                    set.Add("(3A)");
                    break;
                case "SL":
                    set.Add("SL");
                    set.Add("SLEEPER");
                    set.Add("(SL)");
                    break;
                case "CC":
                    set.Add("CC");
                    set.Add("CHAIR");
                    set.Add("(CC)");
                    break;
                case "2S":
                    set.Add("2S");
                    set.Add("SECOND");
                    set.Add("(2S)");
                    break;
                case "EC":
                    set.Add("EC");
                    set.Add("EXEC");
                    break;
                case "3E":
                    set.Add("3E");
                    set.Add("3 ECON");
                    set.Add("AC 3 ECONOMY");
                    set.Add("(3E)");
                    break;
            }

            return set.ToArray();
        }

        private static bool ElementReferencesClass(IWebElement el, string[] tokens)
        {
            try
            {
                string blob = string.Join(" ",
                    new[]
                    {
                        el.Text ?? "",
                        el.GetDomAttribute("aria-label") ?? "",
                        el.GetDomAttribute("title") ?? "",
                        el.GetDomAttribute("class") ?? "",
                        el.GetDomAttribute("id") ?? "",
                    });
                return tokens.Any(t => t.Length > 0 && blob.Contains(t, StringComparison.OrdinalIgnoreCase));
            }
            catch
            {
                return false;
            }
        }

        private static IWebElement? FindClassAvailabilityElement(IWebElement row, string[] classTokens)
        {
            // Prefer IRCTC class fare *cards* (e.g. "AC 3 Tier (3A)" + Refresh) — not a random td that only mentions "3A".
            var ordered = classTokens
                .Where(t => !string.IsNullOrWhiteSpace(t))
                .OrderByDescending(t => t.StartsWith("(", StringComparison.Ordinal) ? 100 + t.Length : t.Length)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            foreach (string token in ordered)
            {
                if (token.Contains('\'', StringComparison.Ordinal))
                    continue;

                string tLower = token.ToLowerInvariant();
                try
                {
                    var hits = row.FindElements(By.XPath(
                        ".//*[self::div or self::button or self::a][" +
                        "contains(translate(.,'ABCDEFGHIJKLMNOPQRSTUVWXYZ','abcdefghijklmnopqrstuvwxyz'),'" +
                        tLower + "')]"));
                    foreach (var el in hits.OrderBy(e => (e.Text ?? "").Length))
                    {
                        try
                        {
                            if (!el.Displayed)
                                continue;
                            int len = (el.Text ?? "").Length;
                            if (len is < 4 or > 400)
                                continue;
                            if (!ElementReferencesClass(el, classTokens))
                                continue;
                            string tag = el.TagName.ToLowerInvariant();
                            if (tag is "div" or "button" or "a")
                                return el;
                        }
                        catch { /* stale */ }
                    }
                }
                catch { /* ignore */ }
            }

            string[] classSelectors =
            {
                "td.pre-avl",
                "div.pre-avl",
                "td.available-status",
                "td[class*='AVAILABLE']",
                "td[class*='avail']",
                ".booking-button",
                "td button",
                "div[class*='avl']",
                "button[class*='avl']",
            };

            foreach (string sel in classSelectors)
            {
                try
                {
                    foreach (var b in row.FindElements(By.CssSelector(sel)))
                    {
                        try
                        {
                            if (b.Displayed && ElementReferencesClass(b, classTokens))
                                return b;
                        }
                        catch { /* stale */ }
                    }
                }
                catch { /* ignore */ }
            }

            try
            {
                foreach (var b in row.FindElements(By.XPath(
                             ".//*[self::td or self::div or self::button or self::a][contains(@class,'avl') or contains(@class,'AVL') or contains(@class,'class')]")))
                {
                    try
                    {
                        if (b.Displayed && ElementReferencesClass(b, classTokens))
                            return b;
                    }
                    catch { /* stale */ }
                }
            }
            catch { /* ignore */ }

            return null;
        }

        private async Task TryClickAvailabilityRefreshAsync(IWebElement row, CancellationToken ct)
        {
            try
            {
                foreach (var xp in new[]
                         {
                             ".//button[contains(translate(normalize-space(.),'ABCDEFGHIJKLMNOPQRSTUVWXYZ','abcdefghijklmnopqrstuvwxyz'),'refresh')]",
                             ".//*[@role='button' and contains(translate(@aria-label,'ABCDEFGHIJKLMNOPQRSTUVWXYZ','abcdefghijklmnopqrstuvwxyz'),'refresh')]",
                             ".//button[contains(@class,'refresh')]",
                         })
                {
                    foreach (var b in row.FindElements(By.XPath(xp)))
                    {
                        try
                        {
                            if (b.Displayed && b.Enabled)
                            {
                                Logger.Info("BookingEngine: Clicking availability Refresh in train row.");
                                _session.SafeClick(b);
                                await Task.Delay(900, ct);
                                return;
                            }
                        }
                        catch { /* stale */ }
                    }
                }
            }
            catch { /* ignore */ }
        }

        private static async Task WaitForAvailabilityLoadedAsync(IWebElement row, CancellationToken ct)
        {
            // After class + optional Refresh, IRCTC usually shows WL/AVL/RAC etc. before Book Now enables.
            var rx = new Regex(@"\b(WL\s*\d+|RAC\s*\d+|AVL|AVAILABLE|REGRET|CAN\s*CELL|CURR\s*AVL|NOT\s+AVAILABLE)\b",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
            for (int i = 0; i < 24 && !ct.IsCancellationRequested; i++)
            {
                try
                {
                    string t = row.Text ?? "";
                    if (rx.IsMatch(t))
                        return;
                }
                catch
                {
                    /* stale */
                }

                await Task.Delay(450, ct);
            }
        }

        private IWebElement? ReFindTrainRowByNumber(string trainNo)
        {
            try
            {
                var rows = Driver.FindElements(By.CssSelector("app-train-avl-enq"));
                return FindTrainRowForNumber(rows, trainNo);
            }
            catch
            {
                return null;
            }
        }

        private static bool IsBookNowActuallyClickable(IWebElement el)
        {
            try
            {
                if (!el.Displayed || !el.Enabled)
                    return false;
                if (string.Equals(el.GetDomAttribute("aria-disabled"), "true", StringComparison.OrdinalIgnoreCase))
                    return false;
                string cls = el.GetDomAttribute("class") ?? "";
                if (cls.Contains("disabled", StringComparison.OrdinalIgnoreCase) ||
                    cls.Contains("mat-button-disabled", StringComparison.OrdinalIgnoreCase))
                    return false;
                return true;
            }
            catch
            {
                return false;
            }
        }

        private static IWebElement? FindFirstEnabledBookNow(ISearchContext ctx)
        {
            foreach (var xp in new[]
                     {
                         ".//button[contains(translate(normalize-space(.),'ABCDEFGHIJKLMNOPQRSTUVWXYZ','abcdefghijklmnopqrstuvwxyz'),'book now')]",
                         ".//a[contains(translate(normalize-space(.),'ABCDEFGHIJKLMNOPQRSTUVWXYZ','abcdefghijklmnopqrstuvwxyz'),'book now')]",
                     })
            {
                foreach (var el in ctx.FindElements(By.XPath(xp)))
                {
                    if (IsBookNowActuallyClickable(el))
                        return el;
                }
            }

            foreach (var css in new[]
                     {
                         "button.trainBook",
                         "button[class*='booking-cls']",
                         "button[class*='book-btn']",
                         "button.book_now",
                         "a.book_now",
                     })
            {
                foreach (var el in ctx.FindElements(By.CssSelector(css)))
                {
                    if (IsBookNowActuallyClickable(el))
                        return el;
                }
            }

            return null;
        }

        private IWebElement? WaitForEnabledBookNowNearTrain(string trainNo, IWebElement initialRow, int seconds)
        {
            IWebElement? row = initialRow;
            var w = new WebDriverWait(Driver, TimeSpan.FromSeconds(seconds));
            try
            {
                return w.Until(_ =>
                {
                    try
                    {
                        if (row != null)
                        {
                            JS.ExecuteScript("arguments[0].scrollIntoView({block:'center'});", row);
                            var b = FindFirstEnabledBookNow(row);
                            if (b != null)
                                return b;
                        }
                    }
                    catch (StaleElementReferenceException)
                    {
                        row = ReFindTrainRowByNumber(trainNo);
                    }

                    return null;
                });
            }
            catch (WebDriverTimeoutException)
            {
                return null;
            }
        }

        private IWebElement? WaitForBookNowButton(int seconds)
        {
            var w = new WebDriverWait(Driver, TimeSpan.FromSeconds(seconds));
            try
            {
                return w.Until(d =>
                {
                    // Global: visible Book Now that is actually clickable (not greyed / aria-disabled).
                    foreach (var xp in new[]
                             {
                                 "//button[contains(translate(normalize-space(.),'ABCDEFGHIJKLMNOPQRSTUVWXYZ','abcdefghijklmnopqrstuvwxyz'),'book now')]",
                                 "//a[contains(translate(normalize-space(.),'ABCDEFGHIJKLMNOPQRSTUVWXYZ','abcdefghijklmnopqrstuvwxyz'),'book now')]",
                                 "//*[contains(translate(normalize-space(.),'ABCDEFGHIJKLMNOPQRSTUVWXYZ','abcdefghijklmnopqrstuvwxyz'),'book now') and (self::button or self::a)]",
                             })
                    {
                        foreach (var el in d.FindElements(By.XPath(xp)))
                        {
                            try
                            {
                                if (el.Displayed && IsBookNowActuallyClickable(el))
                                    return el;
                            }
                            catch { /* stale */ }
                        }
                    }

                    foreach (var css in new[]
                             {
                                 "button[class*='btnDefault'].booking-cls",
                                 "button.trainBook",
                                 "button[class*='book-btn']",
                                 "button[title*='Book Now'], button[title*='book now']",
                                 "a[class*='book-now'], a[class*='Book-Now']",
                                 "button.book_now",
                                 "a.book_now",
                             })
                    {
                        foreach (var el in d.FindElements(By.CssSelector(css)))
                        {
                            try
                            {
                                if (el.Displayed && IsBookNowActuallyClickable(el))
                                    return el;
                            }
                            catch { /* stale */ }
                        }
                    }

                    return null;
                });
            }
            catch (WebDriverTimeoutException)
            {
                return null;
            }
        }

        // ────────────────────────────────────────────────────────────────
        //  FILL PASSENGERS
        // ────────────────────────────────────────────────────────────────
        private async Task<bool> FillPassengersAsync(CancellationToken ct)
        {
            try
            {
                Logger.Info("BookingEngine: Waiting for passenger details page…");
                Wait(28).Until(d =>
                {
                    string u = d.Url ?? "";
                    if (u.Contains("psgn-details", StringComparison.OrdinalIgnoreCase) ||
                        u.Contains("passenger-input", StringComparison.OrdinalIgnoreCase))
                        return true;
                    return TryFind(d,
                        "app-passenger, app-passenger-input, .passenger-entry, app-passenger-detail, " +
                        "input[formcontrolname*='passengerName'], input[placeholder*='Name']") != null;
                });

                for (int i = 0; i < _config.Passengers.Count; i++)
                {
                    var p   = _config.Passengers[i];
                    // Use nth-of-type / nth-child to target the right passenger block
                    string rowSel  = $".passenger-entry:nth-child({i + 1}), " +
                                     $"app-passenger:nth-of-type({i + 1}) .passenger-entry";

                    IWebElement? pRow = null;
                    try { pRow = Driver.FindElement(By.CssSelector(rowSel)); }
                    catch
                    {
                        // fallback: get all rows and index
                        var all = Driver.FindElements(By.CssSelector(".passenger-entry, app-passenger"));
                        if (i < all.Count) pRow = all[i];
                    }
                    if (pRow == null) continue;

                    TryFill(pRow, "input[placeholder*='Name'], input[formcontrolname*='passengerName']", p.Name);
                    TryFill(pRow, "input[placeholder*='Age']",  p.Age.ToString());
                    try
                    {
                        new SelectHelper(pRow.FindElement(
                            By.CssSelector("select[formcontrolname='passengerGender']")))
                            .SelectByValue(p.Gender);
                    }
                    catch { }
                    if (!string.IsNullOrEmpty(p.BerthPreference) && p.BerthPreference != "NO")
                        try
                        {
                            new SelectHelper(pRow.FindElement(
                                By.CssSelector("select[formcontrolname='berthChoice']")))
                                .SelectByText(p.BerthPreference);
                        }
                        catch { }

                    await Task.Delay(200, ct);
                }

                await Task.Delay(400, ct);
                try { TryFind(Driver, "button[class*='InsuranceNo'], button[id*='noInsurance']")?.Click(); }
                catch { }

                var submitBtn = Wait(SHORT_WAIT).Until(d =>
                {
                    var el = TryFind(d, "button[class*='btnDefault'][type='submit'], button.passenger-continue");
                    return el?.Displayed == true && el.Enabled ? el : null;
                });
                _session.SafeClick(submitBtn!);
                await Task.Delay(2000, ct);
                return true;
            }
            catch (Exception ex)
            {
                Logger.Error(ex, "BookingEngine.FillPassengersAsync");
                _session.TakeScreenshot("passenger_error");
                return false;
            }
        }

        // ────────────────────────────────────────────────────────────────
        //  PAYMENT
        // ────────────────────────────────────────────────────────────────
        private async Task<bool> ProceedToPaymentAsync(CancellationToken ct)
        {
            try
            {
                await Task.Delay(1500, ct);

                // ── Solve review-page captcha if present ──────────────────
                try
                {
                    var img = TryFind(Driver, "app-captcha img, img[src*='captcha']");
                    if (img?.Displayed == true)
                    {
                        string src  = img.GetAttribute("src") ?? "";
                        string b64  = src.StartsWith("data:")
                            ? src[(src.IndexOf(',') + 1)..]
                            : Convert.ToBase64String(
                                await new HttpClient().GetByteArrayAsync(src, ct));
                        string? ans = await new CaptchaSolver(AppSettings.CaptchaApiKey)
                            .SolveImageCaptchaAsync(b64, ct);
                        if (!string.IsNullOrEmpty(ans))
                            ClearAndType(
                                TryFind(Driver, "input[formcontrolname='captcha']")!, ans);
                    }
                }
                catch { }

                // ── Continue to payment ───────────────────────────────────
                var payBtn = Wait(SHORT_WAIT).Until(d =>
                {
                    var el = TryFind(d,
                        "button[class*='btnDefault'][aria-label*='Pay'], " +
                        "button[class*='proceed'], button.continue-btn");
                    return el?.Displayed == true && el.Enabled ? el : null;
                });
                _session.SafeClick(payBtn!);
                await Task.Delay(2500, ct);

                // ── UPI payment ───────────────────────────────────────────
                if (_config.PaymentMethod.Equals("UPI", StringComparison.OrdinalIgnoreCase))
                {
                    try
                    {
                        _session.SafeClick(Wait(SHORT_WAIT).Until(d =>
                            TryFind(d, "[id*='UPI'],[aria-label*='UPI'],label[for*='upi']")));
                        await Task.Delay(800, ct);
                        if (!string.IsNullOrEmpty(_config.UpiId))
                            ClearAndType(
                                TryFind(Driver, "input[placeholder*='UPI'],input[id*='upiId']")!,
                                _config.UpiId);
                        _session.SafeClick(TryFind(Driver, "button[class*='payNow']")!);
                    }
                    catch (Exception ex) { Logger.Error(ex, "UPI payment"); }
                }

                await Task.Delay(6000, ct);

                // ── Capture PNR ───────────────────────────────────────────
                try
                {
                    var pnrEl = TryFind(Driver, ".pnr-no, [class*='pnrNo'], strong");
                    string t  = pnrEl?.Text.Trim() ?? "";
                    if (t.Length >= 10 && t.All(char.IsDigit))
                    { _result.PnrNumber = t; Logger.Info($"BookingEngine: PNR: {t}"); }
                }
                catch { }

                _result.ScreenshotPath = _session.TakeScreenshot("success");
                return true;
            }
            catch (Exception ex)
            {
                Logger.Error(ex, "BookingEngine.ProceedToPaymentAsync");
                _session.TakeScreenshot("payment_error");
                return false;
            }
        }

        // ────────────────────────────────────────────────────────────────
        //  HELPERS
        // ────────────────────────────────────────────────────────────────

        /// <summary>Waits until result rows appear instead of a fixed multi-second sleep.</summary>
        private async Task WaitForTrainListRowsAsync(CancellationToken ct)
        {
            const string rowProbe =
                "app-train-avl-enq, app-train-list, div.train-avl-holder, div.train-list";
            try
            {
                Wait(Math.Min(LONG_WAIT, 25)).Until(d =>
                {
                    try
                    {
                        var rows = d.FindElements(By.CssSelector(rowProbe));
                        foreach (var r in rows)
                        {
                            try
                            {
                                if (r.Displayed)
                                    return true;
                            }
                            catch { /* stale */ }
                        }
                    }
                    catch { /* ignore */ }

                    return false;
                });
            }
            catch
            {
                await Task.Delay(700, ct);
            }
        }

        /// <summary>Extract likely station code / suffix from UI text (e.g. "NEW DELHI - NDLS" → NDLS).</summary>
        private static string ExtractStationMatchKey(string userStationText)
        {
            if (string.IsNullOrWhiteSpace(userStationText))
                return string.Empty;
            string s = userStationText.Trim();
            int hi = s.LastIndexOf('-');
            if (hi >= 0 && hi < s.Length - 1)
            {
                string tail = s[(hi + 1)..].Trim();
                if (tail.Length >= 2)
                    return tail;
            }

            return s;
        }

        private void ClickAutocompleteMatching(string userStationText)
        {
            string needle = ExtractStationMatchKey(userStationText);
            if (needle.Length == 0)
            {
                ClickFirstAutocompleteFallback();
                return;
            }

            try
            {
                Wait(SHORT_WAIT).Until(d =>
                {
                    var list = d.FindElements(By.CssSelector(
                        ".ui-autocomplete-list-item, .p-autocomplete-item, .ng-option, " +
                        "mat-option, .mat-mdc-option, .mdc-list-item"));
                    IWebElement? firstVisible = null;
                    foreach (var item in list)
                    {
                        try
                        {
                            if (!item.Displayed)
                                continue;
                            firstVisible ??= item;
                            string t = item.Text ?? "";
                            if (t.Contains(needle, StringComparison.OrdinalIgnoreCase))
                                return item;
                        }
                        catch
                        {
                            /* stale */
                        }
                    }

                    return firstVisible;
                })?.Click();
            }
            catch
            {
                ClickFirstAutocompleteFallback();
            }
        }

        private void ClickFirstAutocompleteFallback()
        {
            try
            {
                Wait(SHORT_WAIT).Until(d =>
                {
                    var list = d.FindElements(By.CssSelector(
                        ".ui-autocomplete-list-item, .p-autocomplete-item, .ng-option, " +
                        "mat-option, .mat-mdc-option, .mdc-list-item"));
                    foreach (var item in list)
                    {
                        try
                        {
                            if (item.Displayed)
                                return item;
                        }
                        catch
                        {
                            /* stale */
                        }
                    }

                    return null;
                })?.Click();
            }
            catch { }
        }

        private WebDriverWait Wait(int seconds) =>
            new(Driver, TimeSpan.FromSeconds(seconds));

        /// <summary>
        /// IRCTC uses ng-select / p-autocomplete; the first CSS match is often a hidden template input.
        /// We scan all matches and return the first visible field.
        /// </summary>
        private static IWebElement? FindFirstVisibleStationInput(IWebDriver d, bool forOrigin)
        {
            string[] selectors = forOrigin
                ? new[]
                {
                    "ng-select[formcontrolname='origin'] input",
                    "ng-select[formcontrolname='origin'] .ng-input input",
                    "p-autocomplete[formcontrolname='origin'] input",
                    "input[formcontrolname='origin']",
                    "input[formControlName='origin']",
                    "input[formcontrolname='fromStation']",
                    "input[formControlName='fromStation']",
                    "input[placeholder*='From']",
                    "input[placeholder*='from']",
                    "input[aria-label*='From']",
                    "input[aria-label*='from']",
                    "input[id*='origin']",
                    "input[name*='origin']",
                }
                : new[]
                {
                    "ng-select[formcontrolname='destination'] input",
                    "ng-select[formcontrolname='destination'] .ng-input input",
                    "p-autocomplete[formcontrolname='destination'] input",
                    "input[formcontrolname='destination']",
                    "input[formControlName='destination']",
                    "input[formcontrolname='destStation']",
                    "input[formControlName='destStation']",
                    "input[placeholder*='To']",
                    "input[placeholder*='to']",
                    "input[aria-label*='To']",
                    "input[aria-label*='to']",
                    "input[id*='destination']",
                    "input[id*='dest']",
                    "input[name*='destination']",
                };

            return FindFirstVisible(d, selectors, requireEnabled: false);
        }

        private static IWebElement? FindFirstVisibleClickable(IWebDriver d, string[] cssSelectors) =>
            FindFirstVisible(d, cssSelectors, requireEnabled: true);

        private static IWebElement? FindFirstVisible(IWebDriver d, string[] cssSelectors, bool requireEnabled)
        {
            foreach (string css in cssSelectors)
            {
                IReadOnlyList<IWebElement> found;
                try
                {
                    found = d.FindElements(By.CssSelector(css));
                }
                catch
                {
                    continue;
                }

                foreach (var el in found)
                {
                    try
                    {
                        if (el.Displayed && (!requireEnabled || el.Enabled))
                            return el;
                    }
                    catch
                    {
                        /* stale */
                    }
                }
            }

            return null;
        }

        private async Task DismissBlockingOverlaysAsync(CancellationToken ct)
        {
            string[] xpaths =
            {
                "//button[normalize-space()='OK' or normalize-space()='Ok']",
                "//*[contains(translate(normalize-space(.),'ABCDEFGHIJKLMNOPQRSTUVWXYZ','abcdefghijklmnopqrstuvwxyz'),'please select class')]/following::button[1]",
                "//mat-snack-bar-container//button",
                "//button[contains(translate(normalize-space(.),'ABCDEFGHIJKLMNOPQRSTUVWXYZ','abcdefghijklmnopqrstuvwxyz'),'accept')]",
                "//button[contains(translate(normalize-space(.),'ABCDEFGHIJKLMNOPQRSTUVWXYZ','abcdefghijklmnopqrstuvwxyz'),'agree')]",
                "//button[contains(translate(normalize-space(.),'ABCDEFGHIJKLMNOPQRSTUVWXYZ','abcdefghijklmnopqrstuvwxyz'),'continue')]",
                "//a[contains(translate(normalize-space(.),'ABCDEFGHIJKLMNOPQRSTUVWXYZ','abcdefghijklmnopqrstuvwxyz'),'close')]",
            };

            for (int round = 0; round < 4; round++)
            {
                bool anyClick = false;
                foreach (string xp in xpaths)
                {
                    try
                    {
                        foreach (var el in Driver.FindElements(By.XPath(xp)))
                        {
                            try
                            {
                                if (!el.Displayed || !el.Enabled)
                                    continue;
                                _session.SafeClick(el);
                                anyClick = true;
                                await Task.Delay(450, ct);
                                break;
                            }
                            catch
                            {
                                /* ignore */
                            }
                        }
                    }
                    catch
                    {
                        /* ignore */
                    }

                    if (anyClick)
                        break;
                }

                if (!anyClick)
                    break;
            }
        }

        private static IWebElement? TryFind(ISearchContext ctx, string css)
        {
            try
            {
                var el = ctx.FindElement(By.CssSelector(css));
                return el;
            }
            catch { return null; }
        }

        private static void ClearAndType(IWebElement el, string text)
        {
            el.Clear();
            el.SendKeys(text);
        }

        private static void TryFill(IWebElement parent, string css, string value)
        {
            try
            {
                var el = parent.FindElement(By.CssSelector(css));
                el.Clear();
                el.SendKeys(value);
            }
            catch { }
        }

        private BookingResult Fail(string reason)
        {
            _result.Status       = BookingStatus.Failed;
            _result.ErrorMessage = reason;
            _result.CompletedAt  = DateTime.Now;
            Logger.Error($"BookingEngine: FAILED – {reason}");
            OnStatusChanged?.Invoke(_result);
            return _result;
        }

        private void UpdateStatus(BookingStatus s)
        {
            _result.Status = s;
            Logger.Info($"BookingEngine: → {s}");
            OnStatusChanged?.Invoke(_result);
        }
    }
}
