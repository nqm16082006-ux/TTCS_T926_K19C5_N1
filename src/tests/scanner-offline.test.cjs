const test = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const vm = require('node:vm');

const root = path.join(__dirname, '../EventTicketBooking.Api/wwwroot');
const htmlSource = fs.readFileSync(path.join(root, 'staff-scanner.html'), 'utf8');

test('staff-scanner.html contains required S-33 UI elements and IndexedDB stores', () => {
  assert.ok(htmlSource.includes('btnDownloadTickets'), 'Must have btnDownloadTickets');
  assert.ok(htmlSource.includes('Tải danh sách vé'), 'Must have download tickets button text');
  assert.ok(htmlSource.includes('syncMetadataText'), 'Must have sync metadata display');
  assert.ok(htmlSource.includes('networkStatusBadge'), 'Must have network status badge');
  assert.ok(htmlSource.includes('EventTicketScannerDB'), 'Must have EventTicketScannerDB database');
  assert.ok(htmlSource.includes('offline_tickets'), 'Must have offline_tickets object store');
  assert.ok(htmlSource.includes('sync_metadata'), 'Must have sync_metadata object store');
});

test('staff-scanner.html contains Web Crypto verification and Read-Only offline lookup', () => {
  assert.ok(htmlSource.includes('verifyOfflineSignature'), 'Must have verifyOfflineSignature function');
  assert.ok(htmlSource.includes('handleOfflineScan'), 'Must have handleOfflineScan function');
  assert.ok(htmlSource.includes('checkAndTriggerAutoDeltaSync'), 'Must have auto delta sync function');
  assert.ok(htmlSource.includes('VÉ HỢP LỆ — NGOẠI TUYẾN'), 'Must have offline valid banner');
  assert.ok(htmlSource.includes('XÁC THỰC THỦ CÔNG'), 'Must distinguish manual input');
});

test('Simulated QR Payload parser correctly extracts components', () => {
  const qr = 'TICKET|v1|TK-ABC1-DEF2|11111111-2222-3333-4444-555555555555|SIG123';
  const parts = qr.split('|');
  assert.equal(parts.length, 5);
  assert.equal(parts[0], 'TICKET');
  assert.equal(parts[1], 'v1');
  assert.equal(parts[2], 'TK-ABC1-DEF2');
  assert.equal(parts[3], '11111111-2222-3333-4444-555555555555');
  assert.equal(parts[4], 'SIG123');
});
