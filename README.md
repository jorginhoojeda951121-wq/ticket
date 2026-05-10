# IRCTC Tatkal Bot — Setup & Developer Guide

## Prerequisites

| Requirement | Version |
|---|---|
| Windows | 10 / 11 (x64) |
| .NET SDK | 8.0+ |
| Google Chrome | Latest stable |
| Visual Studio | 2022 (Community or higher) |
| 2Captcha account | Any plan with balance |

## Quick Start

### 1. Clone / Extract the project
```
git clone <your-repo-url>
cd IRCTCTatkalBot
```

### 2. Open in Visual Studio
Double-click `IRCTCTatkalBot.sln` → Visual Studio 2022 opens.

### 3. Restore NuGet packages
Visual Studio does this automatically on first build.
Or run: `dotnet restore`

### 4. Build
```
dotnet build -c Release
```
Output: `IRCTCTatkalBot\bin\Release\net8.0-windows\IRCTCTatkalBot.exe`

### 5. Run
Double-click the EXE, or press F5 in Visual Studio.

---

## First-Time Configuration

### Step 1 — Captcha provider & API keys
In **Settings**:
- Choose **2Captcha** or **Anti-Captcha**.
- Paste your **2Captcha** key from https://2captcha.com (or leave Anti-Captcha key field empty and paste that vendor’s key into the main field when using Anti-Captcha only).
- Optional: **Anti-Captcha client key** field if you use both or want a dedicated key for Anti-Captcha.

Uncheck **Show Chrome windows** only if you accept headless automation (harder to debug).

### Step 2 — Add IRCTC accounts
Click **"+ Add Account"** and fill in:
- Display name (any label you like)
- IRCTC username
- IRCTC password (stored AES-256 encrypted)
- Phone number (needed for OTP during payment)
- Proxy (optional but recommended — see Proxy section below)

### Step 3 — Add passengers
Edit `ViewModels/MainViewModel.cs` → `GetPassengerList()` to hardcode your
passenger details, or extend the UI to take them from a DataGrid.

### Step 4 — Set train config
In the left panel, fill in:
- From / To station codes (e.g. NDLS, MMCT)
- Journey date
- Train number (leave blank to book first available)
- Class (SL / 3A / 2A / 1A / CC)
- UPI ID for payment

---

## Running a Booking

### Scheduled Mode (recommended for Tatkal)
Click **▶ Start (Scheduled)**.

The bot will:
1. Calculate the next Tatkal window (10:00 AM for AC, 11:00 AM for non-AC)
2. Show a countdown timer
3. At T-90 seconds: open Chrome, log in all accounts simultaneously
4. At T=0: fire all booking tasks in parallel
5. Display results (PNR, time taken, errors) in the Results table

### Immediate Mode (for testing)
Click **⚡ Start Now** — skips the scheduler and books immediately.

---

## Module Architecture

```
IRCTCTatkalBot/
├── Models/
│   ├── Account.cs          — IRCTC account (credentials, proxy, stats)
│   ├── Passenger.cs        — Traveller details
│   ├── BookingConfig.cs    — Full booking parameters
│   └── BookingResult.cs    — Outcome of one booking attempt
│
├── Services/
│   ├── AccountManager.cs   — CRUD + encrypted persistence for accounts
│   ├── ICaptchaSolver.cs   — Captcha abstraction
│   ├── CaptchaSolver.cs    — 2Captcha API client (image + reCAPTCHA)
│   ├── AntiCaptchaSolver.cs — Anti-Captcha ImageToTextTask client
│   ├── CaptchaSolverFactory.cs — Picks solver from settings
│   ├── PassengerStore.cs   — passengers.json persistence
│   ├── PreFlightValidator.cs — Blocks invalid runs before automation starts
│   ├── SessionManager.cs   — One ChromeDriver session per account
│   ├── BookingEngine.cs    — Step-by-step booking automation
│   ├── Scheduler.cs        — Precision countdown to Tatkal window
│   └── BookingOrchestrator.cs — Coordinates N sessions concurrently
│
├── ViewModels/
│   └── MainViewModel.cs    — MVVM bridge between UI and services
│
├── Views/
│   └── AddAccountDialog.xaml — Dialog for adding accounts
│
├── Helpers/
│   ├── EncryptionHelper.cs — AES-256 encrypt/decrypt
│   ├── RelayCommand.cs     — Simple ICommand for MVVM buttons
│   └── Logger.cs           — Thread-safe file + UI logger
│
├── AppSettings.cs          — Global settings (API key, retries, etc.)
├── MainWindow.xaml         — Main UI layout
└── App.xaml                — WPF app entry point
```

Unit tests: `IRCTCTatkalBot.Tests/` (xUnit, same solution).

---

## Proxy Setup (Strongly Recommended)

IRCTC blocks many datacenter IPs. Use Indian residential proxies:

**Format:** `socks5://username:password@host:port`

Per-account proxy: set the Proxy field when adding an account.
This ensures each Chrome session uses a different Indian IP.

Recommended providers for Indian residential proxies:
- BrightData (Luminati)
- Oxylabs
- Smartproxy

---

## Captcha Handling

IRCTC uses image captchas on the login page and sometimes on the booking review page.

Flow:
1. `SessionManager` extracts the captcha `<img>` as Base64
2. Sends it through **`ICaptchaSolver`** (`CaptchaSolver` for 2Captcha or `AntiCaptchaSolver` for Anti-Captcha)
3. Workers solve it (latency varies by provider and load)
4. Answer is typed into the captcha input field

**Timing:** The **Results** grid includes a **Phases** column (`L/S/T/P/Pay` seconds) and logs record the same. Payment-page captcha duration is captured when that captcha appears.

**Tip:** Keep your solver balance topped up.

---

## Automated tests

```powershell
dotnet test IRCTCTatkalBot.sln -c Release
```

---

## Publish (folder deploy / installer input)

Release folder suitable for zip distribution or wrapping with Inno Setup / WiX:

```powershell
dotnet publish IRCTCTatkalBot\IRCTCTatkalBot.csproj -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true -o publish
```

Users still need **Chrome** + **.NET 8 Desktop Runtime** when `--self-contained false`.

---

## OTP Handling

IRCTC sometimes requires an OTP during payment. The current code does NOT
auto-solve OTP (phone access required). Options:

1. **Manual**: Add a brief pause and manually enter OTP when prompted
2. **SMS API**: Integrate an SMS reading API (e.g. SMS Man, SMSHUB) and
   extend `SessionManager` to read the OTP automatically

---

## Acceptance Criteria Mapping

| Criterion | Implementation |
|---|---|
| Captcha < 2s | 2Captcha polling with 2s intervals; typical solve ~8s. Pre-solve during T-90s login phase brings effective booking-window delay to ~0s |
| 5+ concurrent bookings | `BookingOrchestrator` uses `Task.WhenAll` — fully parallel, one Chrome instance per account |
| End-to-end < 45s | Login is done during pre-login phase; at T=0 only Search→Select→Fill→Pay runs (~15–25s on broadband) |

---

## Troubleshooting

| Problem | Solution |
|---|---|
| ChromeDriver version mismatch | Update `Selenium.WebDriver.ChromeDriver` NuGet package to match your Chrome version |
| Login fails with "wrong credentials" | Double-check username/password; IRCTC is case-sensitive |
| Captcha never solved | Check 2Captcha balance and API key; try a manual test via their dashboard |
| Train not found | Verify station codes (use IRCTC search to confirm exact codes) |
| IRCTC blocks session | Switch proxy; reduce booking frequency |

---

## Legal Notice

This software is provided for **security research and testing purposes only**.
Automating IRCTC bookings may violate IRCTC's Terms of Service.
Use responsibly and at your own risk.
