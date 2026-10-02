"""Reads the dumpmenus export and plans a menu per venue archetype.

A venue's menu is capped by its level (MenuLimit), so plans are produced as a ranked list
long enough to cover every cap in use; you take the top N for the venue you are standing in.
"""
import sys
from collections import defaultdict
from pathlib import Path

ARGS = [a for a in sys.argv[1:] if not a.startswith("--")]
# Redirecting stdout on Windows PowerShell yields UTF-16, so the report writes its own file.
OUT = next((a.split("=", 1)[1] for a in sys.argv if a.startswith("--out=")), None)
SRC = Path(ARGS[0] if ARGS else
           r"C:\Program Files (x86)\Steam\steamapps\common\Nivalis Nights\BepInEx\nivalis-menus.txt")
DEPTH = 20         # the highest MenuLimit seen in the save
LAM = 0.1          # score cost accepted per newly required ingredient
COMPACT = "--compact" in sys.argv


def kv(field):
    return field.split("=", 1)[1] if "=" in field else field


def parse(path):
    venues, cur = [], None
    for line in path.read_text(encoding="utf-8").splitlines():
        f = line.split("\t")
        if line.startswith("OWNED\t"):
            cur = {"name": f[1].replace("Venue_", ""), "loc": kv(f[2]), "tier": kv(f[3]),
                   "types": [t for t in kv(f[4]).split(",") if t],
                   "demos": [], "prefs": "", "cands": [], "limit": None,
                   "fridge": None, "cupboard": None}
            venues.append(cur)
        elif cur is None:
            continue
        elif line.startswith("  RUNTIME\t") and len(f) > 2:
            cur["limit"] = int(kv(f[2])) if kv(f[2]).isdigit() else None
        elif line.startswith("  FRIDGE\t") and len(f) > 5:
            cur["fridge"] = (int(kv(f[4])), int(kv(f[2])))
        elif line.startswith("  CUPBOARD\t") and len(f) > 5:
            cur["cupboard"] = (int(kv(f[4])), int(kv(f[2])))
        elif line.startswith("  DEMO\t"):
            cur["demos"].append((f[1], float(kv(f[2])), float(kv(f[3]))))
        elif line.startswith("  LOCALPREF\t"):
            cur["prefs"] = f[1] if len(f) > 1 else ""
        elif line.startswith("  CAND\t") and f[1] != "RECIPE" and f[7] != "n/a":
            cur["cands"].append({
                "name": f[1], "type": f[2], "price": float(f[3]), "excl": float(f[5]),
                "score": float(f[7]), "ings": [i for i in f[8].split(";") if i]})
    return venues


def rank(cands, types, depth, lam):
    """Covers each allowed category once, then fills on score minus an ingredient penalty."""
    chosen, pantry, used = [], set(), set()

    for t in types:
        pool = [c for c in cands if c["type"] == t and c["name"] not in used]
        if not pool or len(chosen) >= depth:
            continue
        best = max(pool, key=lambda c: (c["score"], -len(set(c["ings"]) - pantry)))
        chosen.append(best)
        used.add(best["name"])
        pantry |= set(best["ings"])

    while len(chosen) < depth:
        pool = [c for c in cands if c["name"] not in used]
        if not pool:
            break
        best = max(pool, key=lambda c: c["score"] - lam * len(set(c["ings"]) - pantry))
        chosen.append(best)
        used.add(best["name"])
        pantry |= set(best["ings"])

    return chosen


def main():
    venues = parse(SRC)
    playable = [v for v in venues if v["cands"]]

    print(f"owned venues {len(venues)}, with candidates {len(playable)}")
    limits = defaultdict(int)
    for v in venues:
        limits[v["limit"]] += 1
    print("menu limit -> venue count: " +
          ", ".join(f"{k}:{limits[k]}" for k in sorted(limits, key=lambda x: (x is None, x))))

    tight = [v for v in venues if v["fridge"] and v["fridge"][1] and
             v["fridge"][0] / v["fridge"][1] > 0.5]
    print(f"\nvenues over half-full on fridge volume: {len(tight)}")
    for v in sorted(tight, key=lambda v: -v["fridge"][0] / v["fridge"][1]):
        fu, fc = v["fridge"]
        cu, cc = v["cupboard"]
        print(f"  {v['name']:<42} menu={v['limit']:<3} fridge {fu}/{fc}  cupboard {cu}/{cc}")

    byloc = defaultdict(list)
    for v in playable:
        byloc[v["loc"]].append(v)

    for loc in sorted(byloc):
        group = byloc[loc]
        demo = " ".join(f"{n}:{r}(pref {p})" for n, r, p in group[0]["demos"])
        print(f"\n{'=' * 100}\n{loc}   {demo}\n  local tags: {group[0]['prefs']}")
        pantry_all = set()
        for v in sorted(group, key=lambda v: v["name"]):
            depth = min(v["limit"] or DEPTH, DEPTH)
            menu = rank(v["cands"], v["types"], depth, LAM)
            menu.sort(key=lambda c: -c["score"])
            pantry = set()
            for d in menu:
                pantry |= set(d["ings"])
            pantry_all |= pantry
            avg = sum(d["score"] for d in menu) / len(menu)
            if COMPACT:
                print(f"  {v['name']} ({v['limit']}): "
                      f"{' · '.join(d['name'] for d in menu)}"
                      f"  — {len(pantry)} ingredients, score {avg:.3f}")
                continue
            print(f"\n  {v['name']}  (limit {v['limit']}, allows {','.join(v['types'])})"
                  f"  avg score {avg:.3f}, {len(pantry)} ingredients")
            for d in menu:
                print(f"    {d['score']:.3f} {d['type']:<9} {d['name']:<38} "
                      f"{int(d['price']):>5}  excl {d['excl']:.2f}")
            print(f"    needs: {', '.join(sorted(pantry))}")
        print(f"\n  {loc} combined shopping list ({len(pantry_all)} items): "
              f"{', '.join(sorted(pantry_all))}")


if __name__ == "__main__":
    if OUT:
        import io
        buf = io.StringIO()
        real, sys.stdout = sys.stdout, buf
        try:
            main()
        finally:
            sys.stdout = real
        Path(OUT).write_text(buf.getvalue(), encoding="utf-8")
        print(f"wrote {OUT}")
    else:
        main()
