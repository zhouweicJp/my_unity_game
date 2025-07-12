import os
import re
import json

UNIT_DIR = os.path.join('Assets', 'RTS Engine', 'Demo', 'UnitExtension', 'Resources', 'Prefabs')
BUILDING_DIR = os.path.join('Assets', 'RTS Engine', 'Demo', 'BuildingExtension', 'Resources', 'Prefabs')

pattern_health = re.compile(r"maxHealth:\s*(\d+)")
pattern_initial = re.compile(r"initialHealth:\s*(\d+)")
pattern_damage = re.compile(r"damage:\s*\n\s*enabled:\s*1\s*\n\s*data:\s*\n\s*unit:\s*(\d+)\s*\n\s*building:\s*(\d+)")


def parse_prefab(path):
    with open(path, 'r') as f:
        content = f.read()
    stats = {}
    m = pattern_health.search(content)
    if m:
        stats['maxHealth'] = int(m.group(1))
    m = pattern_initial.search(content)
    if m:
        stats['initialHealth'] = int(m.group(1))
    m = pattern_damage.search(content)
    if m:
        stats['attackDamageUnit'] = int(m.group(1))
        stats['attackDamageBuilding'] = int(m.group(2))
    return stats

def gather_entities(directory):
    entities = {}
    for fname in os.listdir(directory):
        if not fname.endswith('.prefab'):
            continue
        path = os.path.join(directory, fname)
        stats = parse_prefab(path)
        if stats:
            entities[fname.replace('.prefab','')] = {'prefab': path, **stats}
    return entities

def main():
    data = {
        'units': gather_entities(UNIT_DIR),
        'buildings': gather_entities(BUILDING_DIR)
    }
    with open('Tools/RTS_EntityStats.json', 'w') as f:
        json.dump(data, f, indent=2)

if __name__ == '__main__':
    main()
