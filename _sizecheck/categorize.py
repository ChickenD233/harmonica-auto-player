#!/usr/bin/env python3
"""按类别汇总一个发布目录的体积，用来回答「这些 MB 到底是什么」。"""
import os
import sys
from collections import defaultdict

RULES = [
    ("你自己的代码", ("harpautoplayer",)),
    ("调试/诊断原生库（可删）", ("mscordaccore", "mscordbi", "diasymreader", "createdump")),
    (".NET 运行时原生（引擎）", ("coreclr", "clrjit", "hostfxr", "hostpolicy", "libcoreclr")),
    ("Avalonia UI 框架", ("avalonia",)),
    ("图形/文本原生栈", ("skiasharp", "libskia", "libharfbuzz", "av_libglesv2", "harfbuzz")),
    ("MIDI 解析 DryWetMidi", ("drywetmidi", "melanchall")),
    (".NET 基础类库 BCL", ("system.", "microsoft.", "netstandard", "mscorlib")),
]


def bucket(name):
    low = name.lower()
    for label, keys in RULES:
        if any(k in low for k in keys):
            return label
    return "其他"


def main(root):
    totals = defaultdict(int)
    counts = defaultdict(int)
    total = 0
    for dirpath, _dirnames, filenames in os.walk(root):
        for fn in filenames:
            size = os.path.getsize(os.path.join(dirpath, fn))
            b = bucket(fn)
            totals[b] += size
            counts[b] += 1
            total += size
    if not total:
        print("目录为空")
        return
    print(f"发布目录总大小: {total/1048576:.2f} MiB（{total} B）\n")
    print(f"{'类别':<26}{'文件数':>6}{'MiB':>10}{'占比':>9}")
    print("-" * 53)
    for label, size in sorted(totals.items(), key=lambda kv: -kv[1]):
        print(f"{label:<26}{counts[label]:>6}{size/1048576:>10.2f}{size/total*100:>8.1f}%")


if __name__ == "__main__":
    main(sys.argv[1])
