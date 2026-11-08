"""创建 v1.2.0 GitHub Release 并上传资产。

注意：
- api.github.com 与 uploads.github.com 直连可用，不要加 -x 代理
- Python 调 curl 必须用 list 参数（' '.join 会吃掉引号）
- GitHub API 匿名调用会被限流，必须带 token
"""
import json
import subprocess
import sys
import os

ROOT = r'C:\Users\yang\WorkBuddy\2026-10-30-20-41-20\Reload'
OWNER = 'yangmaolei'
REPO = 'Reload'
TAG = 'v1.2.0'
TOKEN = os.environ['GITHUB_TOKEN']

API = f'https://api.github.com/repos/{OWNER}/{REPO}'
UPLOAD = f'https://uploads.github.com/repos/{OWNER}/{REPO}/releases'


def curl(args):
    """执行 curl，参数用 list 传。"""
    cmd = ['curl', '-sS', '-L'] + args
    result = subprocess.run(cmd, capture_output=True, text=True, encoding='utf-8')
    if result.returncode != 0:
        print('curl failed:', result.stderr[:400])
        sys.exit(1)
    return result.stdout


def api(method, url, payload=None, extra=None):
    args = ['-X', method, url,
            '-H', 'Authorization: Bearer ' + TOKEN,
            '-H', 'Accept: application/vnd.github+json',
            '-H', 'X-GitHub-Api-Version: 2022-11-28',
            '-H', 'User-Agent: Reload-Release-Script']
    if payload is not None:
        args += ['-d', json.dumps(payload, ensure_ascii=False)]
    if extra:
        args += extra
    out = curl(args)
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
assets = [
    (os.path.join(ROOT, 'publish/portable/Reload-1.2.0-win-x64.zip'), 'application/zip'),
    (os.path.join(ROOT, 'publish/setup/Reload-Setup-1.2.0-win-x64.zip'), 'application/zip'),
    (os.path.join(ROOT, 'publish/SHA256SUMS.txt'), 'text/plain'),
    (os.path.join(ROOT, 'LICENSE'), 'text/plain'),
]

existing_names = {a['name'] for a in release.get('assets', [])}

for path, content_type in assets:
    name = os.path.basename(path)
    if not os.path.exists(path):
        print('跳过（不存在）:', path)
        continue
    if name in existing_names:
        print('跳过（已存在）:', name)
        continue

    size = os.path.getsize(path)
    print(f'上传 {name} ({size} 字节) …', flush=True)

    out = curl([
        '-X', 'POST', f'{upload_url}?name={name}',
        '-H', 'Authorization: Bearer ' + TOKEN,
        '-H', 'Accept: application/vnd.github+json',
        '-H', 'X-GitHub-Api-Version: 2022-11-28',
        '-H', 'User-Agent: Reload-Release-Script',
        '-H', 'Content-Type: ' + content_type,
        '--data-binary', '@' + path,
    ])

    try:
        result = json.loads(out)
    except json.JSONDecodeError:
        print('上传响应非 JSON:', out[:600])
        sys.exit(1)

    if 'id' in result:
        print(f'  ✓ 上传成功 id={result["id"]} size={result.get("size")}')
    else:
        print('  ✗ 上传失败:', json.dumps(result, ensure_ascii=False)[:500])
        sys.exit(1)

print()
print('完成。Release 页面:')
print(f'  https://github.com/{OWNER}/{REPO}/releases/tag/{TAG}')
