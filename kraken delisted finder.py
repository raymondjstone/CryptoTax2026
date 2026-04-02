import requests
import json
import time
from datetime import datetime, UTC
from collections import defaultdict

WAYBACK_CDX = "http://web.archive.org/cdx/search/cdx"
KRAKEN_URL = "https://api.kraken.com/0/public/AssetPairs"

session = requests.Session()
session.headers.update({"User-Agent": "Mozilla/5.0"})

def get_snapshots():
    params = {
        "url": KRAKEN_URL,
        "output": "json",
        "fl": "timestamp",
        "collapse": "timestamp:6"
    }
    r = session.get(WAYBACK_CDX, params=params, timeout=30)
    r.raise_for_status()
    return [row[0] for row in r.json()[1:]]

def fetch_snapshot(ts):
    url = f"https://web.archive.org/web/{ts}/{KRAKEN_URL}"
    try:
        r = session.get(url, timeout=15)
        if r.status_code == 200:
            return r.json().get("result", {})
    except:
        return None
    return None

def parse_pairs(data):
    pairs = set()
    for v in data.values():
        if "altname" in v:
            pairs.add(v["altname"])
    return pairs

def main():
    print("Getting snapshots...")
    timestamps = get_snapshots()

    timeline = []  # [(date, set_of_pairs)]

    for i, ts in enumerate(timestamps):
        date = datetime.strptime(ts, "%Y%m%d%H%M%S").date()
        print(f"[{i+1}/{len(timestamps)}] {date}")

        data = fetch_snapshot(ts)
        if not data:
            continue

        pairs = parse_pairs(data)
        timeline.append((date, pairs))

        time.sleep(1)

    # sort timeline just in case
    timeline.sort(key=lambda x: x[0])

    # build per-pair timeline
    pair_history = defaultdict(list)

    for date, pairs in timeline:
        for p in pairs:
            pair_history[p].append(date)

    result = []

    for pair, dates in pair_history.items():
        dates = sorted(dates)

        periods = []
        events = []

        start = dates[0]
        prev = dates[0]

        for d in dates[1:]:
            gap = (d - prev).days

            if gap > 40:  # GAP THRESHOLD (monthly snapshots)
                # close period
                periods.append({
                    "start": start.isoformat(),
                    "end": prev.isoformat()
                })
                events.append({"type": "delisted", "date": prev.isoformat()})
                events.append({"type": "relisted", "date": d.isoformat()})
                start = d

            prev = d

        # close final period
        periods.append({
            "start": start.isoformat(),
            "end": prev.isoformat()
        })

        # determine status
        last_seen = dates[-1]
        today = datetime.now(UTC).date()
        status = "active" if (today - last_seen).days < 40 else "delisted"

        if status == "delisted":
            events.append({"type": "delisted", "date": last_seen.isoformat()})

        result.append({
            "altname": pair,
            "periods": periods,
            "events": events,
            "status": status,
            "lifetime_days": sum(
                (datetime.fromisoformat(p["end"]) - datetime.fromisoformat(p["start"])).days
                for p in periods
            )
        })

    output = {
        "generated_at": datetime.now(UTC).isoformat(),
        "pair_count": len(result),
        "pairs": result
    }

    with open("Assets\\kraken_pairs_events.json", "w") as f:
        json.dump(output, f, indent=2)

    print(f"\nDone. Analysed {len(result)} pairs.")

if __name__ == "__main__":
    main()