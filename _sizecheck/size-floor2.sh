#!/usr/bin/env bash
# 继续测「软件本体」的下限：
#   S1 = 现状 + 剔除调试原生库（自包含单文件压缩）
#   F1 = 框架依赖（不打包 .NET 运行时）+ 单文件 + 裁剪
#   F2 = 框架依赖 + 文件夹 + 裁剪
set -o pipefail
cd "$(dirname "$0")/../HarpAutoPlayer"

export NUGET_PACKAGES="$PWD/../.tools/nuget"
export DOTNET_CLI_HOME="$PWD/../.tools/dotnet-home"
export DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1 AVALONIA_TELEMETRY_OPTOUT=1
DOTNET="$PWD/../.tools/dotnet/dotnet"
[ -x "$DOTNET" ] || DOTNET=dotnet

ROOT="$PWD/../_sizecheck/out"
TARGETS="$PWD/../_sizecheck/strip-diagnostics.targets"
mkdir -p "$ROOT"
TRIM=(-p:PublishTrimmed=true -p:TrimMode=partial -p:DebugType=none)

weigh() {  # weigh <目录> <名称>
  local out="$1" name="$2" zip="$ROOT/_w.zip"
  rm -f "$zip"
  ( cd "$out" && zip -q -r "$zip" . )
  python3 - "$out" "$zip" "$name" <<'PY'
import os, sys
out, zip_, name = sys.argv[1], sys.argv[2], sys.argv[3]
exe = os.path.join(out, "HarpAutoPlayer.exe")
total = sum(os.path.getsize(os.path.join(d, f))
            for d, _, fs in os.walk(out) for f in fs)
print(f"{name}: 目录 {total/1048576:.2f} MiB | exe {os.path.getsize(exe)/1048576:.2f} MiB "
      f"| zip {os.path.getsize(zip_)/1048576:.2f} MiB" if os.path.exists(exe)
      else f"{name}: 目录 {total/1048576:.2f} MiB | zip {os.path.getsize(zip_)/1048576:.2f} MiB")
PY
  rm -f "$zip"
}

echo "########## S1 自包含 + 裁剪 + 剔除调试原生库 ##########"
OUT="$ROOT/FLOOR_s1_stripped"; rm -rf "$OUT"
if "$DOTNET" publish HarpAutoPlayer.csproj -c Release -r win-x64 --self-contained true \
     -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true \
     -p:EnableCompressionInSingleFile=true "${TRIM[@]}" \
     -p:CustomAfterMicrosoftCommonTargets="$TARGETS" \
     -o "$OUT" > "$ROOT/FLOOR_s1.log" 2>&1; then weigh "$OUT" "S1 自包含+删调试库"
else echo "S1 失败"; grep -m3 error "$ROOT/FLOOR_s1.log"; fi

echo
echo "########## F1 框架依赖 + 单文件 + 裁剪 ##########"
OUT="$ROOT/FLOOR_f1_fd_single"; rm -rf "$OUT"
if "$DOTNET" publish HarpAutoPlayer.csproj -c Release -r win-x64 --self-contained false \
     -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true "${TRIM[@]}" \
     -p:CustomAfterMicrosoftCommonTargets="$TARGETS" \
     -o "$OUT" > "$ROOT/FLOOR_f1.log" 2>&1; then weigh "$OUT" "F1 框架依赖单文件"
else echo "F1 失败"; grep -m3 error "$ROOT/FLOOR_f1.log"; fi

echo
echo "########## F2 框架依赖 + 文件夹 + 裁剪 ##########"
OUT="$ROOT/FLOOR_f2_fd_folder"; rm -rf "$OUT"
if "$DOTNET" publish HarpAutoPlayer.csproj -c Release -r win-x64 --self-contained false \
     -p:PublishSingleFile=false "${TRIM[@]}" \
     -p:CustomAfterMicrosoftCommonTargets="$TARGETS" \
     -o "$OUT" > "$ROOT/FLOOR_f2.log" 2>&1; then
  weigh "$OUT" "F2 框架依赖文件夹"
  echo "  F2 里最大的 8 个文件："
  find "$OUT" -type f -exec ls -l {} \; | awk '{printf "    %6.2f MiB  %s\n", $5/1048576, $NF}' | sort -rn | head -8
else echo "F2 失败"; grep -m3 error "$ROOT/FLOOR_f2.log"; fi
