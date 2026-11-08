"""清理 Release 上的诊断用 probe 资产。"""
import json
import os
import shutil
import subprocess

# token 从环境变量读，绝不写进代码库
TOKEN = os.environ['GITHUB_TOKEN']

# 沙箱里子进程的 PATH 与 Bash 不同，'curl' 可能找不到 —— 用绝对路径
CURL = shutil.which('curl') or os.path.join(os.environ.get('SystemRoot', r'C:\Windows'), 'System32', 'curl.exe')
URL = 'https://api.github.com/repos/yangmaolei/Reload/releases/tags/v1.2.0'

out = subprocess.run([
    CURL, '-sS',
    '-H', 'Authorization: Bearer ' + TOKEN,
    '-H', 'Accept: application/vnd.github+json',
    '-H', 'User-Agent: Reload-Cleanup',
    URL,
], capture_output=True, text=True, encoding='utf-8').stdout

data = json.loads(out)
for asset in data.get('assets', []):
    name = asset['name']
    if 'probe' in name.lower():
        print('删除 probe 资产:', name)
        subprocess.run([
            CURL, '-sS', '-X', 'DELETE', asset['url'],
            '-H', 'Authorization: Bearer ' + TOKEN,
            '-H', 'Accept: application/vnd.github+json',
            '-H', 'User-Agent: Reload-Cleanup',
        ], capture_output=True)

print()
print('当前资产:')
out2 = subprocess.run([
    CURL, '-sS',
    '-H', 'Authorization: Bearer ' + TOKEN,
    '-H', 'Accept: application/vnd.github+json',
    '-H', 'User-Agent: Reload-Cleanup',
    URL,
], capture_output=True, text=True, encoding='utf-8').stdout

for asset in json.loads(out2).get('assets', []):
    print(f"  {asset['name']:45} {asset['size']:>12} {asset['state']}")
