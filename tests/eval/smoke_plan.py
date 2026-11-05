#!/usr/bin/env python3
"""Stream one plan run from a Haru API and print a timeline and the result.

Usage: python3 tests/eval/smoke_plan.py [--url http://localhost:5156] [--text TEXT | --file FILE] [--save FILE]
Without --text or --file, the server sample (GET /api/sample) is used.
The run fails (exit code 1) when the server returns an error or no plan.
--save writes the run as {"events": [{"t": ms, "event": name, "data": {...}}]} without agent_delta events.
The API replays this file as the recorded sample run (src/Haru.Api/Samples/sample-run.json).
"""
import argparse
import json
import sys
import time
import urllib.error
import urllib.request


def main():
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("--url", default="http://localhost:5156")
    parser.add_argument("--text")
    parser.add_argument("--file")
    parser.add_argument("--max-seconds", type=float, default=120.0, help="fail when the run is slower")
    parser.add_argument("--save", help="write the recorded events to this JSON file")
    args = parser.parse_args()

    if args.file:
        text = open(args.file, encoding="utf-8").read()
    elif args.text:
        text = args.text
    else:
        text = json.load(urllib.request.urlopen(args.url + "/api/sample", timeout=20))["inputText"]

    body = json.dumps({"text": text}, ensure_ascii=False).encode("utf-8")
    req = urllib.request.Request(args.url + "/api/plan", data=body, headers={"Content-Type": "application/json"}, method="POST")
    started = time.time()
    try:
        resp = urllib.request.urlopen(req, timeout=int(args.max_seconds) + 30)
    except urllib.error.HTTPError as e:
        print("HTTP", e.code, e.read().decode("utf-8"))
        return 1

    plan, error, event = None, None, None
    recorded = []
    for raw in resp:
        line = raw.decode("utf-8").rstrip("\n")
        if line.startswith("event:"):
            event = line[6:].strip()
        elif line.startswith("data:"):
            data = line[5:].strip()
            if event == "plan":
                plan = json.loads(data)
            elif event == "error":
                error = json.loads(data)
            if event != "agent_delta":
                recorded.append({"t": int((time.time() - started) * 1000), "event": event, "data": json.loads(data)})
                print(f"{time.time() - started:6.1f}s {event}: {data if len(data) < 200 else data[:200] + '…'}")

    elapsed = time.time() - started
    if error or not plan:
        print("FAILED:", error or "no plan event")
        return 1
    if args.save:
        with open(args.save, "w", encoding="utf-8") as f:
            json.dump({"events": recorded}, f, ensure_ascii=False, separators=(",", ":"))
        print(f"saved {len(recorded)} events to {args.save}")
    print("\nsummary:", plan["summary"])
    for b in plan["blocks"]:
        print(f'  {b["start"]}-{b["end"]} [{b["kind"]}] {b["title"]} - {b["reason"]}')
    for d in plan["deferred"]:
        print(f'  (deferred) {d["title"]} - {d["reason"]}')
    print("validation:", json.dumps(plan["validation"], ensure_ascii=False))
    print("meta:", json.dumps(plan["meta"], ensure_ascii=False))
    print(f"elapsed: {elapsed:.1f}s")
    ok = plan["validation"]["valid"] and elapsed <= args.max_seconds
    print("RESULT:", "PASS" if ok else "FAIL")
    return 0 if ok else 1


if __name__ == "__main__":
    sys.exit(main())
