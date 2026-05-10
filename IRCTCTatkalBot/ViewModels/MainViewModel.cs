using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using OpenQA.Selenium.Chrome;
using IRCTCTatkalBot;
using IRCTCTatkalBot.Helpers;
using IRCTCTatkalBot.Models;
using IRCTCTatkalBot.Services;

namespace IRCTCTatkalBot.ViewModels
{
    /// <summary>
    /// Main application ViewModel — binds the UI to the booking orchestrator.
    /// </summary>
    public class MainViewModel : INotifyPropertyChanged
    {
        private readonly PassengerStore _passengerStore = new();
        private readonly DispatcherTimer _savePassengersTimer;

        public AccountManager AccountManager { get; } = new();
        private BookingOrchestrator? _orchestrator;
        private CancellationTokenSource _cts = new();

        public ObservableCollection<Account> Accounts { get; } = new();
        public ObservableCollection<BookingResult> Results { get; } = new();
        public ObservableCollection<string> Logs { get; } = new();

        public ObservableCollection<Passenger> Passengers { get; } = new();

        private Account? _selectedAccount;
        public Account? SelectedAccount
        {
            get => _selectedAccount;
            set
            {
                _selectedAccount = value;
                OnPropertyChanged();
                CommandManager.InvalidateRequerySuggested();
            }
        }

        public ICommand RemoveAccountCommand { get; }

        private Passenger? _selectedPassenger;
        public Passenger? SelectedPassenger
        {
            get => _selectedPassenger;
            set
            {
                _selectedPassenger = value;
                OnPropertyChanged();
                CommandManager.InvalidateRequerySuggested();
            }
        }

        public ICommand AddPassengerCommand { get; }
        public ICommand RemovePassengerCommand { get; }

        private string _fromStation = "NDLS";
        public string FromStation
        {
            get => _fromStation;
            set { _fromStation = value; OnPropertyChanged(); }
        }

        private string _toStation = "MMCT";
        public string ToStation
        {
            get => _toStation;
            set { _toStation = value; OnPropertyChanged(); }
        }

        private DateTime _journeyDate = DateTime.Today.AddDays(1);
        public DateTime JourneyDate
        {
            get => _journeyDate;
            set { _journeyDate = value; OnPropertyChanged(); }
        }

        private string _trainNumber = "";
        public string TrainNumber
        {
            get => _trainNumber;
            set { _trainNumber = value; OnPropertyChanged(); }
        }

        private string _trainClass = "3A";
        public string TrainClass
        {
            get => _trainClass;
            set
            {
                _trainClass = value;
                OnPropertyChanged();
                WindowTimeDisplay = Scheduler.GetWindowForClass(value).ToString(@"hh\:mm") + " AM IST";
            }
        }

        private string _upiId = "";
        public string UpiId
        {
            get => _upiId;
            set { _upiId = value; OnPropertyChanged(); }
        }

        private string _windowTimeDisplay = "10:00 AM IST";
        public string WindowTimeDisplay
        {
            get => _windowTimeDisplay;
            set { _windowTimeDisplay = value; OnPropertyChanged(); }
        }

        private string _countdownText = "--:--";
        public string CountdownText
        {
            get => _countdownText;
            set { _countdownText = value; OnPropertyChanged(); }
        }

        private bool _isRunning;
        public bool IsRunning
        {
            get => _isRunning;
            set
            {
                _isRunning = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(CanStart));
                CommandManager.InvalidateRequerySuggested();
            }
        }

        public bool CanStart => !IsRunning;

        public string CaptchaApiKey
        {
            get => AppSettings.Instance.TwoCaptchaApiKey;
            set { AppSettings.Instance.TwoCaptchaApiKey = value; AppSettings.Instance.Save(); OnPropertyChanged(); }
        }

        public string AntiCaptchaApiKey
        {
            get => AppSettings.Instance.AntiCaptchaApiKey;
            set { AppSettings.Instance.AntiCaptchaApiKey = value; AppSettings.Instance.Save(); OnPropertyChanged(); }
        }

        public string CaptchaProviderSetting
        {
            get => AppSettings.Instance.CaptchaProvider;
            set { AppSettings.Instance.CaptchaProvider = value; AppSettings.Instance.Save(); OnPropertyChanged(); }
        }

        public bool ShowBrowserWindows
        {
            get => AppSettings.Instance.ShowBrowserWindows;
            set { AppSettings.Instance.ShowBrowserWindows = value; AppSettings.Instance.Save(); OnPropertyChanged(); }
        }

        /// <summary>Optional path to chrome.exe when Chrome is not on PATH or you use a portable install.</summary>
        public string ChromeBinaryPath
        {
            get => AppSettings.Instance.ChromeBinaryPath ?? string.Empty;
            set
            {
                AppSettings.Instance.ChromeBinaryPath = value?.Trim() ?? string.Empty;
                AppSettings.Instance.Save();
                OnPropertyChanged();
            }
        }

        public MainViewModel()
        {
            foreach (var a in AccountManager.Accounts)
                Accounts.Add(a);
            if (Accounts.Count > 0)
                SelectedAccount = Accounts[0];

            _savePassengersTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(650) };
            _savePassengersTimer.Tick += (_, _) =>
            {
                _savePassengersTimer.Stop();
                try { _passengerStore.Save(Passengers.ToList()); }
                catch (Exception ex) { Logger.Error(ex, "PassengerStore.Save"); }
            };

            Passengers.CollectionChanged += Passengers_CollectionChanged;

            foreach (var p in _passengerStore.Load())
                Passengers.Add(p);

            if (Passengers.Count == 0)
                AddPassengerInternal();

            AddPassengerCommand = new RelayCommand(AddPassengerInternal, () => !IsRunning);
            RemovePassengerCommand = new RelayCommand(RemovePassengerInternal,
                () => !IsRunning && SelectedPassenger != null && Passengers.Count > 1);
            RemoveAccountCommand = new RelayCommand(RemoveAccountInternal,
                () => !IsRunning && SelectedAccount != null);

            Logger.OnLog += (msg, level) =>
            {
                Application.Current?.Dispatcher.BeginInvoke(() =>
                {
                    Logs.Insert(0, msg);
                    if (Logs.Count > 500) Logs.RemoveAt(Logs.Count - 1);
                });
            };
        }

        private void Passengers_CollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
        {
            if (e.NewItems != null)
            {
                foreach (Passenger p in e.NewItems)
                    HookPassenger(p);
            }

            ScheduleSavePassengers();
        }

        private void HookPassenger(Passenger p) =>
            p.PropertyChanged += (_, _) => ScheduleSavePassengers();

        private void ScheduleSavePassengers()
        {
            _savePassengersTimer.Stop();
            _savePassengersTimer.Start();
        }

        private void AddPassengerInternal()
        {
            var p = new Passenger { Name = "", Age = 25, Gender = "M" };
            Passengers.Add(p);
            SelectedPassenger = p;
        }

        private void RemovePassengerInternal()
        {
            if (SelectedPassenger == null || Passengers.Count <= 1) return;
            int idx = Passengers.IndexOf(SelectedPassenger);
            Passengers.Remove(SelectedPassenger);
            SelectedPassenger = Passengers[Math.Max(0, idx - 1)];
        }

        private void RemoveAccountInternal()
        {
            if (SelectedAccount == null || IsRunning) return;

            var acc = SelectedAccount;
            var confirm = MessageBox.Show(
                $"Remove account \"{acc.DisplayName}\" ({acc.Username})? Saved credentials will be deleted.",
                "Remove account",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question);
            if (confirm != MessageBoxResult.Yes) return;

            int idx = Accounts.IndexOf(acc);
            RemoveAccount(acc);
            if (Accounts.Count == 0)
                SelectedAccount = null;
            else
                SelectedAccount = Accounts[Math.Min(idx, Accounts.Count - 1)];
        }

        /// <summary>Clone passengers for booking configs (safe if UI edits during run).</summary>
        public List<Passenger> GetPassengersSnapshot() =>
            Passengers.Select(p => new Passenger
            {
                Name = (p.Name ?? "").Trim(),
                Age = p.Age,
                Gender = p.Gender ?? "M",
                BerthPreference = p.BerthPreference ?? "NO",
                IdType = p.IdType ?? "PAN",
                IdNumber = p.IdNumber ?? "",
                IsSeniorCitizen = p.IsSeniorCitizen,
                Nationality = p.Nationality ?? "IN"
            }).ToList();

        public async Task StartBookingAsync(bool immediate = false)
        {
            if (IsRunning) return;

            _savePassengersTimer.Stop();
            try { _passengerStore.Save(Passengers.ToList()); }
            catch (Exception ex) { Logger.Error(ex, "PassengerStore.Save"); }

            var issues = PreFlightValidator.Validate(this, AccountManager);
            if (issues.Count > 0)
            {
                MessageBox.Show(string.Join(Environment.NewLine, issues),
                    "Cannot start booking", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            IsRunning = true;
            Results.Clear();
            _cts = new CancellationTokenSource();

            ICaptchaSolver solver = CaptchaSolverFactory.CreateFromSettings();
            _orchestrator = new BookingOrchestrator(AccountManager, solver);

            _orchestrator.OnStatusMessage += msg =>
                Application.Current?.Dispatcher.BeginInvoke(() => CountdownText = msg);

            _orchestrator.OnBookingResult += result =>
                Application.Current?.Dispatcher.BeginInvoke(() => Results.Add(result));

            var pax = GetPassengersSnapshot();

            var configs = new List<BookingConfig>();
            foreach (var account in AccountManager.GetActive())
            {
                configs.Add(new BookingConfig
                {
                    AccountId = account.Id,
                    FromStation = FromStation,
                    ToStation = ToStation,
                    JourneyDate = JourneyDate,
                    TrainNumber = TrainNumber,
                    TrainClass = TrainClass,
                    Quota = "TATKAL",
                    PaymentMethod = "UPI",
                    UpiId = UpiId,
                    Passengers = pax,
                    TargetOpenTime = Scheduler.GetWindowForClass(TrainClass),
                    AutoSchedule = !immediate,
                    MaxRetries = AppSettings.Instance.DefaultRetries,
                    RetryDelayMs = AppSettings.Instance.RetryDelayMs
                });
            }

            try
            {
                await _orchestrator.RunAllAsync(configs, !immediate, _cts.Token);
            }
            catch (OperationCanceledException)
            {
                Logger.Info("StartBookingAsync: cancelled.");
            }
            catch (Exception ex)
            {
                Logger.Error(ex, "StartBookingAsync");
                Application.Current?.Dispatcher.Invoke(() =>
                    MessageBox.Show(
                        DriverDiagnostics.BuildUserFacingMessage(ex),
                        "Booking run failed",
                        MessageBoxButton.OK,
                        MessageBoxImage.Warning));
            }
            finally
            {
                IsRunning = false;
                CountdownText = "Done";
            }
        }

        /// <summary>Starts Chrome once and opens about:blank (diagnostics; does not touch IRCTC).</summary>
        public Task TestChromeDriverAsync(CancellationToken ct = default)
        {
            return Task.Run(() =>
            {
                string tempProfile = Path.Combine(Path.GetTempPath(), "IRCTCTatkalBot_smoke_" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(tempProfile);

                var options = new ChromeOptions();
                options.AddArgument("--disable-notifications");
                options.AddArgument($"--user-data-dir={tempProfile}");
                if (!AppSettings.Instance.ShowBrowserWindows)
                    options.AddArgument("--headless=new");

                string chromePath = (AppSettings.Instance.ChromeBinaryPath ?? string.Empty).Trim();
                if (chromePath.Length > 0 && File.Exists(chromePath))
                    options.BinaryLocation = chromePath;

                using (var driver = ChromeDriverFactory.Create(options))
                {
                    ct.ThrowIfCancellationRequested();
                    driver.Navigate().GoToUrl("about:blank");
                }

                try { Directory.Delete(tempProfile, true); } catch { /* ignore */ }
            }, ct);
        }

        public void StopBooking()
        {
            _orchestrator?.Cancel();
            _cts.Cancel();
            IsRunning = false;
            Logger.Info("Booking stopped by user.");
        }

        public void AddAccount(string username, string password)
        {
            var account = AccountManager.Add(
                displayName: username,
                username: username,
                plainPassword: password);
            Accounts.Add(account);
            SelectedAccount = account;
        }

        public void RemoveAccount(Account account)
        {
            AccountManager.Remove(account.Id);
            Accounts.Remove(account);
        }

        public event PropertyChangedEventHandler? PropertyChanged;
        protected void OnPropertyChanged([CallerMemberName] string? name = null)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}
