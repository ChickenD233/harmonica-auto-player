#!/usr/bin/env bash
# 补测：强制重建的完全裁剪(E2)，以及「只裁剪、不做单文件压缩」(H)
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

run() {
  local name="$1"; shift
  local out="$ROOT/$name"
  local log="$ROOT/$name.log"
  rm -rf "$out"; mkdir -p "$out"
  echo "==================== ${name} ===================="
  if "$DOTNET" publish HarpAutoPlayer.csproj -c Release -r win-x64 -o "$out" "$@" >"$log" 2>&1; then
    local exe="$out/HarpAutoPlayer.exe"
    if [ -f "$exe" ]; then
      local bytes zbytes
      bytes=$(stat -f%z "$exe")
      ( cd "$out" && zip -q -r "$ROOT/_t.zip" . )
      zbytes=$(stat -f%z "$ROOT/_t.zip"); rm -f "$ROOT/_t.zip"
      echo "ILLink 实际运行: $(grep -ci 'illink' "$log") 行"
      printf "%-18s exe=%7.2f MB   zip=%7.2f MB\n" \
        "$name" "$(echo "scale=4; $bytes/1048576" | bc)" "$(echo "scale=4; $zbytes/1048576" | bc)"
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

# E2：清掉 obj 强制完全裁剪
echo ">> 清掉 obj 以强制重建（E2）"
rm -rf obj
run E2_trim_full_forced "${BASE[@]}" --self-contained true \
  -p:EnableCompressionInSingleFile=true "${LEAN[@]}" \
  -p:PublishTrimmed=true -p:TrimMode=full

# H：只裁剪，不开启单文件压缩（下载体积接近，但启动更快、磁盘更大）
run H_trim_no_singlefile_compress "${BASE[@]}" --self-contained true \
  -p:PublishTrimmed=true -p:TrimMode=partial -p:DebugType=none
