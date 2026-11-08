#!/usr/bin/env bash
# Reload 1.2.0 发布打包脚本
# 产出两个版本：
#   1. 便携版  publish/portable/Reload-1.2.0-win-x64.zip
#   2. 安装版  publish/setup/Reload-Setup-1.2.0-win-x64.exe（单文件，payload 内嵌）
set -euo pipefail

ROOT="/c/Users/yang/WorkBuddy/2026-10-30-20-41-20/Reload"
DOTNET="/c/Users/yang/.dotnet-sdk/sdk8/dotnet.exe"
VERSION="1.2.0"

cd "$ROOT"

echo "==> 1/6 构建主程序（自包含单文件）"
rm -rf publish/win-x64
"$DOTNET" publish src/GameSaveSync.App/GameSaveSync.App.csproj \
  -c Release -r win-x64 \
  -p:SelfContained=true \
  -p:PublishSingleFile=true \
  -p:IncludeNativeLibrariesForSelfExtract=true \
  -p:EnableCompressionInSingleFile=true \
  -p:DebugType=none \
  -o publish/win-x64 2>&1 | grep -E "error|错误|警告" || true

ls -la publish/win-x64/Reload.exe

echo "==> 2/6 准备安装器 payload 与依赖"
mkdir -p src/GameSaveSync.Setup/payload
cp publish/win-x64/Reload.exe src/GameSaveSync.Setup/payload/Reload.exe

# 依赖 dll 拷进 deps/，构建时会被嵌进安装器 exe
rm -rf src/GameSaveSync.Setup/deps
mkdir -p src/GameSaveSync.Setup/deps
for dep in CommunityToolkit.Mvvm.dll Microsoft.Bcl.AsyncInterfaces.dll \
           System.Buffers.dll System.ComponentModel.Annotations.dll System.Memory.dll \
           System.Numerics.Vectors.dll System.Runtime.CompilerServices.Unsafe.dll \
           System.Threading.Tasks.Extensions.dll; do
  cp "src/GameSaveSync.Setup/bin/Release/net48/$dep" src/GameSaveSync.Setup/deps/ 2>/dev/null || true
done
# app.config 里的 bindingRedirect 在单文件下要手动应用，嵌进去
cp src/GameSaveSync.Setup/bin/Release/net48/Reload-Setup.exe.config \
   src/GameSaveSync.Setup/deps/app.config 2>/dev/null || true

echo "==> 3/6 构建安装器（payload 与依赖内嵌）"
"$DOTNET" build src/GameSaveSync.Setup/GameSaveSync.Setup.csproj -c Release 2>&1 \
  | grep -E "error|错误|警告" || true

ls -la src/GameSaveSync.Setup/bin/Release/net48/Reload-Setup.exe

echo "==> 4/6 组装安装版（单文件）"
rm -rf publish/setup
mkdir -p publish/setup

# 单文件安装器：payload 与依赖全部内嵌，用户只需这一个 exe
cp src/GameSaveSync.Setup/bin/Release/net48/Reload-Setup.exe publish/setup/
cp LICENSE publish/setup/

cat > publish/setup/安装说明.txt <<'EOF'
Reload — 游戏存档同步（安装版）v1.2.0
=====================================

安装步骤
--------
1. 双击 Reload-Setup.exe
2. 选择安装范围（仅当前用户 / 所有用户）
3. 选择安装位置，确认安装选项
4. 点击「安装」

安装后
------
· 程序位置：%LOCALAPPDATA%\Programs\Reload（默认）
· 桌面与开始菜单会有快捷方式
· 你的存档数据保存在 %LOCALAPPDATA%\Reload，与安装目录无关

卸载
----
「设置 → 应用 → 已安装的应用 → Reload → 卸载」，
或直接运行 Reload-Setup.exe --uninstall。
卸载只删程序与快捷方式，**不会删除你的存档数据**。

如果 Windows 弹出「已保护你的电脑」
----------------------------------
这是 SmartScreen 对未签名程序的常规提示，不是病毒。
点「更多信息」→「仍要运行」即可；
或在文件属性里勾选「解除锁定」后重试。
EOF

echo "==> 5/6 打包便携版 zip"
rm -rf publish/portable
mkdir -p publish/portable
cp publish/win-x64/Reload.exe publish/portable/
cp LICENSE publish/portable/
cat > publish/portable/使用说明.txt <<'EOF'
Reload — 游戏存档同步（便携版）v1.2.0
====================================

这是便携版：解压即用，不需要安装。

· 直接双击 Reload.exe 运行
· 数据保存在 exe 同级的 data\ 目录
· 不会写注册表、不改系统启动项
· 把整个文件夹拷到 U 盘也能用

想开机自启？在 Reload 的「设置 → 自动化」里开启即可。
EOF

python - <<PYEOF
import os
import zipfile

def make_zip(src_dir, out):
    """
    打包目录内容成 zip。

    注意：目标 zip 必须写在 src_dir 之外。
    否则边写 zip 边扫描目录，会把"正在写的那个 zip"也打包进去 ——
    而且它是残缺的（写一半被读），用户解压会得到一个坏文件。
    """
    out_abs = os.path.abspath(out)
    src_abs = os.path.abspath(src_dir)

    if os.path.commonpath([out_abs, src_abs]) == src_abs:
        raise RuntimeError(f'输出 zip 不能放在被打包目录内：{out}')

    with zipfile.ZipFile(out, 'w', zipfile.ZIP_DEFLATED) as zf:
        for name in sorted(os.listdir(src_abs)):
            full = os.path.join(src_abs, name)
            if os.path.isfile(full):
                zf.write(full, name)

    # 校验：能打开且 CRC 正确
    with zipfile.ZipFile(out) as zf:
        bad = zf.testzip()
        if bad is not None:
            raise RuntimeError(f'zip 校验失败，损坏条目：{bad}')
        count = len(zf.namelist())

    print(f'zip written: {out} {os.path.getsize(out)} bytes, {count} 个条目, CRC 校验 OK')

# 先删掉旧 zip，避免上一次残留
for stale in ['publish/Reload-${VERSION}-win-x64.zip',
              'publish/Reload-Setup-${VERSION}-win-x64.zip']:
    if os.path.exists(stale):
        os.remove(stale)

# 输出到 publish/ 根目录，不在被打包目录内（否则会把自己打包进去）
make_zip('publish/portable', r'publish/Reload-${VERSION}-win-x64.zip')
make_zip('publish/setup', r'publish/Reload-Setup-${VERSION}-win-x64.zip')
PYEOF

echo "==> 6/6 校验"
ls -la publish/portable/
echo
ls -la publish/setup/

python - <<'PYEOF'
import hashlib, os
files = [
    'publish/Reload-1.2.0-win-x64.zip',
    'publish/Reload-Setup-1.2.0-win-x64.zip',
]
lines = []
for p in files:
    if not os.path.exists(p):
        continue
    h = hashlib.sha256(open(p, 'rb').read()).hexdigest()
    size = os.path.getsize(p)
    print(f'{p}\n  {size} bytes\n  sha256 {h}')
    lines.append(f'{h}  {os.path.basename(p)}')

with open('publish/SHA256SUMS.txt', 'w', encoding='utf-8', newline='\n') as f:
    f.write('\n'.join(lines) + '\n')
print()
print('SHA256SUMS.txt written')
PYEOF
