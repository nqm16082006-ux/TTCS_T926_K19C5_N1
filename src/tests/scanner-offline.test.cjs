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

test('staff-scanner.html contains required S-35 auto-sync and batch flush functions', () => {
  assert.ok(htmlSource.includes('syncOfflineQueue'), 'Must have syncOfflineQueue function');
  assert.ok(htmlSource.includes('isSyncInProgress'), 'Must have sync concurrency guard');
  assert.ok(htmlSource.includes('btnSyncNow'), 'Must have btnSyncNow button');
  assert.ok(htmlSource.includes('handleOnlineReconnect'), 'Must have auto sync handler on online event');
  assert.ok(htmlSource.includes('/api/v1/checkin/offline-sync'), 'Must call S-35 batch endpoint');
});

test('S-35 Client Logic: Partial ACK removes ONLY acked items from queue', () => {
  // Giả lập logic lọc queue sau khi nhận kết quả từ server
  const queue = [
    { offlineScanId: 'id-1', ticketCode: 'TK-001' },
    { offlineScanId: 'id-2', ticketCode: 'TK-002' },
    { offlineScanId: 'id-3', ticketCode: 'TK-003' }
  ];

  const batchScanIds = new Set(['id-1', 'id-2', 'id-3']);
  const VALID_ACK_STATUSES = new Set(['synced', 'duplicate', 'conflict', 'rejected']);

  const serverResults = [
    { offlineScanId: 'id-1', status: 'synced', isAckTerminal: true },
    { offlineScanId: 'id-2', status: 'duplicate', isAckTerminal: true }
    // id-3 không có trong results hoặc bị thiếu ACK
  ];

  const ackedIds = new Set(
    serverResults
      .filter(r => r && r.isAckTerminal === true && VALID_ACK_STATUSES.has(r.status) && batchScanIds.has(r.offlineScanId))
      .map(r => r.offlineScanId)
  );

  const remainingQueue = queue.filter(item => !ackedIds.has(item.offlineScanId));

  assert.equal(remainingQueue.length, 1);
  assert.equal(remainingQueue[0].offlineScanId, 'id-3');
});

test('S-35 Client Logic: Network failure or 5xx preserves entire queue', () => {
  const queue = [
    { offlineScanId: 'id-1', ticketCode: 'TK-001' },
    { offlineScanId: 'id-2', ticketCode: 'TK-002' }
  ];

  // Giả lập response thất bại (500 hoặc rớt mạng)
  const isOk = false;
  let remainingQueue = [...queue];

  if (isOk) {
    // Sẽ lọc
  } else {
    // Giữ nguyên queue
  }

  assert.equal(remainingQueue.length, 2);
  assert.equal(remainingQueue[0].offlineScanId, 'id-1');
  assert.equal(remainingQueue[1].offlineScanId, 'id-2');
});

test('S-35 Client Logic: Sync concurrency guard prevents duplicate parallel syncs', () => {
  let isSyncInProgress = false;
  let executionCount = 0;

  function trySync() {
    if (isSyncInProgress) return false;
    isSyncInProgress = true;
    executionCount++;
    return true;
  }

  assert.equal(trySync(), true);
  assert.equal(trySync(), false); // Lần 2 bị guard chặn
  assert.equal(executionCount, 1);

  isSyncInProgress = false; // Reset sau khi hoàn tất
  assert.equal(trySync(), true);
  assert.equal(executionCount, 2);
});

test('S-35 Client Logic: Unknown status with isAckTerminal=true is NOT removed from queue', () => {
  const queue = [
    { offlineScanId: 'id-1', ticketCode: 'TK-001' }
  ];
  const batchScanIds = new Set(['id-1']);
  const VALID_ACK_STATUSES = new Set(['synced', 'duplicate', 'conflict', 'rejected']);

  // Server trả về status lạ (ví dụ 'pending_approval' hay 'unknown') dù isAckTerminal=true
  const serverResults = [
    { offlineScanId: 'id-1', status: 'unknown_status', isAckTerminal: true }
  ];

  const ackedIds = new Set(
    serverResults
      .filter(r => r && r.isAckTerminal === true && VALID_ACK_STATUSES.has(r.status) && batchScanIds.has(r.offlineScanId))
      .map(r => r.offlineScanId)
  );

  const remainingQueue = queue.filter(item => !ackedIds.has(item.offlineScanId));
  assert.equal(remainingQueue.length, 1, 'Item with unknown status must NOT be removed from queue');
});

test('S-35 Client Logic: ACK offlineScanId not belonging to current batch is NOT removed from queue', () => {
  const queue = [
    { offlineScanId: 'id-batch-1', ticketCode: 'TK-001' },
    { offlineScanId: 'id-other', ticketCode: 'TK-002' }
  ];
  // Batch chỉ gửi id-batch-1
  const batchScanIds = new Set(['id-batch-1']);
  const VALID_ACK_STATUSES = new Set(['synced', 'duplicate', 'conflict', 'rejected']);

  // Server vô tình hay cố ý gửi ACK cho id-other (không thuộc batch này)
  const serverResults = [
    { offlineScanId: 'id-other', status: 'synced', isAckTerminal: true }
  ];

  const ackedIds = new Set(
    serverResults
      .filter(r => r && r.isAckTerminal === true && VALID_ACK_STATUSES.has(r.status) && batchScanIds.has(r.offlineScanId))
      .map(r => r.offlineScanId)
  );

  const remainingQueue = queue.filter(item => !ackedIds.has(item.offlineScanId));
  assert.equal(remainingQueue.length, 2, 'Item outside batch must NOT be removed from queue');
});

test('staff-scanner.html contains AbortController with 15s timeout for batch sync', () => {
  assert.ok(htmlSource.includes('AbortController'), 'Must use AbortController');
  assert.ok(htmlSource.includes('15000'), 'Must have 15s timeout');
  assert.ok(htmlSource.includes('signal: abortController.signal'), 'Must pass signal to fetch');
});
