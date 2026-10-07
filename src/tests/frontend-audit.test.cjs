const test = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const vm = require('node:vm');
const root = path.join(__dirname, '../EventTicketBooking.Api/wwwroot');
const source = fs.readFileSync(path.join(root, 'seat-map.html'), 'utf8');
function fn(name) {
  const start = source.search(new RegExp('(?:async )?function ' + name + '\\('));
  assert.ok(start >= 0, name);
  const tail = source.slice(start);
  const next = tail.slice(1).search(/\n    (?:async )?function /);
  return next < 0 ? tail : tail.slice(0, next + 1);
}
function fixture() {
  const intervals = new Map();
  let nextId = 0;
  const elements = new Map();
  function element() {
    const classes = new Set();
    return { textContent: '', style: {}, classList: {
      add: (...values) => values.forEach(v => classes.add(v)),
      remove: (...values) => values.forEach(v => classes.delete(v)),
      contains: v => classes.has(v)
    } };
  }
  const context = vm.createContext({ Date, Intl, Number, String, Map, Set,
    performance: { now: () => 0 },
    document: { getElementById: id => { if (!elements.has(id)) elements.set(id, element()); return elements.get(id); } },
    sessionStorage: { setItem() {}, removeItem() {} },
    setInterval: cb => { intervals.set(++nextId, cb); return nextId; },
    clearInterval: id => intervals.delete(id),
    handleHoldExpired: () => { context.expired = (context.expired || 0) + 1; }
  });
  vm.runInContext('let showtimeId = "show"; let holdTimerInterval = null; let holdExpiresAtMs = 0; let holdRefServerTimeMs = 0; let holdRefLocalPerfMs = 0; const DEFAULT_HOLD_DURATION_SECONDS = 600;', context);
  vm.runInContext(fn('startHoldCountdown') + fn('updateCountdownTick') + fn('stopHoldCountdown'), context);
  return { context, intervals, elements };
}

test('Every static page inline JavaScript and standalone script parses', () => {
  let scripts = 0;
  for (const file of fs.readdirSync(root)) {
    if (file.endsWith('.js')) { new vm.Script(fs.readFileSync(path.join(root, file), 'utf8'), { filename: file }); scripts++; }
    if (!file.endsWith('.html')) continue;
    const html = fs.readFileSync(path.join(root, file), 'utf8');
    for (const match of html.matchAll(/<script\b([^>]*)>([\s\S]*?)<\/script>/gi)) {
      if (/\bsrc=|application\/ld\+json/.test(match[1])) continue;
      new vm.Script(match[2], { filename: file }); scripts++;
    }
  }
  assert.ok(scripts > 20);
});

test('Hold -> unhold -> hold creates exactly one timer and hides it on cancellation', () => {
  const { context, intervals, elements } = fixture();
  context.startHoldCountdown('2026-10-04T10:10:00Z', '2026-10-04T10:00:00Z');
  assert.equal(intervals.size, 1);
  context.stopHoldCountdown();
  assert.equal(intervals.size, 0);
  assert.ok(elements.get('holdTimerCard').classList.contains('hidden'));
  context.startHoldCountdown('2026-10-04T10:10:00Z', '2026-10-04T10:00:00Z');
  context.startHoldCountdown('2026-10-04T10:10:00Z', '2026-10-04T10:00:00Z');
  assert.equal(intervals.size, 1);
  assert.equal(elements.get('countdown-clock').textContent, '10:00');
});

test('A subsecond or expired hold does not leave a new interval running', () => {
  const { context, intervals } = fixture();
  context.startHoldCountdown('2026-10-04T10:00:00.500Z', '2026-10-04T10:00:00Z');
  assert.equal(intervals.size, 0);
  assert.equal(context.expired, 1);
});

test('Missing price is not silently shown as a free ticket; zero remains valid', () => {
  const context = vm.createContext({ Intl, Number });
  vm.runInContext(fn('formatCurrency'), context);
  assert.equal(context.formatCurrency(null), 'Chưa có giá');
  assert.equal(context.formatCurrency(undefined), 'Chưa có giá');
  assert.equal(context.formatCurrency(NaN), 'Chưa có giá');
  assert.match(context.formatCurrency(0), /0/);
});

test('Local login redirects reject external URLs and executable protocols without decoding twice', () => {
  const context = vm.createContext({ URL, window: { location: { href: 'https://tickets.example/login.html', origin: 'https://tickets.example' } } });
  vm.runInContext(fs.readFileSync(path.join(root, 'security.js'), 'utf8'), context);
  for (const value of ['javascript:alert(1)', 'https://attacker.example', '//attacker.example', 'data:text/html,test'])
    assert.equal(context.safeLocalReturnUrl(value), null);
  assert.equal(context.safeLocalReturnUrl('/seat-map.html?showtimeId=a%25b'), '/seat-map.html?showtimeId=a%25b');
  assert.equal(context.escapeHtml('<img src=x onerror="bad">'), '&lt;img src=x onerror=&quot;bad&quot;&gt;');
});

test('Payment result never trusts query-string success and booking requests use configured gateway', () => {
  const result = fs.readFileSync(path.join(root, 'payment-result.html'), 'utf8');
  assert.ok(!result.includes("params.get('status')"));
  assert.ok(result.includes("actualStatus = json.data.status"));
  assert.ok(source.includes('/api/v1/payments/orders/${order.id}'));
});
