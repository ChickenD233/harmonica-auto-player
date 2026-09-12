#!/usr/bin/env bash
# 回答「软件本体还能不能更小」：测这个技术栈的真实下限。
#   1) 文件夹版（自包含 + 裁剪）→ 看清下载包里 15.5 MiB 到底由什么组成
#   2) 框架依赖（不打包 .NET 运行时）→ 该技术栈的下载下限，代价是用户要先装 .NET 8 桌面运行时
set -o pipefail
cd "$(dirname "$0")/../HarpAutoPlayer"

export NUGET_PACKAGES="$PWD/../.tools/nuget"
export DOTNET_CLI_HOME="$PWD/../.tools/dotnet-home"
export DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1 AVALONIA_TELEMETRY_OPTOUT=1
DOTNET="$PWD/../.tools/dotnet/dotnet"
[ -x "$DOTNET" ] || DOTNET=dotnet

ROOT="$PWD/../_sizecheck/out"
mkdir -p "$ROOT"

TRIM=(-p:PublishTrimmed=true -p:TrimMode=partial -p:DebugType=none)

echo "########## 1. 文件夹版：自包含 + 裁剪（拆解成分）##########"
OUT="$ROOT/FLOOR_folder"
rm -rf "$OUT"
if "$DOTNET" publish HarpAutoPlayer.csproj -c Release -r win-x64 --self-contained true \
     -p:PublishSingleFile=false "${TRIM[@]}" -o "$OUT" > "$ROOT/FLOOR_folder.log" 2>&1; then
  python3 "$ROOT/../categorize.py" "$OUT"
else
  echo "发布失败"; grep -m3 error "$ROOT/FLOOR_folder.log"
fi

echo
echo "########## 2. 框架依赖 + 单文件 + 压缩 + 裁剪 ##########"
OUT2="$ROOT/FLOOR_framework_dep"
rm -rf "$OUT2"
if "$DOTNET" publish HarpAutoPlayer.csproj -c Release -r win-x64 --self-contained false \
     -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true \
     -p:EnableCompressionInSingleFile=true "${TRIM[@]}" \
     -o "$OUT2" > "$ROOT/FLOOR_framework_dep.log" 2>&1; then
  exe="$OUT2/HarpAutoPlayer.exe"
  rm -f "$ROOT/_fd.zip"
  ( cd "$OUT2" && zip -q -r "$ROOT/_fd.zip" . )
  python3 - "$exe" "$ROOT/_fd.zip" <<'PY'
import os, sys
for label, p in (("框架依赖 exe", sys.argv[1]), ("框架依赖 zip", sys.argv[2])):
    print(f"{label}: {os.path.getsize(p)/1048576:.2f} MiB")
PY
  rm -f "$ROOT/_fd.zip"
else
  echo "发布失败"; grep -m3 error "$ROOT/FLOOR_framework_dep.log"
fi
