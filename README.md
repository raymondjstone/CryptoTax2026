# CryptoTax2026

A Windows desktop application that connects to the Kraken cryptocurrency exchange API, downloads your complete trade history, and calculates UK Capital Gains Tax liabilities for each tax year.

[![Buy Me A Coffee](https://cdn.buymeacoffee.com/buttons/v2/default-yellow.png)](https://buymeacoffee.com/raymondjstone)

---

## IMPORTANT DISCLAIMER

**THIS SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY, FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT.**

**This application is UNTESTED and UNAUDITED. It has not been verified by any tax professional, accountant, or HMRC.**

**You MUST NOT rely on this software for filing your tax return or making any financial decisions. The calculations produced by this application may be incorrect, incomplete, or based on outdated tax rules.**

**You are solely responsible for the accuracy of your tax filings. Always consult a qualified tax professional or accountant before submitting any tax return to HMRC.**

**The authors and contributors accept NO LIABILITY whatsoever for any losses, penalties, fines, or other consequences arising from the use of this software.**

---

## What It Does

- Connects to the Kraken API using your API key and secret
- Downloads your **full ledger history** (not just trades) in batches, including trades, staking rewards, deposits, withdrawals, airdrops, and fees
- Caches ledger data locally so you don't need to re-download each time
- Resumes downloads from where it left off
- **Imports CSV trade history** from other exchanges: Coinbase, Binance, Crypto.com, and Bybit
- **Converts all amounts to GBP** using historical daily exchange rates with intelligent source prioritization:
  - **Primary source**: Kraken's public OHLC API for directly available pairs (e.g., BTC/GBP, ETH/GBP, ADA/GBP)
  - **Automatic discovery**: Dynamically discovers all available Kraken FX pairs on startup, filtering for relevant quote currencies (GBP, USD, EUR, stablecoins)
  - **Fallback source**: CryptoCompare API for assets not available on Kraken
  - **Smart routing**: USD, EUR, and other fiat currencies converted at daily rates; crypto assets use direct GBP pairs when available, otherwise route via USD + USD/GBP
  - **USDT is NOT treated as USD** — it is converted via USDT/USD rate first, then USD/GBP (two-step conversion)
  - **HMRC-compliant rate calculation methods**: Choose from Daily Open, High, Low, Close, Average, or Nearest Live Rate
  - **Consistent rate application**: Once selected, the same method is applied to all transactions for HMRC compliance
  - **Rate transparency**: Ledger view and tax year disposal details show the exact GBP rate used, its timestamp, and source (Kraken/CryptoCompare) for each transaction
  - **Persistent caching**: FX rates cached locally on disk with source tracking to minimize API calls
- Calculates UK Capital Gains Tax for each tax year using HMRC rules:
  - **Same-day rule** — matches disposals with acquisitions on the same day
  - **Bed & breakfast rule (30-day rule)** — matches disposals with acquisitions within 30 days after
  - **Section 104 pooling** — average cost basis for remaining holdings
  - **Annual exempt amount** — applied per tax year at the correct historical rate
  - **CGT rate banding** — splits gains between basic and higher rate based on your taxable income
  - **Loss carry-forward** — automatically carries forward allowable losses to offset future gains
- **Handles delisted assets** — configure assets that have been delisted/become worthless for synthetic disposal at £0 proceeds
- **Cost basis overrides** — manually override the allowable cost for any disposal (e.g. when you transferred crypto from another exchange at a known purchase price)
- **Disposal notes** — attach notes to individual disposals for your records
- **Tracks staking rewards** as miscellaneous income (separate from CGT), valued at GBP market rate on the date received
- **Balance snapshots** — shows portfolio holdings and estimated GBP value at the start and end of each tax year
- **SA108 summary** — pre-filled summary matching HMRC Self Assessment boxes, with copy-to-clipboard
- **P&L summary** — cross-year profit & loss breakdown by asset
- **Holdings view** — current Section 104 pool quantities and average cost bases
- **What-if scenario tool** — model hypothetical trades to see their CGT impact before executing, including cross-year loss carry-forward effects and 30-day B&B warnings
- **Shows data quality warnings** — flags issues like negative pool quantities, missing FX rates, unmatched ledger entries, deposits valued at market rate, and rounding shortfalls
- Provides a tab for each tax year where you enter your taxable income and other capital gains
- Recalculates tax owed instantly when you change inputs (lightweight summary-only recalculation when only income/other gains change)
- Exports to Excel (.xlsx), PDF, Word (.docx), SA108 PDF, accountant report PDF, and HMRC disposal schedule CSV
- **Audit log** — tracks cost basis overrides and other manual changes

## Tax Rates

The application includes hardcoded UK CGT rates for the following tax years:

| Tax Year | Annual Exempt Amount | Basic Rate | Higher Rate |
|----------|---------------------|------------|-------------|
| 2022/23 and earlier | 12,300 | 10% | 20% |
| 2023/24 | 6,000 | 10% | 20% |
| 2024/25 | 3,000 | 10% | 20% |
| 2025/26 onwards | 3,000 | 18% | 24% |

**These rates may be wrong or out of date. Verify them against official HMRC guidance before relying on any output.**

### Scottish & Welsh Taxpayers

Capital Gains Tax is a **UK-wide reserved tax** — it is not devolved to Scotland or Wales. This means:

- **CGT rates** (18% basic / 24% higher for 2025/26) are the **same for all UK taxpayers**
- **Annual exempt amount** (£3,000 for 2024/25 onwards) is the **same for all UK taxpayers**
- **The basic rate band used for CGT** is always the **UK-wide £37,700**, not the Scottish starter/basic/intermediate thresholds
- **The personal allowance** (£12,570) is set UK-wide and is the same regardless of Scottish/Welsh tax status

Scottish and Welsh income tax bands only affect income tax. When you enter your taxable income in the app, it uses the UK basic rate band to determine how much of your gains are taxed at the lower CGT rate — this is correct for Scottish and Welsh taxpayers. No adjustment is needed.

## Requirements

- Windows 10 (version 1809 or later) or Windows 11
- .NET 8 SDK
- A Kraken API key with "Query Ledger/Trade Data" permission (for Kraken data; CSV import works without API keys)

## Setup

1. Clone this repository
2. Build with `dotnet build -p:Platform=x64`
3. Run the application
4. On the Settings tab, enter your Kraken API key and secret, then click Save Credentials
5. Click Download Trades to fetch your trade history
6. Optionally import CSV files from other exchanges via the CSV Import tab
7. Click Download FX Rates to fetch historical exchange rates
8. Navigate to each tax year tab and enter your taxable income for that year

## Automation / Scheduled Data Sync

The application supports headless operation for scheduled data synchronization. This allows you to keep your data current automatically without needing to remember to run the application manually.

### Command Line Usage

```bash
CryptoTax2026.exe --SyncData
```

This will:
1. Start the application in headless mode (no UI)
2. Load your saved API credentials and settings
3. Download new ledger entries from Kraken (incremental sync)
4. Update FX rates for all currencies in your ledger
5. Display progress and status information in the console
6. Exit automatically when complete

### Scheduling with Windows Task Scheduler

To automatically sync data daily:

1. Open Windows Task Scheduler
2. Create a new Basic Task
3. Set it to run daily at your preferred time
4. Set the action to start the program:
   - **Program**: `C:\Path\To\CryptoTax2026.exe`
   - **Arguments**: `--SyncData`
   - **Start in**: `C:\Path\To\` (directory containing the exe)
5. Enable "Run whether user is logged on or not" if desired

### Prerequisites for Automation

- Kraken API credentials must be configured via the UI first
- The application must have been run at least once in normal mode
- Your API key must have "Query Ledger/Trade Data" permission

### Example Output

```
CryptoTax2026: Starting sync mode...
Testing Kraken API connection...
✓ Kraken API connection successful
Downloading ledger data...
  Downloading ledger (offset 0)...
  Downloading ledger (offset 50)...
✓ Ledger updated: 15 new entries, 1,247 total entries
Downloading FX rates...
  Loading FX rates: GBPUSD (1/8)...
  Loading FX rates: ADAGBP (2/8)...
  ...
✓ FX rates updated for 8 currencies
✓ Sync completed successfully
```

### Error Handling

If the sync fails (e.g., API connection issues, invalid credentials), the application will:
- Display the error message in the console
- Exit with a non-zero exit code
- Log details for troubleshooting

This makes it suitable for monitoring in automated environments.

## Kraken API Key

To create an API key:

1. Log in to kraken.com
2. Go to Security > API
3. Create a new key with only the **"Query Ledger/Trade Data"** permission enabled
4. Do NOT enable trading, withdrawal, or any other permissions

## CSV Import

The CSV Import tab supports importing trade history from:

- **Coinbase** — standard transaction history CSV export
- **Binance** — trade history CSV export (automatically splits market pairs like BTCUSDT into base/quote assets)
- **Crypto.com** — transaction history CSV export
- **Bybit** — trade history CSV export

Imported entries are merged with Kraken ledger data for CGT calculation.

## Data Storage

All data is stored locally on your machine. By default, the location is:

```
%LocalAppData%\CryptoTax2026\
```

### Custom Data Path

You can configure a custom storage location via the Settings page. This is useful for:
- Storing data on a different drive
- Using a cloud-synced folder (Dropbox, OneDrive, etc.)
- Keeping data with your other financial records

When you set a custom path, the application creates a pointer file at the default location that remembers your custom choice.

### Data Files

The storage folder contains:
- `settings.json` — your API credentials, tax year inputs, cost overrides, disposal notes, delisted assets, imported CSV entries, and audit log
- `ledger.json` — cached Kraken ledger history
- `trades.json` — cached trade history (legacy, kept for compatibility)
- `fx_cache/` — subdirectory containing cached daily FX rates from Kraken and CryptoCompare
  - Individual files for each currency pair (e.g., `BTCGBP.json`, `ADAUSD.json`)
  - Automatic cleanup of stale rate files older than 2 years
- `pairmap.json` — tracks which FX rate source (Kraken vs CryptoCompare) each asset uses
- `custom_path.json` — pointer file (stored in default location only) that points to your custom data folder

### Security Note

**Your Kraken API credentials are stored in plain text in the `settings.json` file. Do not share this folder or file with others.**

The API key only has "Query Ledger/Trade Data" permission and cannot be used for trading or withdrawals, but it could still allow someone to view your transaction history.

## Known Limitations

- **FX rate timing**: FX conversion uses daily rates from the configured source (Kraken OHLC or CryptoCompare), not the exact rate at the moment of the trade. This may differ slightly from the actual rate but is acceptable for HMRC purposes when applied consistently.
- **Limited pair coverage**: While the app automatically discovers available Kraken FX pairs and falls back to CryptoCompare, some obscure altcoins may not have historical rate data available — the app will warn you when this happens.
- **Deposit valuation**: Crypto deposits from external wallets are valued at market rate on the date received. If you transferred from another exchange where you bought at a different price, the cost basis will be wrong — use the cost basis override feature to correct it.
- **Cross-exchange transfers**: Does not handle transfers between exchanges (no way to automatically link deposit on Kraken to purchase on another exchange).
- **DeFi limitations**: Does not handle DeFi transactions, staking on other platforms, or complex smart contract interactions outside supported exchanges.
- **Standard tax treatment only**: Does not handle the remittance basis, trading as a business, or other non-standard UK tax situations.

## Tests

The test project is at `CryptoTax2026.Tests/` and uses xUnit. Run with:

```
dotnet test CryptoTax2026.Tests/CryptoTax2026.Tests.csproj -p:Platform=x64
```

## Tech Stack

- WinUI 3 / Windows App SDK
- .NET 8
- QuestPDF (PDF export)
- ClosedXML (Excel export)
- DocumentFormat.OpenXml (Word export)
- xUnit (tests)

## License

This project has no licence. Use at your own risk.
