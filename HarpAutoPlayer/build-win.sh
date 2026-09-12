#!/usr/bin/env bash
# 在非 Windows 主机上交叉发布 Windows x64 自包含**单文件**程序（需要 .NET 8 SDK）。
# 需要本仓库根目录下的 .tools/dotnet（.NET 8 SDK），或系统已装 dotnet。
#
# 产物与线上 Release 一致：zip 根目录下只有 HarpAutoPlayer.exe 与「更新说明.txt」。
# 单文件 exe 已内嵌 .NET 运行时与全部原生库，用户双击即可运行（无需安装 .NET）。
set -euo pipefail
cd "$(dirname "$0")"

if command -v dotnet >/dev/null 2>&1; then
  DOTNET=dotnet
elif [ -x "../.tools/dotnet/dotnet" ]; then
  DOTNET="$PWD/../.tools/dotnet/dotnet"
elif [ -x ".tools/dotnet/dotnet" ]; then
  DOTNET="$PWD/.tools/dotnet/dotnet"
else
  echo "未找到 dotnet，请先安装 .NET 8 SDK 或运行 .tools/dotnet-install.sh" >&2
  exit 1
fi

export NUGET_PACKAGES="${NUGET_PACKAGES:-$PWD/../.tools/nuget}"
export DOTNET_CLI_HOME="${DOTNET_CLI_HOME:-$PWD/../.tools/dotnet-home}"
export DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1
# Avalonia 的构建遥测默认会尝试写 ~/Library/Application Support/AvaloniaUI/，
# 在受限环境（沙箱 / 只读 HOME）下会直接让 MSBuild 报 MSB4018 失败，发布脚本里关掉。
export AVALONIA_TELEMETRY_OPTOUT=1

OUT="release/win-x64"
echo ">> 发布 win-x64 自包含单文件程序到 $OUT ..."
rm -rf "$OUT"
# 体积相关参数（实测依据见《HarpAutoPlayer-体积分析报告.md》，_sizecheck/ 保留原始日志）：
#   EnableCompressionInSingleFile : exe 内部 deflate，94 MB → 43 MB（不影响下载体积，zip 本就压过）
#   PublishTrimmed + TrimMode=partial : 按调用图删掉用不到的 BCL，下载 39.6 MB → 15.5 MB（主要收益）
#   DebugType=none : 不嵌入 PDB
# 注意：不要开 TrimMode=full（只再省 0.5 MB，但会裁到反射式绑定/JSON）；
#       也不要开 InvariantGlobalization / UseSystemResourceKeys（合计仅 0.4 MB，却会改变文化与异常文本行为）。
"$DOTNET" publish HarpAutoPlayer.csproj -c Release \
    -r win-x64 --self-contained true \
    -p:PublishSingleFile=true \
    -p:IncludeNativeLibrariesForSelfExtract=true \
    -p:ExcludeNativeLibrariesFromSingleFile=false \
    -p:EnableCompressionInSingleFile=true \
    -p:PublishTrimmed=true -p:TrimMode=partial \
    -p:DebugType=none \
    -o "$OUT"

# 清掉交叉编译时被一并复制过来的其它平台原生库（macOS/Linux），
# 它们是本机 SDK 的产物，Windows 用不到，进包只会让用户困惑。
find "$OUT" -type f \( -name "*.dylib" -o -name "*.so" \) -delete 2>/dev/null || true

# 更新说明的来源放在 docs/（**不要**放 release/：那是构建产物目录，重建会被清掉）
NOTES="docs/更新说明.txt"
if [ ! -f "$NOTES" ]; then
  echo "!! 缺少 $NOTES（发布包必须带「更新说明.txt」），已用占位内容代替" >&2
  NOTESTMP="$(mktemp)"
  printf '口琴自动演奏器\n\n（本次构建未附带更新说明）\n' > "$NOTESTMP"
  NOTES="$NOTESTMP"
fi

ZIP="release/HarpAutoPlayer-win-x64.zip"
echo ">> 打包 $ZIP ..."
rm -f "$ZIP"
# 优先用 python3 打包：它能把中文文件名写成带 UTF-8 标志的条目，
# 避免 Windows 资源管理器解压出来是乱码（macOS 自带 zip 不会加这个标志）。
if command -v python3 >/dev/null 2>&1; then
  python3 - "$OUT/HarpAutoPlayer.exe" "$NOTES" "$ZIP" <<'PY'
import os, sys, time, zipfile
exe, notes, out = sys.argv[1], sys.argv[2], sys.argv[3]
with zipfile.ZipFile(out, "w", zipfile.ZIP_DEFLATED, compresslevel=6) as z:
    for path in (exe, notes):
        name = os.path.basename(path)
        zi = zipfile.ZipInfo(name)
        zi.flag_bits |= 0x800                     # UTF-8 文件名
        st = os.stat(path)
        zi.date_time = tuple(time.localtime(st.st_mtime)[:6])
        zi.external_attr = (0o755 if name.endswith(".exe") else 0o644) << 16
        with open(path, "rb") as fh:
            z.writestr(zi, fh.read(), zipfile.ZIP_DEFLATED, 6)
print("   条目:", zipfile.ZipFile(out).namelist())
PY
elif command -v zip >/dev/null 2>&1; then
  (cd "$OUT" && zip -qr "../../$ZIP" .)
else
  tar -C "$OUT" -czf "$ZIP" .
fi

echo ">> 完成：$ZIP"
ls -lh "$ZIP"
