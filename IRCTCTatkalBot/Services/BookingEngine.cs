using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using OpenQA.Selenium;
using OpenQA.Selenium.Support.UI;
using IRCTCTatkalBot.Helpers;
using IRCTCTatkalBot.Models;

namespace IRCTCTatkalBot.Services
{
    /// <summary>
    /// Core automation engine.
    /// Executes the full booking flow for one account/config pair:
    ///   Login → Search → Select Train → Fill Passengers → Payment.
    /// When <paramref name="skipInitialLogin"/> is true, login is omitted (scheduled mode after pre-login).
    /// </summary>
    public class BookingEngine
    {
        private readonly SessionManager _session;
        private readonly BookingConfig _config;
        private readonly BookingResult _result;
        private IWebDriver Driver => _session.Driver;
        private WebDriverWait Wait => new(Driver, TimeSpan.FromSeconds(20));
        private bool _loginSucceededOnce;

        public event Action<BookingResult>? OnStatusChanged;

        public BookingEngine(SessionManager session, BookingConfig config)
        {
            _session = session;
            _config = config;
            _result = new BookingResult { AccountId = _config.AccountId };
        }

        // ── Entry Point ───────────────────────────────────────────────

        public async Task<BookingResult> RunAsync(CancellationToken ct = default, bool skipInitialLogin = false)
        {
            var phase = Stopwatch.StartNew();
            try
            {
                // Step 1 – Login (skipped when scheduler already authenticated this session)
                // Also skip on retries once we have a confirmed logged-in session for this engine instance.
                if (skipInitialLogin || (_loginSucceededOnce && _session.IsLoggedIn()))
                {
                    Logger.Info("BookingEngine: Login skipped — using session from Tatkal pre-login phase.");
                    _result.LoginPhaseSeconds = 0;
                }
                else
                {
                    UpdateStatus(BookingStatus.LoggingIn);
                    phase.Restart();
                    bool loggedIn = await _session.LoginAsync(ct);
                    _result.LoginPhaseSeconds = phase.Elapsed.TotalSeconds;
                    if (!loggedIn) return Fail("Login failed");
                    _loginSucceededOnce = true;
                }

                // Step 2 – Search (skip on orchestrator retry if we're still on train-list with a valid session)
                bool onTrainList = SafeGetUrl().Contains("train-list", StringComparison.OrdinalIgnoreCase);
                bool skipSearch = onTrainList && _loginSucceededOnce && _session.IsLoggedIn();

                if (skipSearch)
                {
                    Logger.Info("BookingEngine: Still on train-list with active session — skipping duplicate search, continuing from results.");
                    _result.SearchPhaseSeconds = 0;
                }
                else
                {
                    UpdateStatus(BookingStatus.Searching);
                    phase.Restart();
                    bool searched = await SearchTrainAsync(ct);
                    _result.SearchPhaseSeconds = phase.Elapsed.TotalSeconds;
                    if (!searched) return Fail(_result.ErrorMessage ?? "Train search failed");
                }

                // Step 3 – Select Train & Class
                UpdateStatus(BookingStatus.SelectingTrain);
                phase.Restart();
                bool selected = await SelectTrainAndClassAsync(ct);
                _result.SelectTrainPhaseSeconds = phase.Elapsed.TotalSeconds;
                if (!selected) return Fail(_result.ErrorMessage ?? "Could not select train/class");

                // Step 4 – Fill Passengers
                UpdateStatus(BookingStatus.FillingPassengers);
                phase.Restart();
                bool filled = await FillPassengersAsync(ct);
                _result.FillPassengersPhaseSeconds = phase.Elapsed.TotalSeconds;
                if (!filled) return Fail("Failed to fill passenger details");

                // Step 5 – Payment
                UpdateStatus(BookingStatus.ProcessingPayment);
                phase.Restart();
                bool paid = await ProceedToPaymentAsync(ct);
                _result.PaymentPhaseSeconds = phase.Elapsed.TotalSeconds;
                if (!paid) return Fail("Payment step failed");

                // Done
                _result.Status = BookingStatus.Completed;
                _result.CompletedAt = DateTime.Now;
                Logger.Info($"BookingEngine: ✓ Booking completed in {_result.ElapsedSeconds:F1}s | {_result.PhaseTimingSummary}");
            }
            catch (OperationCanceledException)
            {
                _result.Status = BookingStatus.Cancelled;
                _result.ErrorMessage = "Cancelled by user.";
                Logger.Warning("BookingEngine: Booking cancelled.");
            }
            catch (Exception ex)
            {
                Logger.Error(ex, "BookingEngine.RunAsync");
                _result.ScreenshotPath = _session.TakeScreenshot("error");
                return Fail($"Unexpected error: {ex.Message}");
            }
            finally
            {
                OnStatusChanged?.Invoke(_result);
            }

            return _result;
        }

        // ── Step 2: Search ────────────────────────────────────────────

        private async Task<bool> SearchTrainAsync(CancellationToken ct)
        {
            Logger.Info($"BookingEngine: Searching {_config.FromStation} → {_config.ToStation}");

            try
            {
                // After login, IRCTC may redirect to a dashboard variant.
                // Always go back to train-search so the From/To/Date inputs exist reliably.
                Driver.Navigate().GoToUrl("https://www.irctc.co.in/nget/train-search");
                await Task.Delay(400, ct);

                // Fill "From" station
                IWebElement fromInput;
                try
                {
                    fromInput = WaitForFirstVisible(TimeSpan.FromSeconds(35),
                        By.CssSelector("input[placeholder*='From']"),
                        By.CssSelector("input[aria-label*='From']"),
                        By.CssSelector("input[formcontrolname*='from' i]"),
                        By.CssSelector("input[id*='origin' i], input[name*='origin' i]"),
                        By.XPath("//input[contains(translate(@placeholder,'ABCDEFGHIJKLMNOPQRSTUVWXYZ','abcdefghijklmnopqrstuvwxyz'),'from')]"),
                        By.XPath("//input[contains(translate(@aria-label,'ABCDEFGHIJKLMNOPQRSTUVWXYZ','abcdefghijklmnopqrstuvwxyz'),'from')]")
                    );
                }
                catch (WebDriverTimeoutException)
                {
                    _session.TakeScreenshot("search_from_input_timeout");
                    _result.ErrorMessage = $"Search failed: Could not find 'From' station input field. URL: {SafeGetUrl()}";
                    Logger.Error("BookingEngine.SearchTrainAsync: From input field not found within 35 seconds.");
                    return false;
                }

                fromInput.Clear();
                fromInput.SendKeys(_config.FromStation);
                await Task.Delay(400, ct);
                // Select first autocomplete suggestion
                ClickFirstAutocompleteSuggestion(ct);

                // Fill "To" station
                var toInput = WaitForFirstVisible(TimeSpan.FromSeconds(20),
                    By.CssSelector("input[placeholder*='To']"),
                    By.CssSelector("input[aria-label*='To']"),
                    By.CssSelector("input[formcontrolname*='to' i]"),
                    By.CssSelector("input[id*='destination' i], input[name*='destination' i]"),
                    By.XPath("//input[contains(translate(@placeholder,'ABCDEFGHIJKLMNOPQRSTUVWXYZ','abcdefghijklmnopqrstuvwxyz'),'to')]"),
                    By.XPath("//input[contains(translate(@aria-label,'ABCDEFGHIJKLMNOPQRSTUVWXYZ','abcdefghijklmnopqrstuvwxyz'),'to')]")
                );
                toInput.Clear();
                toInput.SendKeys(_config.ToStation);
                await Task.Delay(400, ct);
                ClickFirstAutocompleteSuggestion(ct);

                // Set Journey Date — IRCTC uses PrimeNG p-calendar (dateformat dd/mm/yy). Plain SendKeys often
                // does not update the Angular form control → empty field, "Invalid journey date", or wrong day.
                var dateInput = WaitForFirstDisplayed(TimeSpan.FromSeconds(20),
                    By.CssSelector("p-calendar#jDate input"),
                    By.CssSelector("p-calendar[formcontrolname='journeyDate'] input"),
                    By.CssSelector("p-calendar input"),
                    By.CssSelector("input[aria-label*='Journey Date' i]"),
                    By.CssSelector("input[placeholder*='DD/MM/YYYY'], input[placeholder*='dd/mm/yyyy']"),
                    By.CssSelector("input[aria-label*='Date'], input[aria-label*='Journey']"));

                bool dateOk = await TryCommitJourneyDateAsync(dateInput, _config.JourneyDate, ct);
                if (!dateOk)
                    Logger.Warning("BookingEngine: Journey date may not have bound to the calendar — IRCTC may reject the search.");

                // Journey class — PrimeNG p-dropdown #journeyClass (not a native <select>). Without this, search stays "All Classes".
                try
                {
                    string cls = (_config.TrainClass ?? "").Trim();
                    if (cls.Length > 0)
                    {
                        bool classSet = await SelectPrimeNgDropdownContainsAsync(ct, cls,
                            By.CssSelector("#journeyClass .ui-dropdown"),
                            By.CssSelector("p-dropdown#journeyClass .ui-dropdown"),
                            By.CssSelector("p-dropdown[formcontrolname='journeyClass'] .ui-dropdown"),
                            By.CssSelector("[formcontrolname='journeyClass'] .ui-dropdown"));

                        if (classSet)
                            Logger.Info($"BookingEngine: Journey class dropdown updated (match contains '{cls}').");
                        else
                            Logger.Warning($"BookingEngine: Could not pick class '{cls}' in journeyClass dropdown (option text may differ on site).");
                    }
                }
                catch (Exception ex)
                {
                    Logger.Warning($"BookingEngine: Journey class dropdown error: {ex.Message}");
                }

                // Quota — native <select> or PrimeNG #journeyQuota
                try
                {
                    IWebElement? quotaSelect = null;
                    try
                    {
                        quotaSelect = WaitForFirstVisible(TimeSpan.FromSeconds(5),
                            By.CssSelector("select[aria-label='Quota']"),
                            By.CssSelector("select[formcontrolname*='quota' i]"),
                            By.XPath("//select[contains(translate(@aria-label,'ABCDEFGHIJKLMNOPQRSTUVWXYZ','abcdefghijklmnopqrstuvwxyz'),'quota')]"));
                    }
                    catch
                    {
                        quotaSelect = null;
                    }

                    if (quotaSelect != null &&
                        quotaSelect.TagName.Equals("select", StringComparison.OrdinalIgnoreCase))
                    {
                        var quotaDropdown = new SelectHelper(quotaSelect);
                        quotaDropdown.SelectByText(_config.Quota);
                    }
                    else
                    {
                        string desired = string.IsNullOrWhiteSpace(_config.Quota) ? "TATKAL" : _config.Quota.Trim();
                        bool quotaSet = await SelectPrimeNgDropdownContainsAsync(ct, desired,
                            By.CssSelector("#journeyQuota .ui-dropdown"),
                            By.CssSelector("p-dropdown#journeyQuota .ui-dropdown"),
                            By.CssSelector("p-dropdown[formcontrolname='journeyQuota'] .ui-dropdown"),
                            By.CssSelector("[formcontrolname='journeyQuota'] .ui-dropdown"));

                        if (quotaSet)
                            Logger.Info($"BookingEngine: Quota dropdown updated (match contains '{desired}').");
                        else
                            Logger.Warning("BookingEngine: Quota dropdown did not accept selection — check TATKAL option label on site.");
                    }
                }
                catch (Exception ex)
                {
                    Logger.Warning($"BookingEngine: Quota selector failed ({ex.Message}); continuing with site default quota.");
                }

                await TryDismissDatepickerOverlayAsync(ct);

                // Click Search button
                var searchBtn = WaitForFirstVisible(TimeSpan.FromSeconds(20),
                    By.CssSelector("button.search_btn, button[class*='train_Search']"),
                    By.XPath("//button[contains(translate(normalize-space(.),'ABCDEFGHIJKLMNOPQRSTUVWXYZ','abcdefghijklmnopqrstuvwxyz'),'search')]")
                );
                _session.SafeClick(searchBtn);

                // IRCTC shows toasts like "Invalid journey date" without changing URL — detect before assuming success.
                string? siteErr = null;
                var searchWaitDeadline = DateTime.UtcNow.AddSeconds(22);
                while (DateTime.UtcNow < searchWaitDeadline)
                {
                    await Task.Delay(400, ct);
                    siteErr = FindVisibleTrainSearchError(Driver);
                    if (siteErr != null)
                        break;

                    try
                    {
                        if ((Driver.Url ?? "").Contains("/nget/booking/train-list", StringComparison.OrdinalIgnoreCase))
                            break;
                    }
                    catch
                    {
                        /* ignore */
                    }
                }

                if (siteErr != null)
                {
                    Logger.Error($"BookingEngine: IRCTC rejected search: {siteErr}");
                    _session.TakeScreenshot("search_site_error");
                    _result.ErrorMessage = $"Train search failed: {siteErr}";
                    return false;
                }

                try
                {
                    var navWait = new WebDriverWait(Driver, TimeSpan.FromSeconds(20));
                    navWait.Until(d => (d.Url ?? "").Contains("/nget/booking/train-list", StringComparison.OrdinalIgnoreCase));
                }
                catch
                {
                    Logger.Warning($"BookingEngine: Search did not reach train-list. url={SafeGetUrl()} title={SafeGetTitle()}");
                    _session.TakeScreenshot("search_no_train_list");
                    if (!(SafeGetUrl()).Contains("train-list", StringComparison.OrdinalIgnoreCase))
                    {
                        _result.ErrorMessage =
                            "Train search did not open results page. Check journey date, quota (TATKAL), and stations in the browser.";
                        return false;
                    }
                }

                await Task.Delay(350, ct);
                Logger.Info($"BookingEngine: Search submitted successfully. url={SafeGetUrl()}");
                return true;
            }
            catch (WebDriverTimeoutException wdEx)
            {
                Logger.Error(wdEx, "BookingEngine.SearchTrainAsync - Timeout");
                _session.TakeScreenshot("search_timeout");
                _result.ErrorMessage =
                    $"Search timeout: {wdEx.Message}. URL: {SafeGetUrl()}, Title: {SafeGetTitle()}";
                return false;
            }
            catch (Exception ex)
            {
                Logger.Error(ex, "BookingEngine.SearchTrainAsync");
                _session.TakeScreenshot("search_error");
                _result.ErrorMessage =
                    "Train search failed: " + DriverDiagnostics.FormatShort(ex) +
                    $" | url={SafeGetUrl()} title={SafeGetTitle()}";
                return false;
            }
        }

        private string SafeGetUrl()
        {
            try { return Driver.Url ?? ""; } catch { return ""; }
        }

        private string SafeGetTitle()
        {
            try { return Driver.Title ?? ""; } catch { return ""; }
        }

        /// <summary>Visible elements only (no Enabled check). PrimeNG widgets often fail <c>Enabled</c> checks.</summary>
        private IWebElement WaitForFirstDisplayed(TimeSpan timeout, params By[] locators)
        {
            var wait = new WebDriverWait(Driver, timeout);
            return wait.Until(d =>
            {
                foreach (var by in locators)
                {
                    try
                    {
                        foreach (var el in d.FindElements(by))
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
                    }
                    catch
                    {
                        /* ignore */
                    }
                }

                return null;
            }) ?? throw new WebDriverTimeoutException("Could not locate a displayed element.");
        }

        private static string? FindVisibleTrainSearchError(IWebDriver driver)
        {
            foreach (var by in new[]
                     {
                         By.CssSelector(".ui-toast-message, .ui-toast-detail, .p-toast-message, .p-toast-detail"),
                         By.CssSelector(".alert-danger, .alert-warning, [class*='error']"),
                         By.XPath("//*[contains(translate(normalize-space(.),'ABCDEFGHIJKLMNOPQRSTUVWXYZ','abcdefghijklmnopqrstuvwxyz'),'invalid journey date')]"),
                         By.XPath("//*[contains(translate(normalize-space(.),'ABCDEFGHIJKLMNOPQRSTUVWXYZ','abcdefghijklmnopqrstuvwxyz'),'invalid') and contains(translate(normalize-space(.),'ABCDEFGHIJKLMNOPQRSTUVWXYZ','abcdefghijklmnopqrstuvwxyz'),'date')]")
                     })
            {
                try
                {
                    foreach (var el in driver.FindElements(by))
                    {
                        try
                        {
                            if (!el.Displayed)
                                continue;
                            var t = (el.Text ?? "").Trim();
                            if (t.Length == 0)
                                continue;
                            if (t.Contains("journey date", StringComparison.OrdinalIgnoreCase) &&
                                (t.Contains("invalid", StringComparison.OrdinalIgnoreCase) ||
                                 t.Contains("error", StringComparison.OrdinalIgnoreCase)))
                                return t;
                            if (t.Contains("invalid journey", StringComparison.OrdinalIgnoreCase))
                                return t;
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
            }

            return null;
        }

        private async Task<bool> TryCommitJourneyDateAsync(IWebElement dateInput, DateTime journeyDate, CancellationToken ct)
        {
            // p-calendar dateformat is often dd/mm/yy — try 2-digit year first, then 4-digit.
            string[] attempts = { journeyDate.ToString("dd/MM/yy"), journeyDate.ToString("dd/MM/yyyy") };
            var js = (IJavaScriptExecutor)Driver;
            string day = journeyDate.Day.ToString("00");
            string month = journeyDate.Month.ToString("00");

            foreach (var dateText in attempts)
            {
                try
                {
                    dateInput.Click();
                    await Task.Delay(120, ct);
                    dateInput.SendKeys(Keys.Control + "a");
                    dateInput.SendKeys(Keys.Delete);
                    await Task.Delay(80, ct);
                    dateInput.SendKeys(dateText);
                    dateInput.SendKeys(Keys.Tab);
                    await Task.Delay(350, ct);

                    if (CalendarInputShowsDayMonth(dateInput, day, month))
                        return true;
                }
                catch
                {
                    /* try JS path */
                }

                try
                {
                    js.ExecuteScript(@"
                        const el = arguments[0], val = arguments[1];
                        if (!el) return;
                        el.focus();
                        el.removeAttribute('readonly');
                        el.value = val;
                        try {
                            el.dispatchEvent(new InputEvent('input', { bubbles: true, inputType: 'insertFromPaste', data: val }));
                        } catch (e) {
                            el.dispatchEvent(new Event('input', { bubbles: true }));
                        }
                        el.dispatchEvent(new Event('change', { bubbles: true }));
                        el.blur();
                    ", dateInput, dateText);
                    await Task.Delay(400, ct);

                    if (CalendarInputShowsDayMonth(dateInput, day, month))
                        return true;
                }
                catch
                {
                    /* next attempt */
                }
            }

            // Last resort: open PrimeNG datepicker and pick the day (month navigation not handled — works when default month matches).
            if (await TryPickJourneyDateViaCalendarPopupAsync(journeyDate, ct))
            {
                try
                {
                    if (CalendarInputShowsDayMonth(dateInput, day, month))
                        return true;
                }
                catch
                {
                    /* ignore */
                }
            }

            return false;
        }

        private static bool CalendarInputShowsDayMonth(IWebElement dateInput, string day, string month)
        {
            string v = "";
            try
            {
                v = (dateInput.GetDomProperty("value") ?? dateInput.GetAttribute("value") ?? "").Trim();
            }
            catch
            {
                try { v = (dateInput.GetAttribute("value") ?? "").Trim(); } catch { /* ignore */ }
            }

            return v.Length > 0 && v.Contains(day, StringComparison.Ordinal) && v.Contains(month, StringComparison.Ordinal);
        }

        private async Task<bool> TryPickJourneyDateViaCalendarPopupAsync(DateTime journeyDate, CancellationToken ct)
        {
            try
            {
                bool calendarOpened = false;
                foreach (var triggerBy in new[]
                         {
                             By.CssSelector("p-calendar#jDate .ui-datepicker-trigger"),
                             By.CssSelector("p-calendar#jDate button"),
                             By.CssSelector("p-calendar[formcontrolname='journeyDate'] .ui-datepicker-trigger"),
                             By.CssSelector("p-calendar[formcontrolname='journeyDate'] button"),
                             By.CssSelector("p-calendar .ui-button-icon-only"),
                         })
                {
                    foreach (var btn in Driver.FindElements(triggerBy))
                    {
                        try
                        {
                            if (!btn.Displayed)
                                continue;
                            _session.SafeClick(btn);
                            await Task.Delay(450, ct);
                            calendarOpened = true;
                            break;
                        }
                        catch
                        {
                            /* next */
                        }
                    }

                    if (calendarOpened)
                        break;
                }

                if (!calendarOpened)
                    return false;

                string dayStr = journeyDate.Day.ToString();
                var dayWait = new WebDriverWait(Driver, TimeSpan.FromSeconds(8));
                var dayCell = dayWait.Until(d =>
                {
                    foreach (var by in new[]
                             {
                                 By.CssSelector(".ui-datepicker-calendar td a:not(.ui-state-disabled)"),
                                 By.CssSelector(".p-datepicker-calendar td span:not(.p-disabled)"),
                                 By.CssSelector(".p-datepicker td span:not(.p-disabled)"),
                             })
                    {
                        foreach (var cell in d.FindElements(by))
                        {
                            try
                            {
                                if (!cell.Displayed)
                                    continue;
                                if (string.Equals((cell.Text ?? "").Trim(), dayStr, StringComparison.Ordinal))
                                    return cell;
                            }
                            catch
                            {
                                /* ignore */
                            }
                        }
                    }

                    return null;
                });

                _session.SafeClick(dayCell!);
                await Task.Delay(350, ct);
                return true;
            }
            catch
            {
                return false;
            }
        }

        private async Task<bool> SelectPrimeNgDropdownContainsAsync(CancellationToken ct, string optionSubstring, params By[] dropRootLocators)
        {
            string desired = (optionSubstring ?? "").Trim();
            if (desired.Length == 0)
                return false;

            var dropRoot = WaitForFirstDisplayed(TimeSpan.FromSeconds(12), dropRootLocators);
            var trigger = FindFirstClickableIn(dropRoot,
                          By.CssSelector(".ui-dropdown-trigger"),
                          By.CssSelector(".ui-dropdown-label-container"),
                          By.CssSelector(".ui-dropdown"))
                      ?? dropRoot;
            _session.SafeClick(trigger);
            await Task.Delay(180, ct);

            try
            {
                var optWait = new WebDriverWait(Driver, TimeSpan.FromSeconds(12))
                    { PollingInterval = TimeSpan.FromMilliseconds(100) };
                var option = optWait.Until(d =>
                {
                    foreach (var li in d.FindElements(By.CssSelector(
                                 "li[role='option'], .ui-dropdown-items li, .ui-dropdown-item, .p-dropdown-items li, .p-dropdown-item, li.ui-menuitem")))
                    {
                        try
                        {
                            if (!li.Displayed)
                                continue;
                            var t = (li.Text ?? "").Trim();
                            if (t.Length > 0 && t.Contains(desired, StringComparison.OrdinalIgnoreCase))
                                return li;
                        }
                        catch
                        {
                            /* ignore */
                        }
                    }

                    return null;
                });

                _session.SafeClick(option!);
                await Task.Delay(140, ct);
                return true;
            }
            catch (WebDriverTimeoutException)
            {
                return false;
            }
        }

        private IWebElement WaitForFirstVisible(TimeSpan timeout, params By[] locators)
        {
            var wait = new WebDriverWait(Driver, timeout);
            return wait.Until(d =>
            {
                foreach (var by in locators)
                {
                    try
                    {
                        foreach (var el in d.FindElements(by))
                        {
                            try
                            {
                                if (el.Displayed && el.Enabled) return el;
                            }
                            catch { /* stale */ }
                        }
                    }
                    catch { /* ignore */ }
                }
                return null;
            }) ?? throw new WebDriverTimeoutException("Could not locate a visible input on train-search.");
        }

        private void ClickFirstAutocompleteSuggestion(CancellationToken ct)
        {
            // IRCTC has changed autocomplete markup multiple times; try common patterns.
            var wait = new WebDriverWait(Driver, TimeSpan.FromSeconds(20));
            var el = wait.Until(d =>
            {
                foreach (var by in new[]
                         {
                             By.CssSelector(".ui-autocomplete-list-item"),
                             By.CssSelector(".ui-autocomplete-items li"),
                             By.CssSelector("li.ui-autocomplete-list-item"),
                             By.CssSelector("ul[role='listbox'] li"),
                             By.XPath("//li[@role='option']")
                         })
                {
                    try
                    {
                        foreach (var item in d.FindElements(by))
                        {
                            try
                            {
                                if (item.Displayed) return item;
                            }
                            catch { /* stale */ }
                        }
                    }
                    catch { /* ignore */ }
                }
                return null;
            });

            if (el != null)
            {
                _session.SafeClick(el);
                try { Task.Delay(250, ct).Wait(ct); } catch { /* ignore */ }
            }
        }

        // ── Step 3: Select Train ──────────────────────────────────────

        private async Task<bool> SelectTrainAndClassAsync(CancellationToken ct)
        {
            Logger.Info($"BookingEngine: Looking for train {_config.TrainNumber} class {_config.TrainClass}");

            try
            {
                await Task.Delay(450, ct);
                TryDismissBlockingOverlays();

                var rows = ResolveTrainRowRoots(TimeSpan.FromSeconds(35));
                if (rows.Count == 0)
                {
                    rows = WaitUntilAnyVisibleElements(TimeSpan.FromSeconds(28),
                        By.CssSelector("app-train-avl-enq"),
                        By.CssSelector(".train-info-block"),
                        By.CssSelector("div.train-heading"),
                        By.CssSelector("[class*='train-info']"),
                        By.CssSelector(".trainList, .train-list, .trainlist"),
                        By.CssSelector("[class*='trainList']"),
                        By.CssSelector("[class*='train-list']"),
                        By.CssSelector("div[class*='train'][class*='white']"),
                        By.CssSelector("mat-card[class*='train']"),
                        By.CssSelector("app-train-list .white-back"),
                        By.CssSelector(".panel.train"),
                        By.CssSelector("div[class*='Train'] div[class*='heading']")).ToList();
                }

                if (rows.Count == 0)
                {
                    Logger.Error("BookingEngine: No visible train rows on train-list (DOM changed or no trains / loading).");
                    _session.TakeScreenshot("no_trains");
                    _result.ErrorMessage =
                        "Could not select train/class: No train rows visible on results page. Check date, route, Tatkal opening time, or inspect Screenshots/no_trains_*.png.";
                    return false;
                }

                Logger.Info($"BookingEngine: Found {rows.Count} train row(s).");

                IWebElement? card = PickTrainCard(rows, _config.TrainNumber);
                if (card == null)
                {
                    Logger.Error("BookingEngine: No trains matched selection.");
                    _session.TakeScreenshot("no_trains");
                    _result.ErrorMessage = "Could not select train/class: No trains found in results.";
                    return false;
                }

                string cls = (_config.TrainClass ?? "").Trim();
                if (cls.Length == 0) cls = "SL";

                await TryClickRefreshForClassAsync(card, cls, ct);

                // Re-query rows after availability refresh — non-fatal if DOM is mid-update; keep prior card.
                rows = ResolveTrainRowRoots(TimeSpan.FromSeconds(8));
                if (rows.Count == 0)
                {
                    rows = WaitUntilAnyVisibleElements(TimeSpan.FromSeconds(8),
                        By.CssSelector("app-train-avl-enq"),
                        By.CssSelector(".train-info-block"),
                        By.CssSelector("[class*='train-info']")).ToList();
                }

                if (rows.Count > 0)
                {
                    var refreshedCard = PickTrainCard(rows, _config.TrainNumber);
                    if (refreshedCard != null)
                        card = refreshedCard;
                }
                else
                {
                    Logger.Info("BookingEngine: Train row re-query empty after refresh — continuing with original row element.");
                }

                IWebElement? classEl = FindClassAvailabilityTarget(card, cls);
                if (classEl == null)
                {
                    Logger.Error($"BookingEngine: Class '{cls}' not found/clickable in train card.");
                    _session.TakeScreenshot("no_class");
                    _result.ErrorMessage = $"Could not select train/class: Class '{cls}' not found/clickable.";
                    return false;
                }

                Logger.Info($"BookingEngine: Clicking class slot for '{cls}' (matched IRCTC label).");
                RobustClickElement(classEl);
                await Task.Delay(450, ct);
                TryDismissBlockingOverlays();

                await PollUntilBookNowEnabledAsync(card, ct, TimeSpan.FromSeconds(35));

                var bookNowBtn =
                    FindBookNowButtonInCard(card)
                    ?? FindFirstClickableIn(card,
                        By.XPath(".//button[contains(translate(normalize-space(.),'ABCDEFGHIJKLMNOPQRSTUVWXYZ','abcdefghijklmnopqrstuvwxyz'),'book')]"),
                        By.CssSelector("button.trainBook, button[class*='btnDefault'], button[class*='btn-primary']"))
                    ?? FindFirstClickableGlobal(
                        By.XPath("//button[contains(translate(normalize-space(.),'ABCDEFGHIJKLMNOPQRSTUVWXYZ','abcdefghijklmnopqrstuvwxyz'),'book')]"),
                        By.CssSelector("button.trainBook, button[class*='btnDefault'], button[class*='btn-primary']"));

                if (bookNowBtn == null)
                    throw new WebDriverTimeoutException("Book Now button not found/clickable after selecting class.");

                int handlesBefore = Driver.WindowHandles.Count;
                _session.SafeClick(bookNowBtn);
                await Task.Delay(600, ct);

                // IRCTC sometimes opens passenger flow in a new tab/window.
                try
                {
                    if (Driver.WindowHandles.Count > handlesBefore)
                        Driver.SwitchTo().Window(Driver.WindowHandles[^1]);
                }
                catch (Exception ex)
                {
                    Logger.Warning($"BookingEngine: Window switch after Book Now: {ex.Message}");
                }

                TryDismissIrctcConfirmIfPresent();

                bool navigated = await WaitForPassengerOrReviewPageAsync(ct, TimeSpan.FromSeconds(55));
                if (!navigated)
                {
                    throw new WebDriverTimeoutException(
                        "Timed out waiting for passenger/review page after Book Now (new window, slow load, or extra dialog).");
                }

                Logger.Info("BookingEngine: Train and class selected.");
                return true;
            }
            catch (Exception ex)
            {
                Logger.Error(ex, "BookingEngine.SelectTrainAndClassAsync");
                _session.TakeScreenshot("select_error");
                _result.ErrorMessage =
                    "Could not select train/class: " + DriverDiagnostics.FormatShort(ex) +
                    $" | url={SafeGetUrl()} title={SafeGetTitle()}";
                return false;
            }
        }

        /// <summary>
        /// Never throws on timeout — returns empty list so callers can fall back (e.g. keep prior train card after Refresh).
        /// </summary>
        private IReadOnlyList<IWebElement> WaitUntilAnyVisibleElements(TimeSpan timeout, params By[] locators)
        {
            try
            {
                var wait = new WebDriverWait(Driver, timeout) { PollingInterval = TimeSpan.FromMilliseconds(150) };
                var els = wait.Until(d =>
                {
                    foreach (var by in locators)
                    {
                        try
                        {
                            var visible = d.FindElements(by).Where(e =>
                            {
                                try
                                {
                                    return e.Displayed && e.Size.Height > 2 && e.Size.Width > 2;
                                }
                                catch
                                {
                                    return false;
                                }
                            }).ToList();

                            if (visible.Count > 0)
                                return visible;
                        }
                        catch
                        {
                            /* ignore */
                        }
                    }

                    return null;
                });

                return els ?? new List<IWebElement>();
            }
            catch (WebDriverTimeoutException)
            {
                return new List<IWebElement>();
            }
        }

        private async Task TryDismissDatepickerOverlayAsync(CancellationToken ct)
        {
            try
            {
                var body = Driver.FindElement(By.TagName("body"));
                body.SendKeys(Keys.Escape);
                await Task.Delay(200, ct);
                body.SendKeys(Keys.Escape);
                await Task.Delay(150, ct);
            }
            catch
            {
                /* ignore */
            }
        }

        /// <summary>
        /// IRCTC train-list: each class column shows "Refresh" until availability loads — Book Now stays disabled until then.
        /// </summary>
        private async Task<bool> TryClickRefreshNearClassInCardAsync(IWebElement card, string classCode, CancellationToken ct)
        {
            string code = classCode.Trim();
            if (code.Length == 0)
                return false;

            try
            {
                foreach (var el in card.FindElements(By.XPath(
                             ".//*[contains(translate(normalize-space(.),'ABCDEFGHIJKLMNOPQRSTUVWXYZ','abcdefghijklmnopqrstuvwxyz'),'refresh')]")))
                {
                    try
                    {
                        if (!el.Displayed)
                            continue;
                        if (!RefreshElementBelongsToClassColumn(el, code))
                            continue;

                        _session.SafeClick(el);
                        Logger.Info($"BookingEngine: Clicked availability Refresh for class '{code}'.");
                        await Task.Delay(1100, ct);
                        return true;
                    }
                    catch
                    {
                        /* next */
                    }
                }
            }
            catch
            {
                /* ignore */
            }

            return false;
        }

        private async Task TryClickRefreshForClassAsync(IWebElement card, string classCode, CancellationToken ct)
        {
            if (await TryClickRefreshNearClassInCardAsync(card, classCode, ct))
                return;

            try
            {
                foreach (var el in card.FindElements(By.CssSelector(
                             "i[class*='refresh'], i[class*='repeat'], a[class*='refresh'], .fa-refresh, .fa-repeat, [title*='Refresh'], [aria-label*='Refresh']")))
                {
                    try
                    {
                        if (!el.Displayed)
                            continue;
                        if (!RefreshElementBelongsToClassColumn(el, classCode))
                            continue;

                        _session.SafeClick(el);
                        Logger.Info($"BookingEngine: Clicked icon Refresh for class '{classCode}'.");
                        await Task.Delay(1100, ct);
                        return;
                    }
                    catch
                    {
                        /* next */
                    }
                }
            }
            catch
            {
                /* ignore */
            }
        }

        private IReadOnlyList<IWebElement> ResolveTrainRowRoots(TimeSpan timeout)
        {
            try
            {
                var wait = new WebDriverWait(Driver, timeout) { PollingInterval = TimeSpan.FromMilliseconds(120) };
                var found = wait.Until(driver =>
                {
                    var list = new List<IWebElement>();
                    foreach (var el in driver.FindElements(By.CssSelector("app-train-avl-enq")))
                    {
                        try
                        {
                            if (el.Displayed && el.Size.Height >= 28 && el.Size.Width >= 80)
                                list.Add(el);
                        }
                        catch
                        {
                            /* ignore */
                        }
                    }

                    return list.Count > 0 ? list : null;
                });

                return found?.ToList() ?? new List<IWebElement>();
            }
            catch (WebDriverTimeoutException)
            {
                return new List<IWebElement>();
            }
        }

        private static IReadOnlyList<string> GetClassTextNeedles(string trainClass)
        {
            string c = (trainClass ?? "").Trim().ToUpperInvariant();
            return c switch
            {
                "3A" => new List<string> { "(3a)", "3a)", "ac 3 tier", "3 tier", "third ac", "ac 3", " 3a ", "a/c 3 tier" },
                "2A" => new List<string> { "(2a)", "ac 2 tier", "2 tier", "second ac", "ac 2", " 2a " },
                "1A" => new List<string> { "(1a)", "first ac", "ac first", "1 tier", " 1a " },
                "SL" => new List<string> { "(sl)", "sleeper", "sleeper (sl)", " sleeper " },
                "CC" => new List<string>
                {
                    "(cc)", "chair car", "ac chair", "executive chair", "exec chair", "chair", "compartment",
                    "executive"
                },
                "2S" => new List<string> { "(2s)", "second seating", " 2s " },
                _ => new List<string> { c.ToLowerInvariant(), $"({c.ToLowerInvariant()})" },
            };
        }

        private IWebElement? FindClassAvailabilityTarget(IWebElement row, string trainClass)
        {
            string code = (trainClass ?? "").Trim();
            if (code.Length == 0) code = "SL";

            var needles = GetClassTextNeedles(code);

            try
            {
                foreach (var el in row.FindElements(By.XPath(".//div | .//span | .//a | .//button | .//td | .//li")))
                {
                    string text;
                    try
                    {
                        text = (el.Text ?? "").Trim();
                        if (text.Length is < 2 or > 400)
                            continue;
                    }
                    catch
                    {
                        continue;
                    }

                    var lower = text.ToLowerInvariant();
                    foreach (var n in needles)
                    {
                        if (lower.Contains(n))
                            return PromoteToRowScopedClickTarget(el);
                    }
                }
            }
            catch
            {
                /* ignore */
            }

            foreach (var n in needles)
            {
                if (n.Length < 3)
                    continue;

                try
                {
                    string safe = n.Replace("'", "");
                    var by = By.XPath(
                        ".//*[contains(translate(normalize-space(.),'ABCDEFGHIJKLMNOPQRSTUVWXYZ','abcdefghijklmnopqrstuvwxyz'), \"" +
                        safe + "\")]");
                    foreach (var el in row.FindElements(by))
                    {
                        try
                        {
                            if (el.Displayed)
                                return PromoteToRowScopedClickTarget(el);
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
        }

        private IWebElement PromoteToRowScopedClickTarget(IWebElement hit)
        {
            IWebElement? node = hit;
            for (int depth = 0; depth < 12 && node != null; depth++)
            {
                try
                {
                    if (!node.Displayed)
                        break;

                    string cls = node.GetAttribute("class") ?? "";
                    string tag = node.TagName.ToLowerInvariant();

                    if (node.Size.Height >= 12 && node.Size.Width >= 16)
                    {
                        if (cls.Contains("pre-avl", StringComparison.OrdinalIgnoreCase) ||
                            cls.Contains("avl-p", StringComparison.OrdinalIgnoreCase) ||
                            cls.Contains("avl-enq", StringComparison.OrdinalIgnoreCase) ||
                            cls.Contains("booking", StringComparison.OrdinalIgnoreCase) ||
                            cls.Contains("chair", StringComparison.OrdinalIgnoreCase) ||
                            cls.Contains("executive", StringComparison.OrdinalIgnoreCase) ||
                            tag is "a" or "button")
                            return node;
                    }

                    node = node.FindElement(By.XPath(".."));
                }
                catch
                {
                    break;
                }
            }

            return hit;
        }

        private void RobustClickElement(IWebElement el)
        {
            try
            {
                ((IJavaScriptExecutor)Driver).ExecuteScript("arguments[0].scrollIntoView({block:'center'});", el);
            }
            catch
            {
                /* ignore */
            }

            try
            {
                Thread.Sleep(120);
            }
            catch
            {
                /* ignore */
            }

            try
            {
                _session.SafeClick(el);
            }
            catch (Exception ex)
            {
                Logger.Warning($"BookingEngine.RobustClickElement: SafeClick failed ({ex.Message}); using JS click.");
                try
                {
                    ((IJavaScriptExecutor)Driver).ExecuteScript("arguments[0].click();", el);
                }
                catch (Exception ex2)
                {
                    Logger.Error(ex2, "BookingEngine.RobustClickElement");
                    throw;
                }
            }
        }

        private async Task PollUntilBookNowEnabledAsync(IWebElement card, CancellationToken ct, TimeSpan maxWait)
        {
            var deadline = DateTime.UtcNow + maxWait;
            while (DateTime.UtcNow < deadline)
            {
                ct.ThrowIfCancellationRequested();
                var btn = FindBookNowButtonInCard(card);
                if (btn != null)
                {
                    try
                    {
                        if (btn.Displayed && btn.Enabled)
                            return;
                    }
                    catch
                    {
                        /* ignore */
                    }
                }

                await Task.Delay(280, ct);
            }
        }

        private static IWebElement? FindBookNowButtonInCard(IWebElement card)
        {
            foreach (var by in new[]
                     {
                         By.XPath(".//button[contains(translate(normalize-space(.),'ABCDEFGHIJKLMNOPQRSTUVWXYZ','abcdefghijklmnopqrstuvwxyz'),'book now')]"),
                         By.XPath(".//button[contains(translate(normalize-space(.),'ABCDEFGHIJKLMNOPQRSTUVWXYZ','abcdefghijklmnopqrstuvwxyz'),'book')]"),
                     })
            {
                try
                {
                    foreach (var el in card.FindElements(by))
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
                }
                catch
                {
                    /* ignore */
                }
            }

            try
            {
                foreach (var el in card.FindElements(By.CssSelector("button.trainBook, button[class*='trainBook'], button[class*='btnDefault']")))
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
            }
            catch
            {
                /* ignore */
            }

            return null;
        }

        private static bool RefreshElementBelongsToClassColumn(IWebElement refreshEl, string classCode)
        {
            string code = classCode.Trim();
            string lower = code.ToLowerInvariant();
            IWebElement? node = refreshEl;
            for (int i = 0; i < 14 && node != null; i++)
            {
                string blob = "";
                try
                {
                    blob = node.Text ?? "";
                }
                catch
                {
                    return false;
                }

                if (blob.Length > 3500)
                {
                    try
                    {
                        node = node.FindElement(By.XPath(".."));
                    }
                    catch
                    {
                        break;
                    }
                    continue;
                }

                bool mentionsClass =
                    blob.Contains(code, StringComparison.OrdinalIgnoreCase) ||
                    (lower == "3a" && (blob.Contains("3 tier", StringComparison.OrdinalIgnoreCase) ||
                                       blob.Contains("3a", StringComparison.OrdinalIgnoreCase))) ||
                    (lower == "sl" && blob.Contains("sleeper", StringComparison.OrdinalIgnoreCase)) ||
                    (lower == "2a" && (blob.Contains("2 tier", StringComparison.OrdinalIgnoreCase) ||
                                       blob.Contains("2a", StringComparison.OrdinalIgnoreCase))) ||
                    (lower == "1a" && (blob.Contains("first", StringComparison.OrdinalIgnoreCase) ||
                                       blob.Contains("1a", StringComparison.OrdinalIgnoreCase))) ||
                    (lower == "cc" && blob.Contains("chair car", StringComparison.OrdinalIgnoreCase));

                if (mentionsClass)
                    return true;

                try
                {
                    node = node.FindElement(By.XPath(".."));
                }
                catch
                {
                    break;
                }
            }

            return false;
        }

        private void TryDismissBlockingOverlays()
        {
            try
            {
                var js = (IJavaScriptExecutor)Driver;
                js.ExecuteScript(@"
                    document.querySelectorAll('.ui-widget-overlay, .ui-blockui, .cdk-overlay-backdrop').forEach(function (e) {
                        try { e.style.display = 'none'; } catch (x) {}
                    });");
            }
            catch
            {
                /* ignore */
            }
        }

        private void TryDismissIrctcConfirmIfPresent()
        {
            foreach (var by in new[]
                     {
                         By.CssSelector(".ui-dialog-buttonpane button"),
                         By.XPath("//button[contains(translate(normalize-space(.),'ABCDEFGHIJKLMNOPQRSTUVWXYZ','abcdefghijklmnopqrstuvwxyz'),'ok')]"),
                         By.XPath("//button[contains(translate(normalize-space(.),'ABCDEFGHIJKLMNOPQRSTUVWXYZ','abcdefghijklmnopqrstuvwxyz'),'yes')]"),
                         By.XPath("//button[contains(translate(normalize-space(.),'ABCDEFGHIJKLMNOPQRSTUVWXYZ','abcdefghijklmnopqrstuvwxyz'),'continue')]"),
                     })
            {
                try
                {
                    foreach (var btn in Driver.FindElements(by))
                    {
                        try
                        {
                            if (btn.Displayed && btn.Enabled)
                            {
                                _session.SafeClick(btn);
                                return;
                            }
                        }
                        catch
                        {
                            /* next */
                        }
                    }
                }
                catch
                {
                    /* ignore */
                }
            }
        }

        private async Task<bool> WaitForPassengerOrReviewPageAsync(CancellationToken ct, TimeSpan maxWait)
        {
            int handlesBefore = Driver.WindowHandles.Count;
            var deadline = DateTime.UtcNow + maxWait;

            while (DateTime.UtcNow < deadline)
            {
                ct.ThrowIfCancellationRequested();

                try
                {
                    if (Driver.WindowHandles.Count > handlesBefore)
                        Driver.SwitchTo().Window(Driver.WindowHandles[^1]);

                    string url = Driver.Url ?? "";
                    if (url.Contains("passenger", StringComparison.OrdinalIgnoreCase) ||
                        url.Contains("psgn", StringComparison.OrdinalIgnoreCase) ||
                        url.Contains("review", StringComparison.OrdinalIgnoreCase) ||
                        url.Contains("booking-psgn", StringComparison.OrdinalIgnoreCase) ||
                        url.Contains("booking/psgn", StringComparison.OrdinalIgnoreCase))
                    {
                        Logger.Info($"BookingEngine: Passenger/review URL detected: {url}");
                        return true;
                    }

                    foreach (var by in new[]
                             {
                                 By.CssSelector("app-passenger"),
                                 By.CssSelector("app-passenger-input"),
                                 By.CssSelector("app-psgn-input"),
                                 By.CssSelector(".passenger-entry"),
                                 By.CssSelector("app-review-journey"),
                                 By.CssSelector("app-booking-summary"),
                                 By.CssSelector("app-psgn"),
                             })
                    {
                        foreach (var el in Driver.FindElements(by))
                        {
                            try
                            {
                                if (el.Displayed)
                                {
                                    Logger.Info($"BookingEngine: Passenger/review component visible ({by}).");
                                    return true;
                                }
                            }
                            catch
                            {
                                /* ignore */
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    Logger.Warning($"BookingEngine: WaitForPassengerOrReviewPageAsync: {ex.Message}");
                }

                await Task.Delay(450, ct);
            }

            return false;
        }

        private static IWebElement? FindFirstDisplayedIn(IWebElement root, params By[] locators)
        {
            foreach (var by in locators)
            {
                try
                {
                    foreach (var el in root.FindElements(by))
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
                }
                catch
                {
                    /* ignore */
                }
            }

            return null;
        }

        private IReadOnlyCollection<IWebElement> WaitUntilAnyElements(TimeSpan timeout, params By[] locators)
        {
            var wait = new WebDriverWait(Driver, timeout);
            var els = wait.Until(d =>
            {
                foreach (var by in locators)
                {
                    try
                    {
                        var found = d.FindElements(by);
                        if (found.Count > 0) return found;
                    }
                    catch { /* ignore */ }
                }
                return null;
            });

            if (els == null) return Array.Empty<IWebElement>();
            return els;
        }

        private static IWebElement? PickTrainCard(IReadOnlyCollection<IWebElement> cards, string trainNumber)
        {
            if (!string.IsNullOrWhiteSpace(trainNumber))
            {
                foreach (var c in cards)
                {
                    try
                    {
                        var t = (c.Text ?? "").Trim();
                        if (t.Contains(trainNumber, StringComparison.OrdinalIgnoreCase))
                            return c;
                    }
                    catch { /* ignore */ }
                }
            }

            foreach (var c in cards)
            {
                try { if (c.Displayed) return c; } catch { /* ignore */ }
            }

            return cards.FirstOrDefault();
        }

        private static IWebElement? FindFirstClickableIn(IWebElement root, params By[] locators)
        {
            foreach (var by in locators)
            {
                try
                {
                    foreach (var el in root.FindElements(by))
                    {
                        try
                        {
                            if (el.Displayed && el.Enabled) return el;
                        }
                        catch { /* stale */ }
                    }
                }
                catch { /* ignore */ }
            }
            return null;
        }

        private IWebElement? FindFirstClickableGlobal(params By[] locators)
        {
            foreach (var by in locators)
            {
                try
                {
                    foreach (var el in Driver.FindElements(by))
                    {
                        try
                        {
                            if (el.Displayed && el.Enabled) return el;
                        }
                        catch { /* stale */ }
                    }
                }
                catch { /* ignore */ }
            }
            return null;
        }

        // ── Step 4: Fill Passengers ───────────────────────────────────

        private async Task<bool> FillPassengersAsync(CancellationToken ct)
        {
            Logger.Info($"BookingEngine: Filling {_config.Passengers.Count} passenger(s)...");

            try
            {
                // Wait for passenger form
                Wait.Until(d => d.FindElement(
                    By.CssSelector("app-passenger, .passenger-entry")));

                for (int i = 0; i < _config.Passengers.Count; i++)
                {
                    var p = _config.Passengers[i];
                    string rowSelector = $".passenger-entry:nth-child({i + 1})";

                    // Name
                    var nameField = Driver.FindElement(
                        By.CssSelector($"{rowSelector} input[placeholder*='Name'], " +
                                       $"input[formcontrolname='passengerName']:nth-of-type({i + 1})"));
                    nameField.Clear();
                    nameField.SendKeys(p.Name);

                    // Age
                    var ageField = Driver.FindElement(
                        By.CssSelector($"{rowSelector} input[placeholder*='Age'], " +
                                       $"input[formcontrolname='passengerAge']:nth-of-type({i + 1})"));
                    ageField.Clear();
                    ageField.SendKeys(p.Age.ToString());

                    // Gender
                    var genderSelect = Driver.FindElement(
                        By.CssSelector($"{rowSelector} select[formcontrolname='passengerGender'], " +
                                       $"select[aria-label='Gender']:nth-of-type({i + 1})"));
                    var genderDropdown = new SelectHelper(genderSelect);
                    genderDropdown.SelectByValue(p.Gender);

                    // Berth preference
                    if (!string.IsNullOrEmpty(p.BerthPreference) && p.BerthPreference != "NO")
                    {
                        try
                        {
                            var berthSelect = Driver.FindElement(
                                By.CssSelector($"{rowSelector} select[formcontrolname='berthChoice']"));
                            var berthDropdown = new SelectHelper(berthSelect);
                            berthDropdown.SelectByText(p.BerthPreference);
                        }
                        catch { /* berth preference may not always be available */ }
                    }

                    Logger.Debug($"BookingEngine: Passenger {i + 1} ({p.Name}) filled.");
                    await Task.Delay(200, ct);
                }

                // Handle "Add Insurance" popup if it appears — click "No"
                await Task.Delay(500, ct);
                try
                {
                    var noInsuranceBtn = Driver.FindElement(
                        By.CssSelector("button[class*='InsuranceNo'], button[id*='noInsurance']"));
                    noInsuranceBtn.Click();
                    Logger.Debug("BookingEngine: Dismissed insurance popup.");
                }
                catch { }

                // Click "Continue" / "Review Journey" button
                var continueBtn = Wait.Until(d =>
                    d.FindElement(By.CssSelector(
                        "button[class*='btnDefault'][type='submit'], " +
                        "button.train_Search")));
                _session.SafeClick(continueBtn);

                await Task.Delay(2000, ct);
                Logger.Info("BookingEngine: Passenger details submitted.");
                return true;
            }
            catch (Exception ex)
            {
                Logger.Error(ex, "BookingEngine.FillPassengersAsync");
                _session.TakeScreenshot("passenger_error");
                return false;
            }
        }

        // ── Step 5: Payment ───────────────────────────────────────────

        private async Task<bool> ProceedToPaymentAsync(CancellationToken ct)
        {
            Logger.Info("BookingEngine: Proceeding to payment...");

            try
            {
                // Solve captcha on review page if present
                await Task.Delay(1500, ct);
                try
                {
                    var captchaImg = Driver.FindElement(By.CssSelector("app-captcha img"));
                    if (captchaImg.Displayed)
                    {
                        var captchaInput = Driver.FindElement(
                            By.CssSelector("input[formcontrolname='captcha']"));

                        string? answer;
                        if (_session.CaptchaSolver is ManualCaptchaSolver)
                        {
                            Logger.Info("BookingEngine: Manual captcha — enter the code in the review page, then automation continues.");
                            answer = await ManualCaptchaSolver.WaitForUserEntryAsync(() =>
                            {
                                try
                                {
                                    return Driver.FindElement(
                                        By.CssSelector("input[formcontrolname='captcha'], input[formControlName='captcha']"));
                                }
                                catch
                                {
                                    return null;
                                }
                            }, ct);
                            _result.LastCaptchaSolveSeconds = null;
                        }
                        else
                        {
                            string src = captchaImg.GetAttribute("src") ?? "";
                            string base64 = src.StartsWith("data:")
                                ? src.Substring(src.IndexOf(',') + 1)
                                : Convert.ToBase64String(
                                    await new System.Net.Http.HttpClient()
                                        .GetByteArrayAsync(src, ct));

                            var swCap = Stopwatch.StartNew();
                            answer = await _session.CaptchaSolver.SolveImageCaptchaAsync(base64, ct);
                            _result.LastCaptchaSolveSeconds = swCap.Elapsed.TotalSeconds;
                        }

                        if (!string.IsNullOrEmpty(answer) && _session.CaptchaSolver is not ManualCaptchaSolver)
                        {
                            captchaInput.Clear();
                            captchaInput.SendKeys(answer);
                        }
                    }
                }
                catch { /* no captcha on this page, skip */ }

                // Click "Proceed to Payment"
                var payBtn = Wait.Until(d =>
                    d.FindElement(By.CssSelector(
                        "button[class*='btnDefault'][aria-label*='Pay'], " +
                        "button[class*='proceed']")));
                _session.SafeClick(payBtn);

                await Task.Delay(2000, ct);

                // Select payment method
                await SelectPaymentMethodAsync(ct);

                // Wait for PNR or success confirmation
                await Task.Delay(5000, ct);
                string? pnr = TryExtractPnr();
                if (!string.IsNullOrEmpty(pnr))
                {
                    _result.PnrNumber = pnr;
                    Logger.Info($"BookingEngine: PNR = {pnr}");
                }

                _result.ScreenshotPath = _session.TakeScreenshot("booking_success");
                return true;
            }
            catch (Exception ex)
            {
                Logger.Error(ex, "BookingEngine.ProceedToPaymentAsync");
                _session.TakeScreenshot("payment_error");
                return false;
            }
        }

        private async Task SelectPaymentMethodAsync(CancellationToken ct)
        {
            await Task.Delay(1500, ct);

            switch (_config.PaymentMethod.ToUpperInvariant())
            {
                case "UPI":
                    try
                    {
                        var upiTab = Wait.Until(d =>
                            d.FindElement(By.CssSelector(
                                "[id*='UPI'], [aria-label*='UPI'], label[for*='upi']")));
                        _session.SafeClick(upiTab);
                        await Task.Delay(800, ct);

                        var upiInput = Driver.FindElement(
                            By.CssSelector("input[placeholder*='UPI'], input[id*='upiId']"));

                        if (!string.IsNullOrWhiteSpace(_config.UpiId))
                        {
                            upiInput.Clear();
                            upiInput.SendKeys(_config.UpiId);
                        }
                        else
                        {
                            Logger.Info("BookingEngine: UPI ID not set in the app — type your VPA in Chrome; waiting for the field to be filled…");
                            await WaitForManualUpiVpaAsync(upiInput, ct);
                        }

                        var payNowBtn = Driver.FindElement(
                            By.CssSelector("button[class*='payNow'], button[aria-label*='Pay Now']"));
                        _session.SafeClick(payNowBtn);
                        Logger.Info("BookingEngine: UPI payment initiated.");
                    }
                    catch (Exception ex)
                    {
                        Logger.Error(ex, "UPI payment selection");
                    }
                    break;

                default:
                    Logger.Warning($"BookingEngine: Payment method '{_config.PaymentMethod}' not auto-handled. Manual action needed.");
                    break;
            }
        }

        private string? TryExtractPnr()
        {
            try
            {
                var pnrEl = Driver.FindElement(
                    By.CssSelector(".pnr-no, [class*='pnrNo'], strong"));
                string text = pnrEl.Text.Trim();
                if (text.Length >= 10 && text.All(char.IsDigit)) return text;
            }
            catch { }
            return null;
        }

        /// <summary>Waits until the UPI field looks like a filled VPA (contains @) and is briefly stable.</summary>
        private static async Task WaitForManualUpiVpaAsync(IWebElement upiInput, CancellationToken ct,
            TimeSpan? maxWait = null)
        {
            maxWait ??= TimeSpan.FromMinutes(12);
            DateTime deadline = DateTime.UtcNow + maxWait.Value;
            string? pending = null;
            DateTime pendingSince = DateTime.MinValue;

            while (DateTime.UtcNow < deadline && !ct.IsCancellationRequested)
            {
                string v;
                try
                {
                    v = upiInput.GetAttribute("value")?.Trim() ?? "";
                }
                catch (StaleElementReferenceException)
                {
                    Logger.Warning("BookingEngine: UPI field went stale while waiting for manual entry.");
                    return;
                }

                bool plausible = v.Length >= 5 && v.Contains('@', StringComparison.Ordinal);
                if (plausible)
                {
                    if (v == pending)
                    {
                        if ((DateTime.UtcNow - pendingSince).TotalMilliseconds >= 650)
                            return;
                    }
                    else
                    {
                        pending = v;
                        pendingSince = DateTime.UtcNow;
                    }
                }
                else
                    pending = null;

                try
                {
                    await Task.Delay(350, ct);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
            }

            Logger.Warning("BookingEngine: Timed out waiting for manual UPI — check the browser and Pay Now if needed.");
        }

        // ── Helpers ───────────────────────────────────────────────────

        private BookingResult Fail(string reason)
        {
            _result.Status = BookingStatus.Failed;
            _result.ErrorMessage = reason;
            _result.CompletedAt = DateTime.Now;
            Logger.Error($"BookingEngine: FAILED – {reason}");
            OnStatusChanged?.Invoke(_result);
            return _result;
        }

        private void UpdateStatus(BookingStatus status)
        {
            _result.Status = status;
            Logger.Info($"BookingEngine: → {status}");
            OnStatusChanged?.Invoke(_result);
        }
    }
}
