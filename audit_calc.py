"""Independent CGT calculation audit script.
Reads raw ledger data and FX rates, replicates the app's calculation pipeline,
and reports detailed results for verification."""

import json, os, sys
from datetime import datetime, timezone, timedelta
from collections import defaultdict

DATA_DIR = 'C:/Users/passp/AppData/Local/Packages/c307954b-7401-4c0a-86b3-725724f09e64_xxsxd2qzh77rr/LocalCache/Local/CryptoTax2026/'

with open(os.path.join(DATA_DIR, 'ledger.json')) as f:
    ledger = json.load(f)
with open(os.path.join(DATA_DIR, 'trades.json')) as f:
    trades_raw = json.load(f)

# Load FX rates
fx_dir = os.path.join(DATA_DIR, 'fx_cache')
fx_rates = {}
for fn in os.listdir(fx_dir):
    if fn.endswith('.json') and fn not in ('pairmap.json', 'manual_overrides.json'):
        with open(os.path.join(fx_dir, fn)) as f:
            fx_rates[fn.replace('.json','')] = json.load(f)

with open(os.path.join(fx_dir, 'pairmap.json')) as f:
    pairmap = json.load(f)

manual_overrides = {}
ovr_path = os.path.join(fx_dir, 'manual_overrides.json')
if os.path.exists(ovr_path):
    with open(ovr_path) as f:
        manual_overrides = json.load(f)

def get_rate(pair_data, timestamp):
    ts = int(timestamp)
    best = None
    for k in sorted(pair_data.keys(), key=lambda x: int(x)):
        if int(k) <= ts:
            best = float(pair_data[k])
        elif best is not None:
            break
    return best if best else float(pair_data[sorted(pair_data.keys(), key=lambda x: int(x))[0]])

def convert_to_gbp(amount, asset, timestamp):
    if asset == 'GBP': return amount
    if amount == 0: return 0.0

    # Check manual overrides first
    if asset in manual_overrides:
        ts = str(int(timestamp))
        if ts in manual_overrides[asset]:
            return amount * float(manual_overrides[asset][ts])
        # Find closest override
        best_ts = None
        for k in manual_overrides[asset]:
            if int(k) <= int(timestamp):
                best_ts = k
        if best_ts:
            return amount * float(manual_overrides[asset][best_ts])

    key = asset + "|GBP"
    if key in pairmap:
        pm = pairmap[key]
        ck = pm['cacheKey']
        inv = pm.get('invert', False)
        if ck in fx_rates:
            rate = get_rate(fx_rates[ck], timestamp)
            return (amount / rate) if inv else (amount * rate)

    # Try USD route
    usd_key = asset + "|USD"
    if usd_key in pairmap:
        pm = pairmap[usd_key]
        ck = pm['cacheKey']
        inv = pm.get('invert', False)
        if ck in fx_rates:
            usd_rate = get_rate(fx_rates[ck], timestamp)
            usd_amt = (amount / usd_rate) if inv else (amount * usd_rate)
            return convert_to_gbp(usd_amt, 'USD', timestamp)

    print(f"  WARNING: No rate for {asset} at ts={int(timestamp)}", file=sys.stderr)
    return amount  # fallback

FIAT = {'GBP', 'USD', 'EUR', 'JPY', 'CAD', 'AUD', 'CHF'}
STABLECOINS = {'USDT', 'USDC', 'DAI'}
STAKING_SUBTYPES = {
    'spotfromstaking','stakingfromspot','spottostaking','stakingtospot',
    'spotfromfutures','futuresfromspot','spottofutures','futurestospot',
    'spotfromearnflex','earnflexfromspot','spottoearnflex','earnflextospotspot',
    'earnfromspot','spotfromearn','spottoearn','earntospot',
    'spotfrombonding','bondingfromspot','spottobonding','bondingtospot',
    'autoallocation',
}

K_DELIST = datetime(2025, 7, 14, 23, 16, 56, tzinfo=timezone.utc).timestamp()

# Filter post-delisting K entries
eff = [e for e in ledger if not (e['NormalisedAsset'] == 'K' and e['time'] > K_DELIST)]
print(f"Effective ledger: {len(eff)} entries (filtered {len(ledger)-len(eff)} post-delisting)")

# ===== BUILD CGT EVENTS =====
events = []

trade_types = {'trade', 'spend', 'receive', 'conversion', 'adjustment', 'sale'}
trade_entries = [e for e in eff if e['type'] in trade_types
    and e.get('subtype','').lower() not in STAKING_SUBTYPES]

by_refid = defaultdict(list)
for e in trade_entries:
    by_refid[e['refid']].append(e)

skipped_single = 0
for refid, entries in by_refid.items():
    entries.sort(key=lambda x: x['time'])
    distinct = set(e['NormalisedAsset'] for e in entries)
    if len(distinct) == 1:
        continue

    received = [e for e in entries if float(e['amount']) > 0]
    spent = [e for e in entries if float(e['amount']) < 0]
    if not received or not spent:
        skipped_single += 1
        continue

    date = entries[0]['time']

    # tradeGbpValue with CORRECTED fee direction
    trade_gbp = 0
    has_fiat = False

    gbp_e = [e for e in entries if e['NormalisedAsset'] == 'GBP']
    if gbp_e:
        for e in gbp_e:
            a, f = float(e['amount']), float(e['fee'])
            trade_gbp += (a - f) if a > 0 else (abs(a) + f)
        has_fiat = True

    if not has_fiat:
        fiat_e = [e for e in entries if e['NormalisedAsset'] in FIAT]
        if fiat_e:
            for e in fiat_e:
                a, f = float(e['amount']), float(e['fee'])
                val = (a - f) if a > 0 else (abs(a) + f)
                trade_gbp += convert_to_gbp(val, e['NormalisedAsset'], date)
            has_fiat = True

    if not has_fiat:
        stable_e = [e for e in entries if e['NormalisedAsset'] in STABLECOINS]
        if stable_e:
            for e in stable_e:
                a, f = float(e['amount']), float(e['fee'])
                val = (a - f) if a > 0 else (abs(a) + f)
                trade_gbp += convert_to_gbp(val, e['NormalisedAsset'], date)
            has_fiat = True

    if not has_fiat:
        for s in spent:
            trade_gbp += convert_to_gbp(abs(float(s['amount'])), s['NormalisedAsset'], date)

    # Disposal events
    spent_crypto = [e for e in spent if e['NormalisedAsset'] not in FIAT and e['NormalisedAsset'] not in STABLECOINS]
    total_spent = sum(abs(float(e['amount'])) + float(e['fee']) for e in spent_crypto)
    for e in spent_crypto:
        g = abs(float(e['amount']))
        f = float(e['fee'])
        q = g + f
        p = q / total_spent if total_spent > 0 else 1
        events.append(dict(date=date, asset=e['NormalisedAsset'], is_acq=False,
            qty=q, fee=f, gbp=trade_gbp*p, refid=refid))

    # Acquisition events
    recv_crypto = [e for e in received if e['NormalisedAsset'] not in FIAT and e['NormalisedAsset'] not in STABLECOINS]
    total_recv = sum(float(e['amount']) for e in recv_crypto)
    for e in recv_crypto:
        g = float(e['amount'])
        f = float(e['fee'])
        q = g - f
        p = g / total_recv if total_recv > 0 else 1
        fee_gbp = convert_to_gbp(f, e['NormalisedAsset'], date) if f > 0 else 0
        events.append(dict(date=date, asset=e['NormalisedAsset'], is_acq=True,
            qty=q, fee=f, gbp=trade_gbp*p + fee_gbp, refid=refid))

    # Stablecoin events
    for e in entries:
        if e['NormalisedAsset'] in STABLECOINS:
            a, f = float(e['amount']), float(e['fee'])
            if a > 0:
                events.append(dict(date=date, asset=e['NormalisedAsset'], is_acq=True,
                    qty=a-f, fee=f, gbp=convert_to_gbp(a, e['NormalisedAsset'], date), refid=refid))
            elif a < 0:
                events.append(dict(date=date, asset=e['NormalisedAsset'], is_acq=False,
                    qty=abs(a)+f, fee=f, gbp=convert_to_gbp(abs(a), e['NormalisedAsset'], date), refid=refid))

# Deposits (non-fiat)
for e in eff:
    if e['type'] == 'deposit' and float(e['amount']) > 0 and e['NormalisedAsset'] not in FIAT:
        a = float(e['amount'])
        events.append(dict(date=e['time'], asset=e['NormalisedAsset'], is_acq=True,
            qty=a, fee=0, gbp=convert_to_gbp(a, e['NormalisedAsset'], e['time']), refid=e['refid']))

# Staking rewards
for e in eff:
    if e['type'] in ('staking','dividend','reward','airdrop','fork','mining') and float(e['amount']) > 0:
        net = float(e['amount']) - float(e['fee'])
        if net <= 0: continue
        gbp = convert_to_gbp(net, e['NormalisedAsset'], e['time'])
        events.append(dict(date=e['time'], asset=e['NormalisedAsset'], is_acq=True,
            qty=net, fee=0, gbp=gbp, refid=e['refid']))

# Delisting conversions
for e in eff:
    if e['type'] == 'transfer' and e.get('subtype','').lower() == 'delistingconversion' and e['NormalisedAsset'] not in FIAT:
        q = abs(float(e['amount']))
        gbp = convert_to_gbp(q, e['NormalisedAsset'], e['time'])
        events.append(dict(date=e['time'], asset=e['NormalisedAsset'], is_acq=float(e['amount'])>0,
            qty=q, fee=0, gbp=gbp, refid=e['refid']))

# K delisting disposal
k_hold = sum(ev['qty'] if ev['is_acq'] else -ev['qty'] for ev in events if ev['asset']=='K' and ev['date']<=K_DELIST)
if k_hold > 0:
    events.append(dict(date=K_DELIST, asset='K', is_acq=False, qty=k_hold, fee=0, gbp=0, refid='DELISTING-K'))
    print(f"K delisting: {k_hold:.4f} K disposed at GBP 0")

events.sort(key=lambda x: x['date'])
print(f"CGT events: {len(events)} ({sum(1 for e in events if not e['is_acq'])} disposals, {sum(1 for e in events if e['is_acq'])} acquisitions)")

# ===== MATCHING RULES =====
def to_date(ts):
    return datetime.fromtimestamp(ts, tz=timezone.utc).date()

def tax_year(ts):
    d = datetime.fromtimestamp(ts, tz=timezone.utc)
    y = d.year
    if d.month < 4 or (d.month == 4 and d.day <= 5):
        y -= 1
    return f"{y}/{(y+1)%100:02d}"

acq_rem = [{'ev': a, 'rem': a['qty']} for a in events if a['is_acq']]
acq_by_asset = defaultdict(list)
for ar in acq_rem:
    acq_by_asset[ar['ev']['asset']].append(ar)

disposals = []

for disp in sorted((e for e in events if not e['is_acq']), key=lambda x: x['date']):
    asset = disp['asset']
    rem = disp['qty']
    dd = to_date(disp['date'])
    aa = acq_by_asset.get(asset, [])

    # Same-day
    for ar in aa:
        if rem <= 0: break
        if to_date(ar['ev']['date']) == dd and ar['rem'] > 0:
            m = min(rem, ar['rem'])
            cp = (m/ar['ev']['qty'])*ar['ev']['gbp'] if ar['ev']['qty']>0 else 0
            pp = (m/disp['qty'])*disp['gbp'] if disp['qty']>0 else 0
            disposals.append(dict(asset=asset, date=disp['date'], qty=m,
                proceeds=pp, cost=cp, rule='SD', refid=disp['refid'], ty=tax_year(disp['date'])))
            ar['rem'] -= m; rem -= m

    # B&B
    if rem > 0:
        bnb = sorted([ar for ar in aa if to_date(ar['ev']['date'])>dd
            and (to_date(ar['ev']['date'])-dd).days<=30 and ar['rem']>0], key=lambda x:x['ev']['date'])
        for ar in bnb:
            if rem <= 0: break
            m = min(rem, ar['rem'])
            cp = (m/ar['ev']['qty'])*ar['ev']['gbp'] if ar['ev']['qty']>0 else 0
            pp = (m/disp['qty'])*disp['gbp'] if disp['qty']>0 else 0
            disposals.append(dict(asset=asset, date=disp['date'], qty=m,
                proceeds=pp, cost=cp, rule='BB', refid=disp['refid'], ty=tax_year(disp['date'])))
            ar['rem'] -= m; rem -= m

# Consumed map
consumed = {}
for ar in acq_rem:
    c = ar['ev']['qty'] - ar['rem']
    if c > 0: consumed[id(ar['ev'])] = c

matched = defaultdict(float)
for d in disposals:
    matched[(d['refid'], d['date'])] += d['qty']

# S104 pool
pools = {}
for evt in sorted(events, key=lambda x: (x['date'], 0 if x['is_acq'] else 1)):
    a = evt['asset']
    if a not in pools: pools[a] = [0.0, 0.0]
    pool = pools[a]

    if evt['is_acq']:
        c = consumed.get(id(evt), 0)
        pq = evt['qty'] - c
        if pq > 0:
            pc = (pq/evt['qty'])*evt['gbp'] if evt['qty']>0 else 0
            pool[0] += pq; pool[1] += pc
    else:
        al = matched.get((evt['refid'], evt['date']), 0)
        pq = evt['qty'] - al
        if pq > 0:
            if pool[0] > 0:
                actual = min(pq, pool[0])
                prop = actual / pool[0]
                cr = pool[1] * prop
                pool[0] -= actual; pool[1] -= cr
                if pool[0] < 0: pool[0] = 0
                if pool[1] < 0: pool[1] = 0
            else:
                cr = 0
            pp = (pq/evt['qty'])*evt['gbp'] if evt['qty']>0 else 0
            disposals.append(dict(asset=a, date=evt['date'], qty=pq,
                proceeds=pp, cost=cr, rule='S104', refid=evt['refid'], ty=tax_year(evt['date'])))

# ===== STAKING INCOME =====
staking_income = defaultdict(float)
for e in eff:
    if e['type'] in ('staking','dividend','reward','airdrop','fork','mining') and float(e['amount']) > 0:
        net = float(e['amount']) - float(e['fee'])
        if net <= 0: continue
        ty = tax_year(e['time'])
        gbp = convert_to_gbp(net, e['NormalisedAsset'], e['time'])
        staking_income[ty] += gbp

# ===== SUMMARIES =====
print("\n" + "="*60)
print("TAX YEAR SUMMARIES")
print("="*60)

ty_disp = defaultdict(list)
for d in disposals:
    ty_disp[d['ty']].append(d)

all_tys = sorted(set(list(ty_disp.keys()) + list(staking_income.keys())))
carried = 0

for ty in all_tys:
    yd = ty_disp.get(ty, [])
    tp = sum(d['proceeds'] for d in yd)
    tc = sum(d['cost'] for d in yd)
    gains = sum(d['proceeds']-d['cost'] for d in yd if d['proceeds']-d['cost']>0)
    losses = sum(d['proceeds']-d['cost'] for d in yd if d['proceeds']-d['cost']<0)
    net = gains + losses

    sy = int(ty.split('/')[0])
    aea = 3000 if sy >= 2023 else 6000 if sy == 2022 else 12300
    if sy == 2023: aea = 6000

    br, hr = (0.18, 0.24) if sy >= 2025 else (0.10, 0.20)
    income = 80000 if ty == '2025/26' else 0

    total_net = net
    lu = 0
    if total_net > aea and carried > 0:
        excess = total_net - aea
        lu = min(carried, excess)
        total_net -= lu

    taxable = max(0, total_net - aea)
    pa = 12570; bb = 37700
    iap = max(0, income - pa)
    ub = max(0, bb - iap)
    gb = min(taxable, ub)
    gh = taxable - gb
    cgt = gb*br + gh*hr

    print(f"\n--- {ty} ---")
    print(f"  Disposals:        {len(yd):>6d}")
    print(f"  Total Proceeds:   GBP {tp:>14,.2f}")
    print(f"  Total Costs:      GBP {tc:>14,.2f}")
    print(f"  Total Gains:      GBP {gains:>14,.2f}")
    print(f"  Total Losses:     GBP {losses:>14,.2f}")
    print(f"  Net Gain/Loss:    GBP {net:>14,.2f}")
    print(f"  Staking Income:   GBP {staking_income.get(ty,0):>14,.2f}")
    print(f"  Losses carried in:GBP {carried:>14,.2f}")
    print(f"  Losses used:      GBP {lu:>14,.2f}")
    print(f"  AEA:              GBP {aea:>14,.2f}")
    print(f"  Taxable Gain:     GBP {taxable:>14,.2f}")
    print(f"  CGT Due:          GBP {cgt:>14,.2f}")

    # By rule breakdown
    by_r = defaultdict(lambda: [0,0,0])
    for d in yd:
        r = by_r[d['rule']]
        r[0] += 1; r[1] += d['proceeds']; r[2] += d['cost']
    for rule in sorted(by_r):
        cnt, pr, co = by_r[rule]
        print(f"    {rule:4s}: {cnt:>4d} disposals, proceeds={pr:>12,.2f}, cost={co:>12,.2f}, gain={pr-co:>12,.2f}")

    carried -= lu
    if net < 0: carried += abs(net)
    print(f"  Losses carried out:GBP {carried:>14,.2f}")

# Asset breakdown for 2025/26
print("\n" + "="*60)
print("2025/26 ASSET BREAKDOWN (top 25 by |gain|)")
print("="*60)
ab = defaultdict(lambda: [0,0,0])
for d in ty_disp.get('2025/26', []):
    r = ab[d['asset']]
    r[0] += 1; r[1] += d['proceeds']; r[2] += d['cost']
for asset, (cnt, pr, co) in sorted(ab.items(), key=lambda x: abs(x[1][1]-x[1][2]), reverse=True)[:25]:
    print(f"  {asset:8s}: {cnt:>4d} disp, proceeds={pr:>12,.2f}, cost={co:>12,.2f}, gain={pr-co:>12,.2f}")

# Final pool state
print("\n" + "="*60)
print("FINAL S104 POOL STATE (non-zero)")
print("="*60)
for a in sorted(pools):
    q, c = pools[a]
    if q > 0.0001:
        cpu = c/q if q > 0 else 0
        print(f"  {a:8s}: qty={q:>14.8f}, cost=GBP {c:>12,.2f}, avg=GBP {cpu:>12,.4f}")
