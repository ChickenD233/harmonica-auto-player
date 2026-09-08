#!/usr/bin/env bash
# 在非 Windows 主机上交叉发布 Windows x64 自包含程序（需要 .NET 8 SDK）。
# 需要本仓库根目录下的 .tools/dotnet（.NET 8 SDK），或系统已装 dotnet。
set -euo pipefail
cd "$(dirname "$0")"

if command -v dotnet >/dev/null 2>&1; then
  DOTNET=dotnet
elif [ -x ".tools/dotnet/dotnet" ]; then
  DOTNET="$PWD/.tools/dotnet/dotnet"
else
  echo "未找到 dotnet，请先安装 .NET 8 SDK 或运行 .tools/dotnet-install.sh" >&2
  exit 1
fi

export NUGET_PACKAGES="${NUGET_PACKAGES:-$PWD/.tools/nuget}"
export DOTNET_CLI_HOME="${DOTNET_CLI_HOME:-$PWD/.tools/dotnet-home}"
export DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1

OUT="release/win-x64"
echo ">> 发布 win-x64 自包含程序到 $OUT ..."
"$DOTNET" publish HarpAutoPlayer/HarpAutoPlayer.csproj -c Release \
    -r win-x64 --self-contained true -o "$OUT"

# 打成 zip（zip 存在则用 zip；否则用 ditto/tar 兜底）
ZIP="release/HarpAutoPlayer-win-x64.zip"
echo ">> 打包 $ZIP ..."
rm -f "$ZIP"
if command -v zip >/dev/null 2>&1; then
  (cd "$OUT" && zip -qr "../../$ZIP" .)
else
  tar -C "$OUT" -czf "$ZIP" .
fi
echo ">> 完成：$ZIP"
ls -lh "$ZIP"
