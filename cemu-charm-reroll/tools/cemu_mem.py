"""Cemu のエミュレート中メモリ (Wii U のアドレス空間) を読み取り専用で読む小道具と、最小限の PowerPC 逆アセンブラ。

Cemu は Wii U のメモリをホストの連続領域に置き、ログに「Init Wii U memory space (base: 0x...)」と出す。
Wii U のアドレス A はホストの base + A。中身はビッグエンディアンのまま。
"""
import ctypes
import re
import struct
import subprocess
from ctypes import wintypes

PROCESS_VM_READ = 0x0010
PROCESS_QUERY_INFORMATION = 0x0400
_k32 = ctypes.WinDLL('kernel32', use_last_error=True)
_k32.OpenProcess.restype = wintypes.HANDLE
_k32.ReadProcessMemory.argtypes = [wintypes.HANDLE, ctypes.c_void_p, ctypes.c_void_p, ctypes.c_size_t, ctypes.POINTER(ctypes.c_size_t)]


def cemu_pid():
    out = subprocess.run(['tasklist', '/FI', 'IMAGENAME eq Cemu.exe', '/FO', 'CSV', '/NH'], capture_output=True, text=True).stdout
    match = re.search(r'"Cemu\.exe","(\d+)"', out)
    if not match:
        raise SystemExit('Cemu が起動していない')
    return int(match.group(1))


def memory_base(log_path):
    text = open(log_path, encoding='utf-8', errors='replace').read()
    matches = re.findall(r'Init Wii U memory space \(base: (0x[0-9a-fA-F]+)\)', text)
    if not matches:
        raise SystemExit('ログにメモリの位置が無い')
    return int(matches[-1], 16)


class Guest:
    def __init__(self, pid, base):
        self.base = base
        self.handle = _k32.OpenProcess(PROCESS_VM_READ | PROCESS_QUERY_INFORMATION, False, pid)
        if not self.handle:
            raise SystemExit(f'OpenProcess 失敗 {ctypes.get_last_error()}')

    def read(self, address, size):
        buffer = ctypes.create_string_buffer(size)
        done = ctypes.c_size_t()
        ok = _k32.ReadProcessMemory(self.handle, ctypes.c_void_p(self.base + address), buffer, size, ctypes.byref(done))
        if not ok:
            return None
        return buffer.raw[:done.value]

    def u32(self, address):
        data = self.read(address, 4)
        return None if data is None else struct.unpack('>I', data)[0]


def _signed16(value):
    return value - 0x10000 if value & 0x8000 else value


def disasm(word, address):
    """よく出る命令だけ読む (分からないものは .word)。"""
    op = word >> 26
    rd, ra, rb = (word >> 21) & 31, (word >> 16) & 31, (word >> 11) & 31
    imm = word & 0xFFFF
    simm = _signed16(imm)
    if op == 7:
        return f'mulli r{rd}, r{ra}, {simm}'
    if op == 14:
        return f'li r{rd}, {simm}' if ra == 0 else f'addi r{rd}, r{ra}, {simm}'
    if op == 15:
        return f'lis r{rd}, 0x{imm:x}' if ra == 0 else f'addis r{rd}, r{ra}, 0x{imm:x}'
    if op == 24:
        return f'ori r{ra}, r{rd}, 0x{imm:x}' if word != 0x60000000 else 'nop'
    if op == 25:
        return f'oris r{ra}, r{rd}, 0x{imm:x}'
    if op == 28:
        return f'andi. r{ra}, r{rd}, 0x{imm:x}'
    if op == 10:
        return f'cmplwi cr{rd >> 2}, r{ra}, {imm}'
    if op == 11:
        return f'cmpwi cr{rd >> 2}, r{ra}, {simm}'
    if op in (32, 33, 34, 35, 40, 41, 42, 44, 45, 36, 37, 38, 39):
        names = {32: 'lwz', 33: 'lwzu', 34: 'lbz', 35: 'lbzu', 40: 'lhz', 41: 'lhzu', 42: 'lha', 44: 'sth', 45: 'sthu',
                 36: 'stw', 37: 'stwu', 38: 'stb', 39: 'stbu'}
        return f'{names[op]} r{rd}, {simm}(r{ra})'
    if op == 18:
        li = word & 0x03FFFFFC
        if li & 0x02000000:
            li -= 0x04000000
        target = (li if word & 2 else address + li) & 0xFFFFFFFF
        return f'b{"l" if word & 1 else ""} 0x{target:08x}'
    if op == 16:
        bd = _signed16(word & 0xFFFC)
        return f'bc {rd},{ra}, 0x{(address + bd) & 0xFFFFFFFF:08x}'
    if op == 19:
        xo = (word >> 1) & 0x3FF
        if xo == 16:
            return 'blr' if word == 0x4E800020 else f'bclr {rd},{ra}'
        if xo == 528:
            return 'bctr' if (word & 1) == 0 else 'bctrl'
    if op == 21:
        sh, mb, me = rb, (word >> 6) & 31, (word >> 1) & 31
        return f'rlwinm r{ra}, r{rd}, {sh}, {mb}, {me}'
    if op == 31:
        xo = (word >> 1) & 0x3FF
        names = {459: 'divwu', 491: 'divw', 235: 'mullw', 40: 'subf', 266: 'add', 11: 'mulhwu', 75: 'mulhw', 8: 'subfc',
                 444: 'or', 28: 'and', 316: 'xor', 24: 'slw', 536: 'srw', 792: 'sraw'}
        if xo in names:
            if xo in (444, 28, 316, 24, 536, 792):
                return f'{names[xo]} r{ra}, r{rd}, r{rb}'
            return f'{names[xo]} r{rd}, r{ra}, r{rb}'
        if xo == 0:
            return f'cmpw cr{rd >> 2}, r{ra}, r{rb}'
        if xo == 32:
            return f'cmplw cr{rd >> 2}, r{ra}, r{rb}'
        if xo == 339:
            spr = ((word >> 16) & 31) | (((word >> 11) & 31) << 5)
            return f'mfspr r{rd}, {spr}'
        if xo == 467:
            spr = ((word >> 16) & 31) | (((word >> 11) & 31) << 5)
            return f'mtspr {spr}, r{rd}'
        if xo == 371:
            return f'mftb r{rd}'
        if xo == 824:
            return f'srawi r{ra}, r{rd}, {rb}'
        if xo == 922:
            return f'extsh r{ra}, r{rd}'
    return f'.word 0x{word:08x}'
