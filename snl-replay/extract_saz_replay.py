"""Dev-time only: turns saz/vow/snl_oct_08/snl_oct_08.saz into replay-data/ (next to this file) for SnlReplay.cs.
The server never reads the .saz. Run it from any folder:  python snl-replay/extract_saz_replay.py   (needs:  pip install brotli)
Keeps only the four app hosts (snlstandby, pro.vow, go.vow, api.vow), drops CORS preflights (the replay is one origin),
decodes bodies, points absolute URLs of those hosts at the replay origin (placeholder), and keeps cookies minus AWS load-balancer ones.
Two things the Oct 8 capture does not have come from snl_oct_01.saz (after this, the .saz files are not needed any more):
 - the "event is full" 422 (that registration succeeded), with the Oct 8 Live Show's event id in its path and the Oct 8 Closed step's id as capacity_step_id;
 - the images: Chrome had them cached on Oct 8, so they were not requested (the logo and cover of the show list, the shows' background images, the
   pictures in the journey). Same URLs, so the same files."""
import zipfile, json, re, shutil, gzip, zlib, pathlib, collections, time
from urllib.parse import urlsplit
try: import brotli
except ImportError: brotli = None

HERE = pathlib.Path(__file__).resolve().parent     # the snl-replay folder
SAZ = HERE.parent / 'saz' / 'vow' / 'snl_oct_08' / 'snl_oct_08.saz'        # the 2026-10-08 capture of a SUCCESSFUL registration (RSVP -> email)
SAZ_FULL = HERE.parent / 'saz' / 'vow' / 'snl_oct_01' / 'snl_oct_01.saz'   # only for the "event is full" 422 and the images
OUT = HERE / 'replay-data'
HOSTS = {'snlstandby.nbcuni.com', 'pro.vow.app', 'go.vow.app', 'api.vow.app'}
TEXT = ('text/', 'application/json', 'application/javascript')
# absolute origins of the app hosts -> a placeholder that SnlReplay.cs replaces with http://localhost:PORT when it loads the data
# (an empty base URL does not work: go.vow.app's JS then builds 'api/v2/...' without the leading slash)
PLACEHOLDER = '__REPLAY_ORIGIN__'
EVENTS_PATH = '/api/v2/public/by-url/nbc/events'
ORIGIN = re.compile(r'https?:(?:\\?/){2}(?:snlstandby\.nbcuni\.com|pro\.vow\.app|go\.vow\.app|api\.vow\.app)')

def split(b):
    i = b.find(b'\r\n\r\n')
    return b[:i].decode('latin1').split('\r\n'), b[i + 4:]

def dechunk(b):
    out = b''
    while b:
        i = b.find(b'\r\n'); n = int(b[:i].split(b';')[0], 16)
        if n == 0: break
        out += b[i + 2:i + 2 + n]; b = b[i + 2 + n + 2:]
    return out

def decode_body(sid, h, body):
    """Fiddler keeps the bytes as they were on the wire: undo chunked transfer coding, then the content coding."""
    if h.get('transfer-encoding', '').lower() == 'chunked': body = dechunk(body)
    enc = h.get('content-encoding')
    if enc == 'gzip': body = gzip.decompress(body)
    elif enc == 'deflate': body = zlib.decompress(body)
    elif enc == 'br':
        if brotli is None: raise SystemExit('this capture has Brotli-encoded responses: pip install brotli')
        body = brotli.decompress(body)
    elif enc: raise SystemExit(f'{sid}: unhandled encoding {enc}')
    return body

EXT = {'text/html': '.html', 'text/css': '.css', 'application/json': '.json', 'application/javascript': '.js', 'image/png': '.png',
       'image/jpeg': '.jpg', 'font/woff2': '.woff2', 'text/plain': '.txt'}
USED = set()

def body_file(host, path, query, ctype):
    """bodies/<host>/<path with / as _>[__query].<ext>: named after what it is. A second response for the same request gets __2, __3."""
    name = re.sub(r'[^A-Za-z0-9._-]+', '_', path.strip('/')) or 'index'
    if query: name += '__' + re.sub(r'[^A-Za-z0-9._=-]+', '_', query)[:40]
    ext = pathlib.PurePosixPath(path).suffix.lower()
    if not ext or len(ext) > 6:
        ext = EXT.get(ctype.split(';')[0].strip(), '.bin' if ctype else '.empty')
    elif ext == '.jpeg': ext = '.jpg'
    stem = name[:-len(ext)] if name.lower().endswith(ext) else name
    n, fn = 1, f'bodies/{host}/{stem}{ext}'
    while fn in USED:
        n += 1; fn = f'bodies/{host}/{stem}__{n}{ext}'
    USED.add(fn)
    (OUT / fn).parent.mkdir(parents=True, exist_ok=True)
    return fn

def retry(action, *args):
    """Windows (virus scanners, the indexer) briefly locks files that were just written: try again a few times."""
    for attempt in range(20):
        try: return action(*args)
        except PermissionError:
            if attempt == 19: raise
            time.sleep(0.5)

def exchanges(z):
    """(id, method, url parts, status, headers as list of [k, v], decoded body, content type) of every exchange on an app host, in capture order."""
    for name in sorted(n for n in z.namelist() if n.endswith('_c.txt')):
        sid = int(name[4:7])
        creq, _ = split(z.read(name))
        method, target, _ = creq[0].split(' ', 2)
        u = urlsplit(target)
        if u.hostname not in HOSTS or method == 'OPTIONS': continue
        lines, body = split(z.read(f'raw/{sid:03d}_s.txt'))
        hdr = [l.split(':', 1) for l in lines[1:] if ':' in l]
        h = {k.strip().lower(): v.strip() for k, v in hdr}
        yield sid, method, u, int(lines[0].split(' ')[1]), hdr, decode_body(sid, h, body), h.get('content-type', '')

def main():
    if OUT.exists(): retry(shutil.rmtree, OUT)
    (OUT / 'bodies').mkdir(parents=True)
    entries = []
    rsvp_path = None
    for sid, method, u, status, hdr, body, ctype in exchanges(zipfile.ZipFile(SAZ)):
        path, role = u.path, None
        # The successful RSVP and the journey reloaded for the new attendee are kept as TEMPLATES (role, not routed): SnlReplay.cs fills in the
        # ids, names and numbers per RSVP. They keep this capture's own event uuid / journey id; the server swaps them for the registered show's.
        if method == 'PUT' and path.endswith('/attendees/rsvp'): role, path, rsvp_path = 'rsvp-success', '/__template/rsvp-success', u.path
        elif method == 'GET' and path.endswith('/load-for-visitor') and u.query.startswith('attendee='): role, path = 'registered-journey', '/__template/registered-journey'
        if role: assert status == 200, (sid, status)
        if ctype.startswith(TEXT):
            body = ORIGIN.sub(PLACEHOLDER, body.decode('utf-8')).encode('utf-8')
        if ctype.startswith(TEXT):   # the event owner (shown as the journey's owner / host) -> placeholders
            body = body.decode('utf-8')
            for real, placeholder in (('Guarna, Gabby (NBCUniversal)', 'Owner, Event (Example)'), ('guarna,gabby(nbcuniversal)', 'owner,event(example)'),
                                      ('gabrielle.guarna@nbcuni.com', 'owner@example.com')):
                body = body.replace(real, placeholder)
            body = body.encode('utf-8')
        if role:   # the person who registered in the capture -> placeholders (the server writes the typed name and email into every answer anyway)
            body = body.decode('utf-8')
            for real, placeholder in (('Christopher Rettig', 'Test User'), ('Christopher', 'Test'), ('Rettig', 'User'), ('crettig1@tql.com', 'test@example.com')):
                body = body.replace(real, placeholder)
            body = body.encode('utf-8')
        cookies = []
        for k, v in hdr:
            if k.strip().lower() != 'set-cookie' or v.strip().startswith(('AWSALB', 'XSRF-TOKEN=', 'vow_session=')) or re.match(r'[A-Za-z0-9]{40}=', v.strip()): continue   # AWS load-balancer cookies; Laravel's anti-forgery and session cookies (encrypted, look like credentials, not needed); the random-named Laravel session cookie piles up in the browser
            parts = [p for p in v.strip().split(';') if not re.match(r'\s*(Domain=|Secure$|SameSite=)', p, re.I)]
            cookies.append(';'.join(parts))
        fn = body_file(u.hostname, path, '' if role else u.query, ctype)
        (OUT / fn).write_bytes(body)
        e = {'id': sid, 'method': method, 'host': u.hostname, 'path': path, 'query': '' if role else u.query,
             'status': status, 'contentType': ctype, 'cookies': cookies, 'file': fn}
        if role: e['role'] = role
        entries.append(e)
    assert {e.get('role') for e in entries} >= {'rsvp-success', 'registered-journey'}
    # The show-list poll was captured ~50 times (coming_soon, then open). SnlReplay.cs generates its answers from a timeline instead,
    # so keep one coming-soon response and one open response (the last one) as templates and drop the rest.
    polls = [e for e in entries if e['method'] == 'GET' and e['path'] == EVENTS_PATH]
    first, last = polls[0], polls[-1]
    assert '"status":"coming_soon"' in (OUT / first['file']).read_text('utf-8') and '"status":"open"' in (OUT / last['file']).read_text('utf-8')
    first['role'], last['role'] = 'events-coming-soon', 'events-open'
    for e in polls[1:-1]: retry((OUT / e['file']).unlink)
    for e in (first, last):   # name the two kept templates after their role instead of their position in the polls
        new = (OUT / e['file']).with_name(f"api_v2_public_by-url_nbc_events__{e['role'][len('events-'):]}.json")
        retry((OUT / e['file']).replace, new); e['file'] = new.relative_to(OUT).as_posix()
    entries = [e for e in entries if e not in polls[1:-1]]
    # The "event is full" 422 (--rsvp full, and a sold-out show): not in this capture, so the one from the Oct 1 capture, moved onto the Live Show
    # of this capture: its event id in the path, and the Closed step of this capture's journey as capacity_step_id.
    journey = json.loads(next(OUT / e['file'] for e in entries if e.get('role') == 'registered-journey').read_text('utf-8'))
    full = [x for x in exchanges(zipfile.ZipFile(SAZ_FULL)) if x[1] == 'PUT' and x[2].path.endswith('/attendees/rsvp') and x[3] == 422]
    assert full, 'no 422 in the Oct 1 capture'
    sid, method, u, status, hdr, body, ctype = full[0]
    doc = json.loads(body)
    assert doc.get('capacity_full') and 'capacity_step_id' in doc
    doc['capacity_step_id'] = journey['journey']['capacity_step_id']
    fn = body_file('api.vow.app', rsvp_path, '', ctype)
    (OUT / fn).write_text(json.dumps(doc, separators=(',', ':')), encoding='utf-8')
    entries.append({'id': sid, 'method': method, 'host': 'api.vow.app', 'path': rsvp_path, 'query': '', 'status': status, 'contentType': ctype,
                    'cookies': [], 'file': fn})
    # The images the Oct 8 capture lacks (cached by the browser then): every image the Oct 1 capture got with a 200 for a URL this capture never asked for.
    have = {(e['method'], e['host'], e['path'], e['query']) for e in entries}
    for sid, method, u, status, hdr, body, ctype in exchanges(zipfile.ZipFile(SAZ_FULL)):
        key = (method, u.hostname, u.path, u.query)
        if method != 'GET' or status != 200 or not ctype.startswith('image/') or key in have: continue
        have.add(key)
        fn = body_file(u.hostname, u.path, u.query, ctype)
        (OUT / fn).write_bytes(body)
        entries.append({'id': sid, 'method': method, 'host': u.hostname, 'path': u.path, 'query': u.query, 'status': status, 'contentType': ctype,
                        'cookies': [], 'file': fn})
        print('image from the Oct 1 capture:', u.hostname + u.path + ('?' + u.query if u.query else ''), len(body), 'bytes')
    (OUT / 'index.json').write_text(json.dumps(entries, indent=1), encoding='utf-8')
    # report: same path+query on two hosts would be ambiguous for the path-only routing
    seen = collections.defaultdict(set)
    for e in entries: seen[(e['method'], e['path'], e['query'])].add(e['host'])
    print(len(entries), 'entries,', len(seen), 'distinct requests,', sum(len(v) > 1 for v in seen.values()), 'on more than one host')

main()
