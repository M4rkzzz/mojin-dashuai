"""Download pinned pack files anonymously; verify hashes and record usable origins."""
import argparse
import concurrent.futures
import datetime
import hashlib
import json
from pathlib import Path
import threading
import time
import urllib.error
import urllib.parse
import urllib.request

ROOT = Path(__file__).resolve().parents[1]
HOSTS = {'cdn.modrinth.com', 'mediafilez.forgecdn.net', 'edge.forgecdn.net', 'media.forgecdn.net', 'github.com'}


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--instances', nargs='+', choices=['m3e', 'dc2', 'mb'], default=['dc2', 'mb'])
    parser.add_argument('--limit-mib', type=float, default=2)
    args = parser.parse_args()
    if not 0 < args.limit_mib <= 20:
        parser.error('Limit must be within (0, 20] MiB/s')
    cache = ROOT / '.local/source-cache'
    cache.mkdir(parents=True, exist_ok=True)
    lock = threading.Lock()
    next_read = time.monotonic()

    def throttle(count):
        nonlocal next_read
        with lock:
            now = time.monotonic()
            next_read = max(now, next_read) + count / (args.limit_mib * 1024 ** 2)
            delay = next_read - now
        if delay > 0:
            time.sleep(delay)

    def hashes(path):
        result = {name: hashlib.new(name) for name in ['sha1', 'sha256', 'sha512']}
        with path.open('rb') as stream:
            while data := stream.read(1024 ** 2):
                for digest in result.values():
                    digest.update(data)
        return {name: value.hexdigest() for name, value in result.items()}

    class PublicRedirect(urllib.request.HTTPRedirectHandler):
        def redirect_request(self, req, fp, code, msg, headers, newurl):
            uri = urllib.parse.urlsplit(newurl)
            if uri.scheme != 'https' or uri.hostname not in HOSTS | {'release-assets.githubusercontent.com', 'objects.githubusercontent.com'} or uri.username or uri.password:
                raise ValueError('Unexpected public download redirect')
            return super().redirect_request(req, fp, code, msg, headers, newurl)

    def verify(row):
        # A prior complete download remains valid only if the cached bytes still match.
        if row.get('sha256') and row.get('downloadVerification', {}).get('verified'):
            target = cache / (row['sha256'] + '.jar')
            if target.is_file() and target.stat().st_size == row['size']:
                actual = hashes(target)
                if actual['sha256'] == row['sha256']:
                    return {**actual, 'ok': True, 'sources': row.get('verifiedSources') or [row['sources'][0]], 'cached': True}
        urls = []
        for source in row.get('sources', []):
            uri = urllib.parse.urlsplit(source)
            if uri.scheme != 'https' or uri.hostname not in HOSTS or uri.username or uri.password or uri.query:
                continue
            # CurseForge uses both CDN hostnames. Verify bytes before retaining any URL.
            if uri.hostname == 'edge.forgecdn.net':
                urls.append(urllib.parse.urlunsplit(uri._replace(netloc='mediafilez.forgecdn.net')))
            urls.append(source)
        errors = []
        part = cache / (row['sha1'] + '.download-part')
        for url in dict.fromkeys(urls):
            try:
                request = urllib.request.Request(url, headers={'User-Agent': 'MojinDashuai-PackVerifier/0.1', 'Accept-Encoding': 'identity'})
                with urllib.request.build_opener(PublicRedirect).open(request, timeout=30) as response, part.open('wb') as output:
                    if response.status != 200:
                        raise ValueError('Unexpected download status')
                    count = 0
                    while data := response.read(65536):
                        count += len(data)
                        if count > row['size']:
                            raise ValueError('Download exceeds pinned size')
                        output.write(data)
                        throttle(len(data))
                actual = hashes(part)
                if count != row['size'] or actual['sha1'] != row['sha1'] or (row.get('sha256') and actual['sha256'] != row['sha256']):
                    raise ValueError('Download does not match pinned file')
                part.replace(cache / (actual['sha256'] + '.jar'))
                return {**actual, 'ok': True, 'sources': [url], 'cached': False}
            except (OSError, ValueError) as error:
                errors.append({'host': urllib.parse.urlsplit(url).hostname, 'error': type(error).__name__,
                               'status': error.code if isinstance(error, urllib.error.HTTPError) else None})
            finally:
                part.unlink(missing_ok=True)
        return {'ok': False, 'errors': errors}

    for instance in args.instances:
        path = ROOT / f'packs/{instance}-source-audit.json'
        audit = json.loads(path.read_text(encoding='utf-8'))
        rows = [r for r in audit['files'] if r.get('sha1') and r.get('size') and r.get('sources')]
        count = passed = 0

        def save():
            # Source verification and individual mirror publication can overlap.
            # Retain completed mirror evidence for the same exact file.
            current = json.loads(path.read_text(encoding='utf-8'))
            fallbacks = {r.get('sha256'): r['fallback'] for r in current['files'] if r.get('fallback')}
            for row in audit['files']:
                if row.get('sha256') in fallbacks:
                    row['fallback'] = fallbacks[row['sha256']]
            audit['releaseReady'] = False
            temp = path.with_suffix('.tmp')
            temp.write_text(json.dumps(audit, ensure_ascii=False, indent=2) + '\n', encoding='utf-8')
            temp.replace(path)

        with concurrent.futures.ThreadPoolExecutor(max_workers=2) as pool:
            futures = {pool.submit(verify, row): row for row in rows}
            for future in concurrent.futures.as_completed(futures):
                row = futures[future]
                result = future.result()
                count += 1
                passed += result['ok']
                if result['ok']:
                    for algorithm in ['sha1', 'sha256', 'sha512']:
                        row[algorithm] = result[algorithm]
                    row['verifiedSources'] = result['sources']
                    row['status'] = 'author-origin-download-verified'
                row['downloadVerification'] = {'verified': result['ok'], 'at': datetime.datetime.now(datetime.timezone.utc).isoformat(),
                                               'cached': result.get('cached', False), 'errors': result.get('errors', [])}
                if count % 20 == 0:
                    save()
                    print(json.dumps({'instance': instance, 'checked': count, 'total': len(rows), 'passed': passed}), flush=True)
        save()
        print(json.dumps({'instance': instance, 'checked': count, 'passed': passed, 'releaseReady': False}), flush=True)


if __name__ == '__main__':
    main()
