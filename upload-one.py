"""上传单个 Release 资产，带断点式重试。

【为什么要专门写这个】
直连 uploads.github.com 只有 0.05 MB/s，68MB 要传 20+ 分钟；
长时间单连接会被中途 reset（curl 55）。
所以拆成"每次传一段、断了就重连"的思路：
curl 传整个文件，但失败后立刻重试（服务器按 asset name 幂等，
重传会覆盖同名资产，不会产生重复）。

代理与否由命令行参数决定 —— VPN 开启后代理反而更慢。
"""
import json
import os
import shutil
import subprocess
import sys
import time

ROOT = r'C:\Users\yang\WorkBuddy\2026-10-30-20-41-20\Reload'
TOKEN = os.environ['GITHUB_TOKEN']
OWNER = 'yangmaolei'
REPO = 'Reload'
TAG = 'v1.2.0'
UPLOAD = f'https://uploads.github.com/repos/{OWNER}/{REPO}/releases/438126585/assets'

# 用绝对路径调 curl —— 沙箱里子进程的 PATH 与 Bash 不同，'curl' 会找不到
CURL = shutil.which('curl') or r'C:\Windows\System32\curl.exe'

local_path = sys.argv[1]
use_proxy = '--proxy' in sys.argv

name = os.path.basename(local_path)
size = os.path.getsize(local_path)
print(f'上传 {name} ({size} 字节) 代理={"是" if use_proxy else "否"}', flush=True)

attempt = 0
max_attempts = 12

while attempt < max_attempts:
    attempt += 1
    start = time.time()

    args = [
        CURL, '-sS', '-L', '--max-time', '1500',
        '-X', 'POST', f'{UPLOAD}?name={name}',
        '-H', 'Authorization: Bearer ' + TOKEN,
        '-H', 'Accept: application/vnd.github+json',
        '-H', 'X-GitHub-Api-Version: 2022-11-28',
        '-H', 'User-Agent: Reload-Release-Script',
        '-H', 'Content-Type: application/zip',
    ]

    if use_proxy:
        args += ['-x', 'http://127.0.0.1:12450']

    args += ['--data-binary', '@' + local_path]

    result = subprocess.run(args, capture_output=True, text=True, encoding='utf-8')
    elapsed = time.time() - start

    if result.returncode == 0 and '"id"' in result.stdout:
        try:
            info = json.loads(result.stdout)
            print(f'✓ 上传成功 id={info["id"]} size={info.get("size")} 用时 {elapsed:.0f}s', flush=True)
            sys.exit(0)
        except json.JSONDecodeError:
            pass

    print(f'  第 {attempt}/{max_attempts} 次失败（用时 {elapsed:.0f}s）：'
          f'{(result.stderr or result.stdout)[:150]}', flush=True)

    if attempt < max_attempts:
        wait = min(5 * attempt, 30)
        print(f'  等待 {wait} 秒后重试…', flush=True)
        time.sleep(wait)

print('全部重试均失败')
sys.exit(1)
