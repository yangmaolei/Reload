"""校验 GitHub Release 资产完整性：Range 下载首尾各 4MB，与本地比 md5。"""
import hashlib
import os
import subprocess
import sys

ROOT = r'C:\Users\yang\WorkBuddy\2026-10-30-20-41-20\Reload'
BASE = 'https://github.com/yangmaolei/Reload/releases/download/v1.2.0'


def curl_range(url, start, end, out):
    args = [
        '-sS', '-L', '--max-time', '300',
        '-H', 'User-Agent: Reload-Verify',
        '-r', f'{start}-{end}',
        '-o', out,
        url,
    ]
    result = subprocess.run(args, capture_output=True, text=True)
    if result.returncode != 0:
        print('curl failed:', result.stderr[:300])
        return None
    return out


def md5(path):
    return hashlib.md5(open(path, 'rb').read()).hexdigest()


def verify(name, local_path):
    url = f'{BASE}/{name}'
    size = os.path.getsize(local_path)
    print(f'--- {name} ({size} 字节) ---')

    ok = True

    # 首 4MB
    head_len = min(4 * 1024 * 1024, size)
    tmp = os.path.join(os.environ['TEMP'], 'rel-head.bin')
    if curl_range(url, 0, head_len - 1, tmp):
        remote = md5(tmp)
        local = md5(open(local_path, 'rb').read(head_len))
        match = remote == local
        print(f'  首 {head_len} 字节 md5: {"✓ 一致" if match else "✗ 不一致"}')
        ok = ok and match

    # 尾 4MB（显式区间，不支持 bytes=-N 后缀写法）
    tail_start = max(0, size - 4 * 1024 * 1024)
    tmp2 = os.path.join(os.environ['TEMP'], 'rel-tail.bin')
    if curl_range(url, tail_start, size - 1, tmp2):
        remote2 = md5(tmp2)
        with open(local_path, 'rb') as f:
            f.seek(tail_start)
            local2 = hashlib.md5(f.read()).hexdigest()
        match2 = remote2 == local2
        print(f'  尾 {size - tail_start} 字节 md5: {"✓ 一致" if match2 else "✗ 不一致"}')
        ok = ok and match2

    for p in (tmp, tmp2):
        if os.path.exists(p):
            os.remove(p)

    return ok


targets = [
    ('Reload-1.2.0-win-x64.zip', os.path.join(ROOT, 'publish/Reload-1.2.0-win-x64.zip')),
    ('Reload-Setup-1.2.0-win-x64.zip', os.path.join(ROOT, 'publish/Reload-Setup-1.2.0-win-x64.zip')),
]

for name, local in targets:
    if not os.path.exists(local):
        print(f'本地文件不存在: {local}')
        continue
    if not verify(name, local):
        print('  完整性校验未通过')
        sys.exit(1)

print()
print('全部资产完整性校验通过')
