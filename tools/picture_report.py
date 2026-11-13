#!/usr/bin/env python3
"""Which indexed Unreal assets have a picture, and where it came from (saved in the asset file, rendered by us, none).

    picture_report.py <index root> [--packs N]

Kinds that never get a picture (levels, sounds, data tables...) are left out of the counts."""
import collections, json, os, sys

NO_PICTURE = ('World', 'Sound', 'Dialogue', 'Reverb', 'Attenuation', 'Concurrency', 'DataTable', 'CurveTable', 'Redirector', 'BuildData', 'HLOD')
root = sys.argv[1]
top = int(sys.argv[sys.argv.index('--packs') + 1]) if '--packs' in sys.argv else 25
by_class = collections.defaultdict(collections.Counter)
by_pack = {}
for n in sorted(os.listdir(root)):
    f = os.path.join(root, n, 'index.json')
    if not os.path.exists(f):
        continue
    d = json.load(open(f))
    if d.get('source') == 'raw':
        continue
    c = collections.Counter()
    for a in d['assets']:
        if any(s in a['class'] for s in NO_PICTURE):
            continue
        src = a.get('thumbsrc', 'rendered') if a.get('thumb') else 'none'
        by_class[a['class']][src] += 1
        c[src] += 1
    by_pack[n] = c
tot = collections.Counter()
for c in by_class.values():
    tot.update(c)
all_ = sum(tot.values()) or 1
print(f"{all_} assets that can have a picture: saved {tot['embedded']} ({tot['embedded'] / all_:.0%}), "
      f"ours {tot['rendered']} ({tot['rendered'] / all_:.0%}), none {tot['none']} ({tot['none'] / all_:.0%})")
print('\nby kind (saved / ours / none):')
for cls, c in sorted(by_class.items(), key=lambda x: -sum(x[1].values()))[:20]:
    print(f"  {cls:32} {c['embedded']:7} {c['rendered']:7} {c['none']:7}")
print(f'\npacks with the most assets without any picture (top {top}):')
for n, c in sorted(by_pack.items(), key=lambda x: -x[1]['none'])[:top]:
    if c['none']:
        print(f"  {c['none']:6} of {sum(c.values()):6}  {n}")
