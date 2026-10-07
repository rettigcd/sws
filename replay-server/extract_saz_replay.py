"""Dev-time only: turns saz/vow/snl_oct_01/snl_oct_01.saz into replay-data/ (next to this file) for ReplayServer.cs.
The server never reads the .saz. Run it from any folder:  python replay-server/extract_saz_replay.py
Keeps only the four app hosts (snlstandby, pro.vow, go.vow, api.vow), drops CORS preflights (the replay is one origin),
decodes bodies, points absolute URLs of those hosts at the replay origin (placeholder), and keeps cookies minus AWS load-balancer ones."""
import zipfile, json, re, shutil, gzip, zlib, pathlib, collections
from urllib.parse import urlsplit

HERE = pathlib.Path(__file__).resolve().parent     # the replay-server folder
SAZ = HERE.parent / 'saz' / 'vow' / 'snl_oct_01' / 'snl_oct_01.saz'
OUT = HERE / 'replay-data'
HOSTS = {'snlstandby.nbcuni.com', 'pro.vow.app', 'go.vow.app', 'api.vow.app'}
TEXT = ('text/', 'application/json', 'application/javascript')
# absolute origins of the app hosts -> a placeholder that ReplayServer.cs replaces with http://localhost:PORT when it loads the data
# (an empty base URL does not work: go.vow.app's JS then builds 'api/v2/...' without the leading slash)
PLACEHOLDER = '__REPLAY_ORIGIN__'
EVENTS_PATH = '/api/v2/public/by-url/nbc/events'
ORIGIN = re.compile(r'https?:(?:\\?/){2}(?:snlstandby\.nbcuni\.com|pro\.vow\.app|go\.vow\.app|api\.vow\.app)')

def split(b):
    i = b.find(b'\r\n\r\n')
    return b[:i].decode('latin1').split('\r\n'), b[i + 4:]

def main():
    z = zipfile.ZipFile(SAZ)
    if OUT.exists(): shutil.rmtree(OUT)
    (OUT / 'bodies').mkdir(parents=True)
    entries = []
    for name in sorted(n for n in z.namelist() if n.endswith('_c.txt')):
        sid = int(name[4:7])
        creq, _ = split(z.read(name))
        method, target, _ = creq[0].split(' ', 2)
        u = urlsplit(target)
        if u.hostname not in HOSTS or method == 'OPTIONS': continue
        lines, body = split(z.read(f'raw/{sid:03d}_s.txt'))
        status = int(lines[0].split(' ')[1])
        hdr = [l.split(':', 1) for l in lines[1:] if ':' in l]
        h = {k.strip().lower(): v.strip() for k, v in hdr}
        enc = h.get('content-encoding')
        if enc == 'gzip': body = gzip.decompress(body)
        elif enc == 'deflate': body = zlib.decompress(body)
        elif enc: raise SystemExit(f'{sid}: unhandled encoding {enc}')
        ctype = h.get('content-type', '')
        if ctype.startswith(TEXT):
            body = ORIGIN.sub(PLACEHOLDER, body.decode('utf-8')).encode('utf-8')
        cookies = []
        for k, v in hdr:
            if k.strip().lower() != 'set-cookie' or v.strip().startswith('AWSALB') or re.match(r'[A-Za-z0-9]{40}=', v.strip()): continue   # random-named Laravel session cookie: piles up in the browser
            parts = [p for p in v.strip().split(';') if not re.match(r'\s*(Domain=|Secure$|SameSite=)', p, re.I)]
            cookies.append(';'.join(parts))
        fn = f'bodies/{sid:03d}.bin'
        (OUT / fn).write_bytes(body)
        entries.append({'id': sid, 'method': method, 'host': u.hostname, 'path': u.path, 'query': u.query,
                        'status': status, 'contentType': ctype, 'cookies': cookies, 'file': fn})
    # The show-list poll was captured ~97 times (coming_soon, then open). ReplayServer.cs generates its answers from a timeline instead,
    # so keep one coming-soon response and one open response (the last one) as templates and drop the rest.
    polls = [e for e in entries if e['method'] == 'GET' and e['path'] == EVENTS_PATH]
    first, last = polls[0], polls[-1]
    assert '"status":"coming_soon"' in (OUT / first['file']).read_text('utf-8') and '"status":"open"' in (OUT / last['file']).read_text('utf-8')
    first['role'], last['role'] = 'events-coming-soon', 'events-open'
    for e in polls[1:-1]: (OUT / e['file']).unlink()
    entries = [e for e in entries if e not in polls[1:-1]]
    (OUT / 'index.json').write_text(json.dumps(entries, indent=1), encoding='utf-8')
    # report: same path+query on two hosts would be ambiguous for the path-only routing
    seen = collections.defaultdict(set)
    for e in entries: seen[(e['method'], e['path'], e['query'])].add(e['host'])
    print(len(entries), 'entries,', len(seen), 'distinct requests,', sum(len(v) > 1 for v in seen.values()), 'on more than one host')

main()

