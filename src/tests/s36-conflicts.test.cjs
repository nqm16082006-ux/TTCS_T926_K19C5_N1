const test = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const vm = require('node:vm');
const root = path.join(__dirname, '../EventTicketBooking.Api/wwwroot');
const overview = fs.readFileSync(path.join(root, 'overview.html'), 'utf8');
const scanner = fs.readFileSync(path.join(root, 'staff-scanner.html'), 'utf8');
const escapeHtml = value => String(value).replaceAll('&', '&amp;').replaceAll('<', '&lt;').replaceAll('>', '&gt;').replaceAll('"', '&quot;');

function reportHarness(fetch) {
  const panel = { innerHTML: '', open: false, style: {}, setAttribute() {}, addEventListener() {},
    showModal() { this.open = true; }, close() { this.open = false; } };
  const table = { addEventListener() {}, closest: () => ({ appendChild() {} }) };
  const context = vm.createContext({ document: { body: { appendChild() {} }, createElement: () => panel, getElementById: () => table },
    fetch, API_BASE: '', token: 'test', escapeHtml, window: { location: {} } });
  vm.runInContext(overview.slice(overview.indexOf('    let conflictShowtimeId'), overview.indexOf('    async function loadShowtimeSales')), context);
  return { panel, context };
}

test('S36 report displays both gates/timestamps and escapes server text', async () => {
  let requested;
  const { panel, context } = reportHarness(async url => {
    requested = url;
    return { ok: true, status: 200, json: async () => ({ success: true, data: {
      totalTickets: 1, totalConflicts: 1, page: 1, pageSize: 20,
      items: [{ ticketCode: '<script>bad</script>', validGate: 'A', validTime: '2026-10-10T12:00:05Z',
        conflictGate: '<img onerror=bad>', conflictTime: '2026-10-10T12:00:10Z' }]
    } }) };
  });
  await vm.runInContext('conflictShowtimeId = "show-1"; loadCheckInConflicts()', context);
  assert.equal(panel.open, true, 'Details must open visibly in a modal instead of below a long table');
  assert.match(requested, /show-1\/checkin-conflicts\?page=1&pageSize=20$/);
  assert.match(panel.innerHTML, /19:00:05/);
  assert.match(panel.innerHTML, /19:00:10/);
  assert.match(panel.innerHTML, /&lt;script&gt;/);
  assert.doesNotMatch(panel.innerHTML, /<img onerror/);
});

test('S36 report handles empty results and retry after error', async () => {
  let fail = true;
  const { panel, context } = reportHarness(async () => {
    if (fail) throw new Error('network');
    return { ok: true, json: async () => ({ success: true, data: { totalTickets: 0, totalConflicts: 0, page: 1, pageSize: 20, items: [] } }) };
  });
  await vm.runInContext('loadCheckInConflicts()', context);
  assert.match(panel.innerHTML, /role="alert"/);
  assert.match(panel.innerHTML, /data-conflict-action="refresh"/);
  fail = false;
  await vm.runInContext('loadCheckInConflicts()', context);
  assert.match(panel.innerHTML, /Không có lượt xung đột/);
});

test('S36 scanner reports conflict/rejection separately while clearing acknowledged scans', async () => {
  let queue = ['synced', 'conflict', 'rejected'].map((status, i) => ({ offlineScanId: 'id-' + i, status }));
  const alerts = [];
  const context = vm.createContext({ console, AbortController, setTimeout, clearTimeout,
    window: {}, token: 'test', document: { getElementById: () => null },
    isDeviceOffline: () => false, getOfflineQueue: () => queue, saveOfflineQueue: value => { queue = value; },
    updateQueueCounterUI() {}, alert: value => alerts.push(value),
    fetch: async () => ({ ok: true, json: async () => ({ results: queue.map(item => ({ ...item, isAckTerminal: true })) }) })
  });
  vm.runInContext(scanner.slice(scanner.indexOf('      let isSyncInProgress'), scanner.indexOf('      // LOAD TODAY SHOWS')), context);
  await context.window.syncOfflineQueue(true);
  assert.equal(queue.length, 0);
  assert.equal(alerts.length, 1);
  assert.match(alerts[0], /1 lượt hợp lệ/);
  assert.match(alerts[0], /1 lượt xung đột/);
  assert.match(alerts[0], /1 lượt bị từ chối/);
});

test('modified page inline scripts parse', () => {
  for (const html of [overview, scanner])
    for (const match of html.matchAll(/<script\b[^>]*>([\s\S]*?)<\/script>/gi))
      if (match[1].trim()) new vm.Script(match[1]);
});
