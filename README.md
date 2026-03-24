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
- Downloads your full trade history in batches (respecting API rate limits)
- Caches trade data locally so you don't need to re-download each time
- Resumes downloads from where it left off
- Calculates UK Capital Gains Tax for each tax year using HMRC rules:
  - **Same-day rule** - matches disposals with acquisitions on the same day
  - **Bed & breakfast rule (30-day rule)** - matches disposals with acquisitions within 30 days after
  - **Section 104 pooling** - average cost basis for remaining holdings
  - **Annual exempt amount** - applied per tax year at the correct historical rate
  - **CGT rate banding** - splits gains between basic and higher rate based on your taxable income
- Provides a tab for each tax year where you enter your taxable income and other capital gains
- Recalculates tax owed instantly when you change inputs
- Exports to Excel (.xlsx), PDF, and Word (.docx) with optional raw Kraken trade data included

## Tax Rates

The application includes hardcoded UK CGT rates for the following tax years:

| Tax Year | Annual Exempt Amount | Basic Rate | Higher Rate |
|----------|---------------------|------------|-------------|
| 2022/23 and earlier | 12,300 | 10% | 20% |
| 2023/24 | 6,000 | 10% | 20% |
| 2024/25 | 3,000 | 10% | 20% |
| 2025/26 onwards | 3,000 | 18% | 24% |

**These rates may be wrong or out of date. Verify them against official HMRC guidance before relying on any output.**

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
- `trades.json` - cached trade history
- `settings.json` - your API credentials and tax year inputs

**Your API credentials are stored in plain text on your local machine. Do not share this folder.**

## Known Limitations

- Non-GBP trading pairs (e.g. BTC/USD, ETH/USDT) use the quoted cost as an approximate GBP value. Historical FX conversion is not implemented.
- Crypto-to-crypto trades are treated as simultaneous disposal and acquisition but lack proper GBP valuation at the time of trade.
- Does not handle transfers between exchanges, airdrops, hard forks, staking rewards, DeFi transactions, or any activity outside Kraken.
- Does not carry forward losses from previous tax years.
- Does not handle the remittance basis or any non-standard tax situations.
- The gov.uk rate scraping may break if HMRC changes their website structure.

## Tech Stack

- WinUI 3 / Windows App SDK
- .NET 8
- QuestPDF (PDF export)
- ClosedXML (Excel export)
- DocumentFormat.OpenXml (Word export)

## License

This project has no licence. Use at your own risk.
