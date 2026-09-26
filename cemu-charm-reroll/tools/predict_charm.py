"""パッチが選んだ値から、その直後に作られるお守りを、お守りの種類ごとに予測する。
お守りの作り方は MH3G スキルシミュレーターと同じで、データはこのリポジトリの charms.json を使う。

使い方: python predict_charm.py [値 ...]
        値を省くと、遊んでいる最中の Cemu のメモリから、パッチが直近に選んだ値 (最大 8 個、新しい順) を読む。
"""
import argparse
import json
import os
import sys
from collections import defaultdict

sys.path.insert(0, os.path.dirname(__file__))

DEFAULT_CHARMS = os.path.join(os.path.dirname(__file__), '..', '..', 'src', 'Mh3gSim.Core', 'Data', 'charms.json')


def load_model(path):
    data = json.load(open(path, encoding='utf-8'))
    lists = defaultdict(list)
    for skill in data['skills']:
        lists[(skill['kind'], skill['list'])].append(skill)
    kinds = []
    for kind in data['kinds']:
        name = kind['kind']
        kinds.append({
            'name': name,
            'threshold': kind['secondSkillThreshold'],
            'names': kind['names'],
            'slotRows': kind['slotRows'],
            'first': sorted(lists[(name, 'skill1')], key=lambda s: s['index']),
            'second': sorted(lists[(name, 'skill2')], key=lambda s: s['index']),
        })
    return data['rng']['multiplier'], data['rng']['modulus'], kinds


def generate(multiplier, modulus, kind, state):
    """乱数が state の時に作られるお守り (シミュレーターの CharmGenerator.Generate と同じ順番で乱数を引く)。"""
    def step(value):
        return multiplier * value % modulus

    state = step(state)
    first = kind['first'][state % len(kind['first'])]
    state = step(state)
    points1 = first['min'] + state % (first['max'] - first['min'] + 1)

    state = step(state)
    second, points2 = None, 0
    if kind['second'] and state % 100 >= kind['threshold']:
        state = step(state)
        second = kind['second'][state % len(kind['second'])]
        state = step(state)
        positive = state % 2 == 1
        state = step(state)
        points2 = 1 + state % second['max'] if positive else second['min'] + state % (1 - second['min'])
        if second['skill'] == first['skill'] or points2 == 0:
            second, points2 = None, 0

    # マイナスの第 2 スキルはスロット値に数えない。小数のまま足してから切り捨てる
    slot_value = int(points1 * 10.0 / first['max'] + (points2 * 10.0 / second['max'] if points2 > 0 else 0))
    state = step(state)
    roll = state % 100
    row = kind['slotRows'][min(max(slot_value, 1), len(kind['slotRows'])) - 1]
    slots = (3 if roll >= row[2] else 2) if roll >= row[1] else (1 if roll >= row[0] else 0)
    score = slot_value + 2 * slots
    name = next((n['name'] for n in kind['names'] if n.get('maxScore') is None or score <= n['maxScore']), '')
    return {'kind': kind['name'], 'name': name, 'skill1': first['skill'], 'points1': points1,
            'skill2': second['skill'] if second else '', 'points2': points2, 'slots': slots}


def describe(charm):
    second = f' {charm["skill2"]}{charm["points2"]:+d}' if charm['skill2'] else ''
    return f'{charm["kind"]} {charm["name"]} {charm["skill1"]}{charm["points1"]:+d}{second} スロット {charm["slots"]}'


def picked_values_from_cemu(log_path):
    from cemu_mem import Guest, cemu_pid, memory_base
    from charm_state import codecave_base, read_patch_state
    base = codecave_base(log_path)
    if base is None:
        raise SystemExit('パッチが当たっていません (ログに適用の記録がありません)')
    guest = Guest(cemu_pid(), memory_base(log_path))
    count, _, recent = read_patch_state(guest, base)
    if not count:
        raise SystemExit('まだ選び直していません (起動してからお守りが 1 つも作られていません)')
    return recent


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument('--charms', default=DEFAULT_CHARMS, help='charms.json (既定はこのリポジトリのもの)')
    parser.add_argument('--log', default=os.path.expandvars(r'%APPDATA%\Cemu\log.txt'), help='Cemu の log.txt')
    parser.add_argument('values', nargs='*', type=int, help='パッチが選んだ値 (省くと Cemu のメモリから読む)')
    args = parser.parse_args()

    multiplier, modulus, kinds = load_model(args.charms)
    values = args.values or picked_values_from_cemu(args.log)
    for value in values:
        print(f'選んだ値 {value} の直後に作られるお守り:')
        for kind in kinds:
            print('  ' + describe(generate(multiplier, modulus, kind, value)))


if __name__ == '__main__':
    main()
