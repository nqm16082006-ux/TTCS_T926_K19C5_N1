const test = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const vm = require('node:vm');
const root = path.join(__dirname, '../EventTicketBooking.Api/wwwroot');
const source = fs.readFileSync(path.join(root, 'seat-map.html'), 'utf8');
const overview = fs.readFileSync(path.join(root, 'overview.html'), 'utf8');
function fn(name) {
  const start = source.search(new RegExp('(?:async )?function ' + name + '\\('));
  assert.ok(start >= 0, name);
  const tail = source.slice(start);
  const next = tail.slice(1).search(/\n    (?:async )?function /);
  return next < 0 ? tail : tail.slice(0, next + 1);
}
function overviewFn(name) {
  const start = overview.search(new RegExp('(?:async )?function ' + name + '\\('));
  assert.ok(start >= 0, name);
  const tail = overview.slice(start);
  const next = tail.slice(1).search(/\n    (?:async )?function /);
  return next < 0 ? tail : tail.slice(0, next + 1);
}
function reportFixture(fetchImpl) {
  const elements = new Map();
  function getElement(id) {
    if (!elements.has(id)) {
      const classes = new Set();
      elements.set(id, {
        innerHTML: '',
        textContent: '',
        classList: {
          add: (...values) => values.forEach(value => classes.add(value)),
          remove: (...values) => values.forEach(value => classes.delete(value)),
          contains: value => classes.has(value)
        }
      });
    }
    return elements.get(id);
  }
  const context = vm.createContext({
    Date, Intl, Number, String, console: { error() {} },
    API_BASE: '',
    token: 'access-token',
    window: { location: {} },
    document: {
      getElementById: getElement,
      querySelector: selector => selector === '#ovShowtimeSalesBody tr[data-showtime-id]' &&
        (elements.get('ovShowtimeSalesBody')?.innerHTML.includes('data-showtime-id') ?? false)
        ? {}
        : null
    },
    fetch: fetchImpl,
    escapeHtml: value => String(value).replace(/[&<>"']/g, character => ({
      '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;'
    })[character])
  });
  vm.runInContext(
    'let showtimeSalesRequestInFlight = false; let showtimeSalesRefreshPending = false;\n' +
    overviewFn('loadShowtimeSales') + overviewFn('renderShowtimeSales'),
    context
  );
  return { context, elements, getElement };
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

test('Showtime sales dashboard schedules an automatic refresh every 60 seconds', () => {
  const match = overview.match(/document\.addEventListener\('DOMContentLoaded', \(\) => \{([\s\S]*?)\n    \}\);/);
  assert.ok(match, 'dashboard initialization callback');
  let ready;
  let intervalCallback;
  let intervalMs;
  let initialLoads = 0;
  let reportLoads = 0;
  const context = vm.createContext({
    document: { addEventListener: (_event, callback) => { ready = callback; } },
    window: {
      setInterval: (callback, milliseconds) => {
        intervalCallback = callback;
        intervalMs = milliseconds;
      }
    },
    loadOverviewData: () => { initialLoads++; },
    loadShowtimeSales: () => { reportLoads++; }
  });

  vm.runInContext(`document.addEventListener('DOMContentLoaded', () => {${match[1]}\n});`, context);
  ready();
  assert.equal(initialLoads, 1);
  assert.equal(reportLoads, 1);
  assert.equal(intervalMs, 60_000);
  intervalCallback();
  assert.equal(reportLoads, 2);
});

test('Showtime sales dashboard renders ticket, hold, availability and per-tier revenue safely', async () => {
  let requestUrl;
  let requestOptions;
  const { context, elements } = reportFixture(async (url, options) => {
    requestUrl = url;
    requestOptions = options;
    return {
      ok: true,
      status: 200,
      json: async () => ({
        success: true,
        data: {
          generatedAt: '2026-10-07T12:00:00Z',
          showtimes: [{
            showtimeId: 'show-1',
            eventTitle: '<img src=x onerror=alert(1)>',
            startTime: '2026-10-08T12:00:00Z',
            ticketSoldCount: 3,
            heldSeatCount: 2,
            availableSeatCount: 5,
            revenueByCategory: [{
              name: '<VIP>',
              ticketSoldCount: 3,
              revenue: 125000
            }]
          }]
        }
      })
    };
  });

  await context.loadShowtimeSales();

  assert.equal(requestUrl, '/api/events/sales-by-showtime');
  assert.equal(requestOptions.cache, 'no-store');
  assert.equal(requestOptions.headers.Authorization, 'Bearer access-token');
  const table = elements.get('ovShowtimeSalesBody').innerHTML;
  assert.match(table, /&lt;img src=x onerror=alert\(1\)&gt;/);
  assert.match(table, /&lt;VIP&gt;/);
  assert.match(table, />3<\/td>/);
  assert.match(table, />2<\/td>/);
  assert.match(table, />5<\/td>/);
  assert.match(table, /3 vé/);
  assert.match(table, /125\.000/);
  assert.match(elements.get('ovShowtimeSalesUpdated').textContent, /^Cập nhật lúc/);
});

test('A scheduled report refresh is queued instead of being dropped while a request is active', async () => {
  let resolveFirstRequest;
  let requests = 0;
  const response = {
    ok: true,
    status: 200,
    json: async () => ({
      success: true,
      data: { generatedAt: '2026-10-07T12:00:00Z', showtimes: [] }
    })
  };
  const { context } = reportFixture(() => {
    requests++;
    return requests === 1
      ? new Promise(resolve => { resolveFirstRequest = resolve; })
      : Promise.resolve(response);
  });

  const firstRefresh = context.loadShowtimeSales();
  await context.loadShowtimeSales();
  assert.equal(requests, 1);

  resolveFirstRequest(response);
  await firstRefresh;
  await new Promise(resolve => setImmediate(resolve));
  assert.equal(requests, 2);
});

test('An invalid report timestamp does not replace the last valid report data', async () => {
  const { context, elements } = reportFixture(async () => ({
    ok: true,
    status: 200,
    json: async () => ({
      success: true,
      data: {
        generatedAt: 'invalid timestamp',
        showtimes: [{
          showtimeId: 'invalid',
          eventTitle: 'Must not replace existing data',
          revenueByCategory: []
        }]
      }
    })
  }));
  elements.set('ovShowtimeSalesBody', {
    innerHTML: '<tr data-showtime-id="previous">Previous valid report</tr>'
  });

  await context.loadShowtimeSales();

  assert.equal(
    elements.get('ovShowtimeSalesBody').innerHTML,
    '<tr data-showtime-id="previous">Previous valid report</tr>'
  );
  assert.match(elements.get('ovShowtimeSalesUpdated').textContent, /Không thể cập nhật báo cáo/);
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
