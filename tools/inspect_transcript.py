import json
import sys

path = sys.argv[1]
with open(path, "r", encoding="utf-8") as f:
    data = json.load(f)

segs = data.get("segments", [])
print("total segments:", len(segs))
if not segs:
    sys.exit(0)
print("duration_s:", round(segs[-1]["end_ms"] / 1000, 1))

print("\n--- 60s buckets (count / first text) ---")
last_ms = segs[-1]["end_ms"]
for start_s in range(0, int(last_ms / 1000) + 60, 60):
    bucket = [s for s in segs if start_s * 1000 <= s["start_ms"] < (start_s + 60) * 1000]
    if bucket:
        first = bucket[0]["text"].replace("\n", " ")[:42]
        print(f"{start_s // 60:3d}:{start_s % 60:02d}  n={len(bucket):2d}  {first}")

print("\n--- detail 2000s-2400s ---")
for s in segs:
    if 2000_000 <= s["start_ms"] <= 2400_000:
        print(f"{s['start_ms']/1000:8.1f}  {s['speaker'] or '-':6s}  {s['text'][:80]}")
