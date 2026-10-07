const test = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const vm = require('node:vm');
const source = fs.readFileSync(path.join(__dirname, '../EventTicketBooking.Api/wwwroot/seat-map.html'), 'utf8');
function fn(name) {
  const start = source.search(new RegExp('(?:async )?function ' + name + '\\('));
  assert.ok(start >= 0, name);
  const tail = source.slice(start);
  const next = tail.slice(1).search(/\n    (?:async )?function /);
  return tail.slice(0, next + 1);
}
function fixture() {
  let now = 100000;
  let interval;
  const hidden = new Set(['hidden']);
  const label = { textContent: '' };
  const button = { disabled: false, innerHTML: '<span>Tiếp tục</span>', querySelector: () => label };
  const notice = { classList: { toggle(name, value) { value ? hidden.add(name) : hidden.delete(name); } } };
  const message = { textContent: '' };
  const elements = { btnPrimaryAction: button, bookingRateLimitNotice: notice, bookingRateLimitMessage: message };
  const calls = [];
  const context = vm.createContext({
    console, Number, Math, Map, Set, Array, JSON, Date: { now: () => now, parse: Date.parse },
    document: { getElementById: id => elements[id] || null },
    setInterval: callback => { interval = callback; return 1; }, clearInterval: () => { interval = null; },
    getValidToken: () => 'token',
    sessionStorage: { removeItem: () => { throw new Error('Must preserve saved holds'); } },
    loadSeats: () => { throw new Error('Must not reload seats on 429'); },
    alert: () => { throw new Error('Must use inline notice'); },
    window: { location: { href: 'seat-map.html' } },
    fetch: async url => { calls.push(url); return { status: 429, ok: false,
      headers: { get: () => null }, json: async () => ({ code: 'RATE_LIMITED', retryAfterSeconds: 30 }) }; }
  });
  vm.runInContext(`let bookingActionInFlight = false; const bookingRateLimits = new Map();
    let bookingRateLimitTimer = null; const selectedSeatIds = new Set(['draft']);
    const myHeldSeatIds = new Set(['held']); const API_BASE = ''; const showtimeId = 'show';`, context);
  vm.runInContext(['currentBookingPolicy', 'bookingRetrySeconds', 'updateBookingRateLimitUI',
    'handleBookingRateLimit', 'handlePrimaryAction', 'performPrimaryAction', 'createOrderAndProceed'].map(fn).join('\n'), context);
  context.updateCartPanel = () => context.updateBookingRateLimitUI();
  return { context, button, label, message, hidden, calls,
    advance(seconds) { now += seconds * 1000; interval?.(); }, hasTimer: () => !!interval };
}
const response = (status, header = null) => ({ status, headers: { get: () => header } });

test('429 during hold preserves selections, blocks repeated clicks and unlocks without auto retry', async () => {
  const f = fixture();
  await f.context.handlePrimaryAction();
  assert.equal(f.calls.length, 1);
  assert.equal(f.button.disabled, true);
  assert.match(f.label.textContent, /30 giây/);
  assert.match(f.message.textContent, /giữ vé/);
  assert.equal(f.hidden.has('hidden'), false);
  assert.equal(vm.runInContext('selectedSeatIds.has("draft") && myHeldSeatIds.has("held")', f.context), true);
  await f.context.handlePrimaryAction();
  assert.equal(f.calls.length, 1);
  f.advance(10);
  assert.match(f.label.textContent, /20 giây/);
  f.advance(20);
  assert.equal(f.button.disabled, false);
  assert.equal(f.hidden.has('hidden'), true);
  assert.equal(f.hasTimer(), false);
  assert.equal(f.calls.length, 1);
  await f.context.handlePrimaryAction();
  assert.equal(f.calls.length, 2);
});

test('429 during order creation preserves held seats and never starts payment', async () => {
  const f = fixture();
  vm.runInContext('selectedSeatIds.clear()', f.context);
  await f.context.handlePrimaryAction();
  assert.deepEqual(f.calls, ['/api/v1/orders/showtimes/show']);
  assert.equal(f.button.disabled, true);
  assert.match(f.message.textContent, /tạo đơn/);
  assert.equal(vm.runInContext('myHeldSeatIds.has("held")', f.context), true);
  assert.equal(f.context.window.location.href, 'seat-map.html');
  await f.context.createOrderAndProceed();
  assert.equal(f.calls.length, 1);
});

test('Only the limited operation is disabled and cart changes cannot bypass its cooldown', () => {
  const f = fixture();
  f.context.handleBookingRateLimit(response(429), { retryAfterSeconds: 30 }, 'seat-hold');
  assert.equal(f.button.disabled, true);
  vm.runInContext('selectedSeatIds.clear()', f.context);
  f.context.updateCartPanel();
  assert.equal(f.button.disabled, false);
  vm.runInContext('selectedSeatIds.add("other")', f.context);
  f.context.updateCartPanel();
  assert.equal(f.button.disabled, true);
  vm.runInContext('selectedSeatIds.clear(); myHeldSeatIds.clear()', f.context);
  f.advance(30);
  assert.equal(f.button.disabled, true);
});

test('Missing/malformed JSON uses Retry-After or a safe default and creates only one timer', () => {
  const f = fixture();
  f.context.handleBookingRateLimit(response(429, '12'), null, 'seat-hold');
  assert.equal(f.context.bookingRetrySeconds('seat-hold'), 12);
  f.context.handleBookingRateLimit(response(429, 'bad'), { retryAfterSeconds: -1 }, 'order-create');
  assert.equal(f.context.bookingRetrySeconds('order-create'), 60);
  f.advance(12);
  assert.equal(f.hasTimer(), true);
  f.advance(48);
  assert.equal(f.hasTimer(), false);
});

test('Redis outage displays a short cooldown; unrelated errors keep their existing handling', () => {
  const f = fixture();
  assert.equal(f.context.handleBookingRateLimit(response(503), { code: 'RATE_LIMIT_UNAVAILABLE', retryAfterSeconds: 5 }, 'seat-hold'), true);
  assert.match(f.message.textContent, /Hệ thống đang bận/);
  assert.equal(f.context.handleBookingRateLimit(response(409), {}, 'seat-hold'), false);
  assert.equal(f.context.handleBookingRateLimit(response(503), {}, 'seat-hold'), false);
  f.advance(5);
  assert.equal(f.button.disabled, false);
});
