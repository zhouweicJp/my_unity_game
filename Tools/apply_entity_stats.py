import os
import re
import json

STATS_FILE = os.path.join('Tools', 'RTS_EntityStats.json')
pattern_health = re.compile(r"maxHealth:\s*\d+")
pattern_initial = re.compile(r"initialHealth:\s*\d+")
pattern_damage = re.compile(r"(damage:\s*\n\s*enabled:\s*1\s*\n\s*data:\s*\n\s*unit:\s*)(\d+)(\s*\n\s*building:\s*)(\d+)")

def apply_stats(path, stats):
    with open(path, 'r') as f:
        content = f.read()
    if 'maxHealth' in stats:
        content = re.sub(pattern_health, f"maxHealth: {stats['maxHealth']}", content)
    if 'initialHealth' in stats:
        content = re.sub(pattern_initial, f"initialHealth: {stats['initialHealth']}", content)
    if 'attackDamageUnit' in stats and 'attackDamageBuilding' in stats:
        content = re.sub(pattern_damage,
                        lambda m: f"{m.group(1)}{stats['attackDamageUnit']}{m.group(3)}{stats['attackDamageBuilding']}",
                        content)
    with open(path, 'w') as f:
        f.write(content)

def main():
    with open(STATS_FILE, 'r') as f:
        data = json.load(f)
    for group in ('units', 'buildings'):
        if group in data:
            for name, entry in data[group].items():
                path = entry.get('prefab')
                if path and os.path.exists(path):
                    apply_stats(path, entry)
                    print(f"Updated {path}")

if __name__ == '__main__':
    main()
