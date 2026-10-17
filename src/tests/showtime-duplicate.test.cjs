const test = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const vm = require('node:vm');
const path = require('node:path');
const root = path.join(__dirname, '../EventTicketBooking.Api/wwwroot');
const script = fs.readFileSync(path.join(root, 'showtime-duplicate.js'), 'utf8');

function harness() {
  const form = { elements: { salesEndTime: { value: '' }, showtimeName: { value: '' }, start: { value: '' }, end: { value: '' } } };
  const alert = { textContent: '' };
  const close = {};
  const buttons = [{ disabled: false }, { disabled: false }];
  const dialog = { style: {}, setAttribute() {}, addEventListener() {}, showModal() { this.open = true; }, close() { this.open = false; },
    querySelector: selector => selector === 'form' ? form : selector === '[role=alert]' ? alert : close,
    querySelectorAll: () => buttons };
  const calls = [];
  let resolveResponse;
  const context = vm.createContext({
    document: { createElement: () => dialog, body: { appendChild() {} } },
    currentEventId: 'event-1', API_BASE: '', token: 'token',
    currentShowtimes: [{ id: 'show-1', startTime: '2026-10-14T12:00:00Z', endTime: '2026-10-14T14:00:00Z' }],
    formatDateTimeLocal: value => value.slice(0, 16), loadShowtimes: async id => calls.push({ refresh: id }),
    fetch: (url, options) => { calls.push({ url, options }); return new Promise(resolve => { resolveResponse = resolve; }); }
  });
  vm.runInContext(script, context);
  context.openDuplicateShowtime('show-1');
  return { form, alert, dialog, buttons, calls, respond: response => resolveResponse(response) };
}

test('S58 duplicates the selected showtime in the same event and prevents double submit', async () => {
  const h = harness();
  assert.equal(h.dialog.open, true);
  assert.match(h.form.elements.start.value, /^2026-10-15/);
  const event = { preventDefault() {} };
  h.form.elements.showtimeName.value = 'Đêm Chung kết';
  const pending = h.form.onsubmit(event);
  await h.form.onsubmit(event);
  assert.equal(h.calls.length, 1);
  assert.equal(h.calls[0].url, '/api/events/event-1/showtimes/show-1/duplicate');
  const body = JSON.parse(h.calls[0].options.body);
  assert.equal(body.name, 'Đêm Chung kết');
  assert.ok(new Date(body.endTime) > new Date(body.startTime));
  assert.ok(h.buttons.every(button => button.disabled));
  h.respond({ ok: true, json: async () => ({ success: true, data: { id: 'new-show' } }) });
  await pending;
  assert.equal(h.dialog.open, false);
  assert.equal(h.calls[1].refresh, 'event-1');
});

test('S58 validates the schedule before POST and keeps the form open for API errors', async () => {
  const h = harness();
  h.form.elements.end.value = h.form.elements.start.value;
  await h.form.onsubmit({ preventDefault() {} });
  assert.equal(h.calls.length, 0);
  assert.match(h.alert.textContent, /kết thúc/);
  h.form.elements.end.value = '2026-10-15T16:00';
  const pending = h.form.onsubmit({ preventDefault() {} });
  h.respond({ ok: false, json: async () => ({ success: false, message: 'Không có quyền' }) });
  await pending;
  assert.equal(h.dialog.open, true);
  assert.equal(h.alert.textContent, 'Không có quyền');
  assert.ok(h.buttons.every(button => !button.disabled));
});

test('S58 replaces event duplication with showtime duplication and pages parse', () => {
  for (const name of ['events.html', 'event-detail.html']) {
    const html = fs.readFileSync(path.join(root, name), 'utf8');
    for (const match of html.matchAll(/<script\b[^>]*>([\s\S]*?)<\/script>/gi))
      if (match[1].trim()) new vm.Script(match[1]);
    if (name === 'events.html') assert.doesNotMatch(html, /duplicateEvent|Nhân bản sự kiện/);
    else assert.match(html, /openDuplicateShowtime/);
  }
});
