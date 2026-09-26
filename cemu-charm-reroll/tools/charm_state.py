"""遊んでいる最中の Cemu のメモリを読み取り専用で読み、お守りの乱数が今どのテーブルに乗っているかと、
パッチがこの起動で選び直した回数・直近に選んだ値を表示する。

使い方: python charm_state.py [--log <Cemu の log.txt>]
"""
import argparse
import os
import re
import struct
import sys

sys.path.insert(0, os.path.dirname(__file__))
from cemu_mem import Guest, cemu_pid, memory_base

MODULUS, MULTIPLIER = 65363, 176
# テーブル番号と、その周期に含まれる代表の値 (MH3G スキルシミュレーターと同じ番号)
TABLE_SEEDS = [1, 15, 5, 13, 4, 3, 9, 12, 26, 18, 163, 401, 6, 2, 489, 802, 1203]
CURSED_TABLES = {11, 12, 15, 16, 17}
RNG_POINTER = 0x10298CE0
CHARM_CHANNEL = 1
PATCH_GROUP = 'MH3G_CharmTableReroll_JP_HD'
LOG_SIZE = 8  # パッチが覚えている直近の値の数
DEFAULT_LOG = os.path.expandvars(r'%APPDATA%\Cemu\log.txt')


def table_map():
    """乱数の値 → テーブル番号。"""
    tables = {}
    for table, seed in enumerate(TABLE_SEEDS, 1):
        value = seed
        while True:
            tables[value] = table
            value = value * MULTIPLIER % MODULUS
            if value == seed:
                break
    return tables


def codecave_base(log_path):
    """パッチの作業領域の先頭 (ログの "Applying patch group '...' (Codecave: xxxxxxxx-...)")。当たっていなければ None。"""
    text = open(log_path, encoding='utf-8', errors='replace').read()
    matches = re.findall(rf"Applying patch group '{PATCH_GROUP}' \(Codecave: ([0-9a-fA-F]+)-", text)
    return int(matches[-1], 16) if matches else None


def read_patch_state(guest, base):
    """(選び直した回数, 直近の値, 直近の値の一覧 (新しい順))。"""
    count, last = struct.unpack('>2I', guest.read(base, 8))
    ring = struct.unpack(f'>{LOG_SIZE}I', guest.read(base + 8, 4 * LOG_SIZE))
    recent = [ring[(count - 1 - i) % LOG_SIZE] for i in range(min(count, LOG_SIZE))]
    return count, last, recent


def describe_table(table):
    return f'テーブル {table}{" (呪われたテーブル)" if table in CURSED_TABLES else ""}'


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument('--log', default=DEFAULT_LOG, help='Cemu の log.txt')
    args = parser.parse_args()

    guest = Guest(cemu_pid(), memory_base(args.log))
    tables = table_map()
    rng = guest.u32(RNG_POINTER)
    channels = struct.unpack('>4H', guest.read(rng + 0x40, 8))
    value = channels[CHARM_CHANNEL] or 1
    print(f'お守りの乱数: {channels[CHARM_CHANNEL]} → {describe_table(tables[value])}')

    base = codecave_base(args.log)
    if base is None:
        print('パッチ: 当たっていません (ログに適用の記録がありません)')
        return
    count, last, recent = read_patch_state(guest, base)
    if not count:
        print('パッチ: まだ選び直していません (起動してからお守りが 1 つも作られていません)')
        return
    print(f'パッチ: この起動で {count} 回選び直しました。直近に選んだ値 {last} → {describe_table(tables[last])}')
    print('直近に選んだ値 (新しい順): ' + ', '.join(f'{v} (テーブル {tables[v]})' for v in recent))


if __name__ == '__main__':
    main()
