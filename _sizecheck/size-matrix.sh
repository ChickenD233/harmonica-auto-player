#!/usr/bin/env bash
# 体积实验：对同一份源码尝试不同的发布参数，测量单文件 exe 大小。
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
rm -f "$ROOT/results.txt"

run() {
  local name="$1"; shift
  local out="$ROOT/$name"
  local log="$ROOT/$name.log"
  rm -rf "$out"; mkdir -p "$out"
  echo "==================== ${name} ===================="
  if "$DOTNET" publish HarpAutoPlayer.csproj -c Release -r win-x64 -o "$out" "$@" >"$log" 2>&1; then
    local exe="$out/HarpAutoPlayer.exe"
    if [ -f "$exe" ]; then
      local bytes
      bytes=$(stat -f%z "$exe")
      local extra
      extra=$(find "$out" -type f ! -name 'HarpAutoPlayer.exe' | wc -l | tr -d ' ')
      ( cd "$out" && zip -q -r "$ROOT/_t.zip" . )
      local zbytes
      zbytes=$(stat -f%z "$ROOT/_t.zip")
      rm -f "$ROOT/_t.zip"
      printf "%-18s exe=%7.2f MB   zip=%7.2f MB   额外文件=%s\n" \
        "$name" \
        "$(echo "scale=4; $bytes/1048576" | bc)" \
        "$(echo "scale=4; $zbytes/1048576" | bc)" \
        "$extra"
      echo "$name $bytes $zbytes" >> "$ROOT/results.txt"
    else
      echo "${name} : 未产生 exe，见 ${log}"
    fi
  else
    echo "${name} : 发布失败，见 ${log}"
    grep -m3 'error' "$log" | head -3
  fi
  echo
}

BASE=(-p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true)
LEAN=(-p:DebugType=none -p:UseSystemResourceKeys=true -p:StackTraceSupport=false
      -p:EventSourceSupport=false -p:MetadataUpdaterSupport=false
      -p:InvariantGlobalization=true)

run A_baseline "${BASE[@]}" --self-contained true

run B_compress "${BASE[@]}" --self-contained true -p:EnableCompressionInSingleFile=true

run C_compress_lean "${BASE[@]}" --self-contained true \
  -p:EnableCompressionInSingleFile=true "${LEAN[@]}"

run D_trim_partial "${BASE[@]}" --self-contained true \
  -p:EnableCompressionInSingleFile=true "${LEAN[@]}" \
  -p:PublishTrimmed=true -p:TrimMode=partial

run E_trim_full "${BASE[@]}" --self-contained true \
  -p:EnableCompressionInSingleFile=true "${LEAN[@]}" \
  -p:PublishTrimmed=true -p:TrimMode=full

run F_framework_dep -p:PublishSingleFile=true --self-contained false

run G_trim_conservative "${BASE[@]}" --self-contained true \
  -p:EnableCompressionInSingleFile=true -p:DebugType=none \
  -p:PublishTrimmed=true -p:TrimMode=partial

echo "==================== 汇总 ===================="
sort -k2 -n "$ROOT/results.txt" | awk '{printf "%-18s exe=%7.2f MB   zip=%7.2f MB\n", $1, $2/1048576, $3/1048576}'
