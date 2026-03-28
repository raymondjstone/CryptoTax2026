# CryptoTax2026

A Windows desktop application that connects to the Kraken cryptocurrency exchange API, downloads your complete trade history, and calculates UK Capital Gains Tax liabilities for each tax year.

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
- Downloads your **full ledger history** (not just trades) in batches, including trades, staking rewards, deposits, withdrawals, and fees
- Caches ledger data locally so you don't need to re-download each time
- Resumes downloads from where it left off
- **Converts all amounts to GBP** using historical daily exchange rates downloaded from Kraken's public OHLC API
  - USD, EUR, and other fiat currencies converted at the correct daily rate
  - **USDT is NOT treated as USD** — it is converted via USDT/USD rate first, then USD/GBP (two-step conversion)
  - Crypto-to-crypto trades valued using direct GBP pair or via USD pair + USD/GBP
  - FX rates are cached locally for 24 hours
- Calculates UK Capital Gains Tax for each tax year using HMRC rules:
  - **Same-day rule** - matches disposals with acquisitions on the same day
  - **Bed & breakfast rule (30-day rule)** - matches disposals with acquisitions within 30 days after
  - **Section 104 pooling** - average cost basis for remaining holdings
  - **Annual exempt amount** - applied per tax year at the correct historical rate
  - **CGT rate banding** - splits gains between basic and higher rate based on your taxable income
- **Tracks staking rewards** as miscellaneous income (separate from CGT), valued at GBP market rate on the date received
- **Shows data quality warnings** — flags issues like negative pool quantities, missing FX rates, unmatched ledger entries, and deposits valued at market rate
- Provides a tab for each tax year where you enter your taxable income and other capital gains
- Recalculates tax owed instantly when you change inputs
- Exports to Excel (.xlsx), PDF, and Word (.docx) with optional raw Kraken data included

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
- A Kraken API key with "Query Ledger/Trade Data" permission

## Setup

1. Clone this repository
2. Build with `dotnet build -p:Platform=x64`
3. Run the application
4. On the Settings tab, enter your Kraken API key and secret, then click Save Credentials
5. Click Download Trades to fetch your trade history
6. Navigate to each tax year tab and enter your taxable income for that year

## Kraken API Key

To create an API key:

1. Log in to kraken.com
2. Go to Security > API
3. Create a new key with only the **"Query Ledger/Trade Data"** permission enabled
4. Do NOT enable trading, withdrawal, or any other permissions

## Data Storage

All data is stored locally on your machine at:

```
%LocalAppData%\CryptoTax2026\
```

This includes:
- `ledger.json` - cached Kraken ledger history
- `trades.json` - cached trade history (legacy)
- `settings.json` - your API credentials and tax year inputs
- `fx_cache/` - cached daily FX rates from Kraken

**Your API credentials are stored in plain text on your local machine. Do not share this folder.**

## Known Limitations

- FX conversion uses daily closing prices from Kraken's OHLC data, not the exact rate at the moment of the trade. This may differ slightly from the actual rate.
- Crypto deposits from external wallets are valued at market rate on the date received. If you transferred from another exchange where you bought at a different price, the cost basis will be wrong — you would need to adjust manually.
- Does not handle transfers between exchanges (no way to automatically link deposit on Kraken to purchase on another exchange).
- Does not handle airdrops, hard forks, or DeFi transactions outside Kraken.
- Does not carry forward losses from previous tax years.
- Does not handle the remittance basis or any non-standard tax situations.
- FX rates for less common altcoins may not be available on Kraken — the app will warn you when this happens.
- The gov.uk rate scraping may break if HMRC changes their website structure.

## Tech Stack

- WinUI 3 / Windows App SDK
- .NET 8
- QuestPDF (PDF export)
- ClosedXML (Excel export)
- DocumentFormat.OpenXml (Word export)

## License

This project has no licence. Use at your own risk.
