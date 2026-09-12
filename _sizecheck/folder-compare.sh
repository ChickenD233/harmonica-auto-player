#!/usr/bin/env bash
# 对比：不裁剪 vs 裁剪后，发布目录里各程序集的实际大小变化。
set -o pipefail
cd "$(dirname "$0")/../HarpAutoPlayer"

export NUGET_PACKAGES="$PWD/../.tools/nuget"
export DOTNET_CLI_HOME="$PWD/../.tools/dotnet-home"
export DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1
export AVALONIA_TELEMETRY_OPTOUT=1
DOTNET="$PWD/../.tools/dotnet/dotnet"
[ -x "$DOTNET" ] || DOTNET=dotnet

ROOT="$PWD/../_sizecheck/out"
mkdir -p "$ROOT"

echo ">> 发布【不裁剪】目录版 ..."
rm -rf "$ROOT/FOLDER_raw"; mkdir -p "$ROOT/FOLDER_raw"
"$DOTNET" publish HarpAutoPlayer.csproj -c Release -r win-x64 --self-contained true \
  -o "$ROOT/FOLDER_raw" -p:DebugType=none > "$ROOT/FOLDER_raw.log" 2>&1 || echo "失败"

echo ">> 发布【部分裁剪】目录版 ..."
rm -rf "$ROOT/FOLDER_trim"; mkdir -p "$ROOT/FOLDER_trim"
"$DOTNET" publish HarpAutoPlayer.csproj -c Release -r win-x64 --self-contained true \
  -o "$ROOT/FOLDER_trim" -p:DebugType=none \
  -p:PublishTrimmed=true -p:TrimMode=partial > "$ROOT/FOLDER_trim.log" 2>&1 || echo "失败"

echo
echo "==================== 目录体积对比 ===================="
printf "%-46s %10s %10s\n" "程序集" "不裁剪" "裁剪后"
python3 - "$ROOT/FOLDER_raw" "$ROOT/FOLDER_trim" <<'PY'
import os, sys
raw, trim = sys.argv[1], sys.argv[2]
def sizes(d):
    out = {}
    for f in os.listdir(d):
        p = os.path.join(d, f)
        if os.path.isfile(p):
            out[f] = os.path.getsize(p)
    return out
r, t = sizes(raw), sizes(trim)
names = sorted(set(r) | set(t), key=lambda n: -(r.get(n, 0)))
print(f"{'合计':<46} {sum(r.values())/1048576:9.2f}M {sum(t.values())/1048576:9.2f}M")
print("-" * 70)
for n in names[:28]:
    a, b = r.get(n, 0), t.get(n, 0)
    if a < 200_000 and b < 200_000:
        continue
    print(f"{n:<46} {a/1048576:9.2f}M {b/1048576:9.2f}M")
print("-" * 70)
print("裁剪后仍存在但明显变小的（前 15）:")
shrunk = [(n, r[n], t.get(n, 0)) for n in r if n in t and r[n] - t[n] > 300_000]
for n, a, b in sorted(shrunk, key=lambda x: -(x[1]-x[2]))[:15]:
    print(f"  {n:<44} {a/1048576:8.2f}M -> {b/1048576:6.2f}M")
print("裁剪后完全消失的（前 20，按原大小）:")
gone = sorted([(n, r[n]) for n in r if n not in t], key=lambda x: -x[1])
for n, a in gone[:20]:
    print(f"  {n:<44} {a/1048576:8.2f}M")
print(f"  ... 共消失 {len(gone)} 个文件")
PY
