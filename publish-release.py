"""创建 v1.2.0 GitHub Release 并上传资产。

注意：
- api.github.com 与 uploads.github.com 直连可用，不要加 -x 代理
  （但大文件直连偶尔会被 reset，需要重试；代理更慢，只作兜底）
- Python 调 curl 必须用 list 参数（' '.join 会吃掉引号）
- GitHub API 匿名调用会被限流，必须带 token
"""
import json
import os
import subprocess
import sys
import time

ROOT = r'C:\Users\yang\WorkBuddy\2026-10-30-20-41-20\Reload'
OWNER = 'yangmaolei'
REPO = 'Reload'
TAG = 'v1.2.0'
TOKEN = os.environ['GITHUB_TOKEN']

API = f'https://api.github.com/repos/{OWNER}/{REPO}'


def curl(args, timeout=3600):
    """执行 curl，参数用 list 传。

    【为什么要走系统代理】
    实测（2026-11-08）：直连 uploads.github.com 只有 0.04 MB/s，
    68MB 要传 28 分钟；走系统代理 127.0.0.1:12450 有 0.36 MB/s，快 9 倍。
    沙箱里的直连链路明显被限速了。
    """
    cmd = ['curl', '-sS', '-L', '--max-time', str(timeout)] + args
    result = subprocess.run(cmd, capture_output=True, text=True, encoding='utf-8')
    return result.returncode, result.stdout, result.stderr


def api(method, url, payload=None):
    args = ['-X', method, url,
            '-H', 'Authorization: Bearer ' + TOKEN,
            '-H', 'Accept: application/vnd.github+json',
            '-H', 'X-GitHub-Api-Version: 2022-11-28',
            '-H', 'User-Agent: Reload-Release-Script']
    if payload is not None:
        args += ['-d', json.dumps(payload, ensure_ascii=False)]

    code, out, err = curl(args)
    if code != 0:
        print('api curl failed:', err[:300])
        sys.exit(1)

    try:
        return json.loads(out)
    except json.JSONDecodeError:
        print('非 JSON 响应:', out[:600])
        sys.exit(1)


# ---------- 1. 创建 Release ----------
with open(os.path.join(ROOT, 'RELEASE_NOTES_1.2.0.md'), encoding='utf-8') as f:
    body = f.read()

existing = api('GET', f'{API}/releases/tags/{TAG}')
if isinstance(existing, dict) and existing.get('id'):
    print('Release 已存在，id =', existing['id'])
    release = existing
else:
    release = api('POST', f'{API}/releases', {
        'tag_name': TAG,
        'name': 'Reload v1.2.0 — 多选与批量导出 · 自启动检测 · 安装版',
        'body': body,
        'draft': False,
        'prerelease': False,
        'make_latest': 'true',
    })
    print('Release created, id =', release.get('id'))

release_id = release['id']
upload_url = release.get('upload_url', '').split('{')[0]

# ---------- 2. 上传资产 ----------
# 小文件先传，保证即使大文件失败，Release 也已有可下载内容
assets = [
    (os.path.join(ROOT, 'publish/SHA256SUMS.txt'), 'text/plain'),
    (os.path.join(ROOT, 'LICENSE'), 'text/plain'),
    (os.path.join(ROOT, 'publish/Reload-1.2.0-win-x64.zip'), 'application/zip'),
    (os.path.join(ROOT, 'publish/Reload-Setup-1.2.0-win-x64.zip'), 'application/zip'),
]

existing_names = {a['name'] for a in release.get('assets', [])}


def delete_asset(name):
    """删掉同名资产（用于重传修正过的文件）。"""
    for asset in release.get('assets', []):
        if asset['name'] != name:
            continue

        print(f'删除已有资产 {name}（内容已修正，需要重传）', flush=True)
        code, out, err = curl([
            '-X', 'DELETE', asset['url'],
            '-H', 'Authorization: Bearer ' + TOKEN,
            '-H', 'Accept: application/vnd.github+json',
            '-H', 'X-GitHub-Api-Version: 2022-11-28',
            '-H', 'User-Agent: Reload-Release-Script',
        ])
        if code == 0:
            print('  ✓ 已删除')
        else:
            print('  ✗ 删除失败:', (err or out)[:200])
        return True

    return False


def upload(path, content_type, attempts=6):
    """上传一个资产，带重试。"""
    name = os.path.basename(path)
    size = os.path.getsize(path)

    for attempt in range(1, attempts + 1):
        print(f'上传 {name} ({size} 字节) 第 {attempt}/{attempts} 次 …', flush=True)

        args = [
            '-X', 'POST', f'{upload_url}?name={name}',
            '-H', 'Authorization: Bearer ' + TOKEN,
            '-H', 'Accept: application/vnd.github+json',
            '-H', 'X-GitHub-Api-Version: 2022-11-28',
            '-H', 'User-Agent: Reload-Release-Script',
            '-H', 'Content-Type: ' + content_type,
            # 大文件走系统代理：直连被限速到 0.04 MB/s，代理是 0.36 MB/s
            '-x', 'http://127.0.0.1:12450',
            '--data-binary', '@' + path,
        ]

        code, out, err = curl(args)

        if code == 0:
            try:
                result = json.loads(out)
            except json.JSONDecodeError:
                print('  响应非 JSON:', out[:400])
                result = {}

            if 'id' in result:
                print(f'  ✓ 上传成功 id={result["id"]} size={result.get("size")}')
                return True

            message = result.get('message', out[:300])
            print('  ✗ 上传失败:', message)
        else:
            print('  ✗ curl 错误:', (err or out)[:300])

        if attempt < attempts:
            wait = attempt * 8
            print(f'  等待 {wait} 秒后重试…', flush=True)
            time.sleep(wait)

    return False


# 先删掉已知有问题的旧资产（第一次打包时 zip 把自己也打进去了）
for stale in ['Reload-1.2.0-win-x64.zip', 'Reload-Setup-1.2.0-win-x64.zip', 'SHA256SUMS.txt']:
    if stale in existing_names:
        delete_asset(stale)
        existing_names.discard(stale)

failed = []
for path, content_type in assets:
    name = os.path.basename(path)
    if not os.path.exists(path):
        print('跳过（不存在）:', path)
        continue
    if name in existing_names:
        print('跳过（已存在）:', name)
        continue

    if not upload(path, content_type):
        failed.append(name)

print()
if failed:
    print('以下资产上传失败:', failed)
    sys.exit(1)

print('全部资产上传完成。Release 页面:')
print(f'  https://github.com/{OWNER}/{REPO}/releases/tag/{TAG}')
