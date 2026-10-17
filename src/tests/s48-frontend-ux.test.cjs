const test = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const vm = require('node:vm');

const root = path.join(__dirname, '../EventTicketBooking.Api/wwwroot');
const orderDetailsHtml = fs.readFileSync(path.join(root, 'order-details.html'), 'utf8');
const auditorHtml = fs.readFileSync(path.join(root, 'auditor.html'), 'utf8');
const myOrdersHtml = fs.readFileSync(path.join(root, 'my-orders.html'), 'utf8');
const orderAuditLogsHtml = fs.readFileSync(path.join(root, 'order-audit-logs.html'), 'utf8');

function extractScript(html) {
  let combined = '';
  for (const match of html.matchAll(/<script\b([^>]*)>([\s\S]*?)<\/script>/gi)) {
    if (/\bsrc=|application\/ld\+json/.test(match[1])) continue;
    combined += match[2] + '\n';
  }
  return combined;
}

function createDOMContext(customStorage = {}, search = '') {
  const elements = new Map();
  function createElement() {
    const classes = new Set();
    const attrs = new Map();
    const node = {
      textContent: '',
      innerHTML: '',
      title: '',
      href: '',
      style: {},
      classList: {
        add: (...vals) => vals.forEach(v => classes.add(v)),
        remove: (...vals) => vals.forEach(v => classes.delete(v)),
        toggle: (v, force) => {
          if (force === undefined) {
            if (classes.has(v)) classes.delete(v); else classes.add(v);
          } else if (force) classes.add(v); else classes.delete(v);
        },
        contains: v => classes.has(v)
      },
      setAttribute: (k, v) => attrs.set(k, String(v)),
      getAttribute: k => attrs.get(k) || null,
      removeAttribute: k => attrs.delete(k),
      appendChild: child => { child.parentNode = node; return child; },
      remove: () => {},
      removeChild: () => {},
      focus: () => {}
    };
    return node;
  }

  const localStorageStore = new Map(Object.entries(customStorage));
  const storage = {
    getItem: k => localStorageStore.get(k) || null,
    setItem: (k, v) => localStorageStore.set(k, String(v)),
    removeItem: k => localStorageStore.delete(k),
    clear: () => localStorageStore.clear()
  };

  const windowObj = {
    location: {
      search: search,
      href: 'http://localhost/order-details.html' + search,
      pathname: '/order-details.html'
    },
    history: {
      replaceState: (state, title, url) => {
        windowObj.location.href = url;
        const qIdx = url.indexOf('?');
        windowObj.location.search = qIdx >= 0 ? url.slice(qIdx) : '';
      }
    }
  };

  const documentObj = {
    body: createElement(),
    createElement: () => createElement(),
    getElementById: id => {
      if (!elements.has(id)) elements.set(id, createElement());
      return elements.get(id);
    },
    querySelectorAll: () => [],
    querySelector: () => null,
    addEventListener: () => {}
  };

  const navigatorObj = {
    clipboard: {
      writeText: async text => text
    }
  };

  const context = vm.createContext({
    console,
    Date,
    Intl,
    Number,
    String,
    Boolean,
    Math,
    URLSearchParams,
    window: windowObj,
    location: windowObj.location,
    history: windowObj.history,
    document: documentObj,
    localStorage: storage,
    navigator: navigatorObj,
    alert: () => {},
    confirm: () => true,
    fetch: async () => ({ ok: true, json: async () => ({ data: {} }) })
  });

  return { context, elements, storage, windowObj };
}

// =========================================================================
// 1. Order details accepts canonical ?id=
// =========================================================================
test('1. order-details accepts canonical ?id=<GUID>', () => {
  const testGuid = '3fa85f64-5717-4562-b3fc-2c963f66afa6';
  const { context } = createDOMContext({}, `?id=${testGuid}`);
  const script = extractScript(orderDetailsHtml) + '\nwindow.testOrderId = orderId;';
  vm.runInContext(script, context);
  assert.equal(context.window.testOrderId, testGuid);
});

// =========================================================================
// 2. Backward compatible ?orderId=
// =========================================================================
test('2. order-details is backward compatible with ?orderId=<GUID> and normalizes to ?id=', () => {
  const testGuid = 'e9c8f01b-9441-47fb-9fa9-3b624317f0a1';
  const { context, windowObj } = createDOMContext({ access_token: 'test_token' }, `?orderId=${testGuid}`);
  const script = extractScript(orderDetailsHtml) + '\nwindow.testOrderId = orderId;';
  vm.runInContext(script, context);
  assert.equal(context.window.testOrderId, testGuid);
  assert.ok(windowObj.location.href.includes(`order-details.html?id=${testGuid}`));
});

// =========================================================================
// 3. Missing both params -> correct error
// =========================================================================
test('3. missing both params -> correct error message shown', () => {
  const { context, elements } = createDOMContext({}, '');
  const script = extractScript(orderDetailsHtml);
  vm.runInContext(script, context);
  const errMsg = elements.get('errorMessage').textContent;
  assert.match(errMsg, /Không tìm thấy mã đơn hàng trong URL/);
  assert.ok(elements.get('orderContent').classList.contains('hidden'));
});

// =========================================================================
// 4. Refund card renders with amount and shortened copyable ID
// =========================================================================
test('4. refund card renders when order has refund', () => {
  const { context, elements } = createDOMContext({}, '?id=3fa85f64-5717-4562-b3fc-2c963f66afa6');
  const script = extractScript(orderDetailsHtml);
  vm.runInContext(script, context);

  const order = {
    id: '3fa85f64-5717-4562-b3fc-2c963f66afa6',
    status: 'Cancelled',
    refundStatus: 'Processing',
    refundAmount: 500000,
    refundRequestId: '12345678-abcd-ef01-2345-6789abcdef01'
  };

  context.renderRefundInfo(order);

  assert.ok(!elements.get('refundDetailsContent').classList.contains('hidden'));
  assert.ok(elements.get('refundEmptyState').classList.contains('hidden'));
  assert.match(elements.get('cardRefundAmount').textContent, /500\.000/);
  assert.match(elements.get('cardRefundId').textContent, /12345678\.\.\./);
  assert.equal(elements.get('cardRefundId').getAttribute('data-full-id'), '12345678-abcd-ef01-2345-6789abcdef01');
});

// =========================================================================
// 5. RetryScheduled renders retry info
// =========================================================================
test('5. RetryScheduled renders retry info for customer and auditor', () => {
  // Customer view (default)
  const { context: custContext, elements: custElements } = createDOMContext(
    { user_info: JSON.stringify({ roles: ['Customer'] }) },
    '?id=3fa85f64-5717-4562-b3fc-2c963f66afa6'
  );
  vm.runInContext(extractScript(orderDetailsHtml), custContext);

  const retryOrder = {
    id: '3fa85f64-5717-4562-b3fc-2c963f66afa6',
    status: 'Cancelled',
    refundStatus: 'RetryScheduled',
    refundAmount: 300000,
    refundRetryCount: 1,
    refundAttemptCount: 2,
    refundNextRetryAt: '2026-10-18T10:30:00Z',
    refundLastAttemptedAt: '2026-10-18T10:15:00Z'
  };

  custContext.renderRefundInfo(retryOrder);
  assert.match(custElements.get('refundSectionStatusBadge').textContent, /Đang thử lại/);
  assert.match(custElements.get('refundCustomerDesc').textContent, /tự động xếp lịch thử lại/);
  assert.ok(custElements.get('refundTechnicalDetails').classList.contains('hidden'));

  // Staff / Auditor view
  const { context: staffContext, elements: staffElements } = createDOMContext(
    { user_info: JSON.stringify({ roles: ['Auditor'] }) },
    '?id=3fa85f64-5717-4562-b3fc-2c963f66afa6'
  );
  vm.runInContext(extractScript(orderDetailsHtml), staffContext);
  staffContext.renderRefundInfo(retryOrder);

  assert.ok(!staffElements.get('refundTechnicalDetails').classList.contains('hidden'));
  assert.equal(String(staffElements.get('techRetryCount').textContent), '1');
  assert.equal(String(staffElements.get('techAttemptCount').textContent), '2');
});

// =========================================================================
// 6. ManualReviewRequired renders gateway reason
// =========================================================================
test('6. ManualReviewRequired renders friendly state for customer and gateway reason for auditor', () => {
  const manualOrder = {
    id: '3fa85f64-5717-4562-b3fc-2c963f66afa6',
    status: 'Cancelled',
    refundStatus: 'ManualReviewRequired',
    refundAmount: 1200000,
    refundRetryCount: 3,
    refundAttemptCount: 3,
    refundErrorCode: 'GATEWAY_TIMEOUT',
    refundErrorMessage: 'Cổng thanh toán phản hồi Timeout sau 3 lần thử lại.'
  };

  // Staff / Auditor
  const { context: staffContext, elements: staffElements } = createDOMContext(
    { user_info: JSON.stringify({ roles: ['Auditor'] }) },
    '?id=3fa85f64-5717-4562-b3fc-2c963f66afa6'
  );
  vm.runInContext(extractScript(orderDetailsHtml), staffContext);
  staffContext.renderRefundInfo(manualOrder);

  assert.match(staffElements.get('refundSectionStatusBadge').textContent, /Chờ kế toán xử lý/);
  assert.equal(staffElements.get('techErrorCode').textContent, 'GATEWAY_TIMEOUT');
  assert.match(staffElements.get('techErrorMessage').textContent, /Timeout sau 3 lần/);

  // Customer
  const { context: custContext, elements: custElements } = createDOMContext(
    { user_info: JSON.stringify({ roles: ['Customer'] }) },
    '?id=3fa85f64-5717-4562-b3fc-2c963f66afa6'
  );
  vm.runInContext(extractScript(orderDetailsHtml), custContext);
  custContext.renderRefundInfo(manualOrder);

  assert.match(custElements.get('refundSectionStatusBadge').textContent, /Cần bộ phận hỗ trợ kiểm tra/);
  assert.match(custElements.get('refundCustomerDesc').textContent, /chuyển sang bộ phận kế toán/);
  assert.ok(custElements.get('refundTechnicalDetails').classList.contains('hidden'));
});

// =========================================================================
// 7. Succeeded renders success state
// =========================================================================
test('7. Succeeded renders success state badge and customer title', () => {
  const { context, elements } = createDOMContext(
    { user_info: JSON.stringify({ roles: ['Customer'] }) },
    '?id=3fa85f64-5717-4562-b3fc-2c963f66afa6'
  );
  vm.runInContext(extractScript(orderDetailsHtml), context);

  const successOrder = {
    id: '3fa85f64-5717-4562-b3fc-2c963f66afa6',
    status: 'Cancelled',
    refundStatus: 'Succeeded',
    refundAmount: 450000
  };

  context.renderRefundInfo(successOrder);
  assert.match(elements.get('refundSectionStatusBadge').textContent, /Đã hoàn tiền/);
  assert.equal(elements.get('refundCustomerTitle').textContent, 'Đã hoàn tiền thành công');
});

// =========================================================================
// 8. No refund -> empty state without red error
// =========================================================================
test('8. no refund -> friendly empty state without error', () => {
  const { context, elements } = createDOMContext({}, '?id=3fa85f64-5717-4562-b3fc-2c963f66afa6');
  vm.runInContext(extractScript(orderDetailsHtml), context);

  const normalOrder = {
    id: '3fa85f64-5717-4562-b3fc-2c963f66afa6',
    status: 'Paid',
    totalAmount: 200000
  };

  context.renderRefundInfo(normalOrder);
  assert.ok(!elements.get('refundEmptyState').classList.contains('hidden'));
  assert.ok(elements.get('refundDetailsContent').classList.contains('hidden'));
  assert.ok(orderDetailsHtml.includes('Đơn hàng này chưa có yêu cầu hoàn tiền.'));
});

// =========================================================================
// 9. Unsafe server text escaped
// =========================================================================
test('9. unsafe server text is escaped (XSS protection)', () => {
  const { context } = createDOMContext({}, '?id=test');
  vm.runInContext(extractScript(orderDetailsHtml), context);

  const malicious = '<script>alert("XSS")</script>&foo=\'bar\'';
  const escaped = context.escapeHtml(malicious);
  assert.ok(!escaped.includes('<script>'));
  assert.ok(!escaped.includes('</script>'));
  assert.ok(escaped.includes('&lt;script&gt;'));
  assert.ok(escaped.includes('&quot;'));
  assert.ok(escaped.includes('&#039;'));
});

// =========================================================================
// 10. Auditor manual list renders 11 columns with required S-48 fields
// =========================================================================
test('10. auditor manual list renders 11 columns with required S-48 data', () => {
  const { context, elements } = createDOMContext(
    { user_info: JSON.stringify({ roles: ['Auditor'] }) },
    ''
  );
  vm.runInContext(extractScript(auditorHtml), context);

  const sampleList = [
    {
      id: 'ref-1111-2222-3333-4444',
      orderId: 'ord-5555-6666-7777-8888',
      orderCode: 'ORD998877',
      amount: 600000,
      status: 'ManualReviewRequired',
      retryCount: 3,
      attemptCount: 3,
      maxRetries: 3,
      errorCode: 'TIMEOUT',
      errorMessage: 'Gateway Timeout',
      lastAttemptedAt: '2026-10-17T14:00:00Z',
      nextRetryAt: null,
      createdAt: '2026-10-17T13:00:00Z',
      updatedAt: '2026-10-17T14:05:00Z'
    }
  ];

  context.rawRefundsList = sampleList;
  context.updateMetricsAndBadges(sampleList);
  context.renderRefundsTable(sampleList);

  const tableHtml = elements.get('refundsTableBody').innerHTML;
  assert.ok(tableHtml.includes('ref-1111...'));
  assert.ok(tableHtml.includes('#ORD998877'));
  assert.ok(tableHtml.includes('600.000'));
  assert.ok(tableHtml.includes('TIMEOUT'));
  assert.ok(tableHtml.includes('Gateway Timeout'));
  assert.ok(tableHtml.includes('order-details.html?id=ord-5555-6666-7777-8888'));
  assert.ok(tableHtml.includes('openRefundDetailModal'));
});

// =========================================================================
// 11. Link from refund -> order-details uses canonical ?id=
// =========================================================================
test('11. all links from refund tables and modals to order-details use canonical ?id=', () => {
  // Check auditor.html
  assert.ok(auditorHtml.includes('order-details.html?id='));
  assert.ok(!auditorHtml.includes('order-details.html?orderId='));

  // Check my-orders.html
  assert.ok(myOrdersHtml.includes('order-details.html?id='));
  assert.ok(!myOrdersHtml.includes('order-details.html?orderId='));

  // Check order-audit-logs.html
  assert.ok(orderAuditLogsHtml.includes('order-details.html?id='));
  assert.ok(!orderAuditLogsHtml.includes('order-details.html?orderId='));
});

// =========================================================================
// 12. Customer does not see internal accounting actions
// =========================================================================
test('12. customer view does not see internal accounting actions or tech details', () => {
  const { context, elements } = createDOMContext(
    { user_info: JSON.stringify({ roles: ['Customer'] }) },
    '?id=3fa85f64-5717-4562-b3fc-2c963f66afa6'
  );
  vm.runInContext(extractScript(orderDetailsHtml), context);

  assert.equal(context.isStaffOrAdmin(), false);

  const order = {
    id: '3fa85f64-5717-4562-b3fc-2c963f66afa6',
    status: 'Cancelled',
    refundStatus: 'ManualReviewRequired',
    refundAmount: 500000,
    refundErrorCode: 'SECRET_PAYMENT_EXCEPTION'
  };

  context.renderRefundInfo(order);

  // Technical box hidden for customer
  assert.ok(elements.get('refundTechnicalDetails').classList.contains('hidden'));

  // No mock actions for customer
  assert.ok(elements.get('refundMockActionContainer').classList.contains('hidden'));
});

// =========================================================================
// 14. Auditor table renders action buttons strictly according to status
// =========================================================================
test('14. auditor table renders action buttons strictly according to status', () => {
  const { context, elements } = createDOMContext(
    { user_info: JSON.stringify({ roles: ['Auditor'] }) },
    ''
  );
  vm.runInContext(extractScript(auditorHtml), context);

  // 1. ManualReviewRequired has Tiếp nhận, Chi tiết, Xem đơn; NO Hoàn tất
  const manualItem = [{
    id: 'ref-manual-1',
    orderId: 'ord-1',
    orderCode: 'ORD001',
    amount: 300000,
    status: 'ManualReviewRequired',
    retryCount: 3,
    attemptCount: 3
  }];
  context.rawRefundsList = manualItem;
  context.renderRefundsTable(manualItem);
  let html = elements.get('refundsTableBody').innerHTML;
  assert.ok(html.includes('Tiếp nhận'), 'ManualReviewRequired must have Tiếp nhận button');
  assert.ok(html.includes('openAssignModal'), 'ManualReviewRequired must open assign modal');
  assert.ok(html.includes('Chi tiết'), 'ManualReviewRequired must have Chi tiết button');
  assert.ok(html.includes('Xem đơn'), 'ManualReviewRequired must have Xem đơn button');
  assert.ok(!html.includes('openResolveModal'), 'ManualReviewRequired must NOT have Hoàn tất button');

  // 2. AccountingProcessing has Hoàn tất, Chi tiết, Xem đơn; NO Tiếp nhận
  const processingItem = [{
    id: 'ref-proc-1',
    orderId: 'ord-2',
    orderCode: 'ORD002',
    amount: 450000,
    status: 'AccountingProcessing',
    retryCount: 3,
    attemptCount: 3
  }];
  context.rawRefundsList = processingItem;
  context.renderRefundsTable(processingItem);
  html = elements.get('refundsTableBody').innerHTML;
  assert.ok(html.includes('Hoàn tất'), 'AccountingProcessing must have Hoàn tất button');
  assert.ok(html.includes('openResolveModal'), 'AccountingProcessing must open resolve modal');
  assert.ok(html.includes('Chi tiết'), 'AccountingProcessing must have Chi tiết button');
  assert.ok(html.includes('Xem đơn'), 'AccountingProcessing must have Xem đơn button');
  assert.ok(!html.includes('openAssignModal'), 'AccountingProcessing must NOT have Tiếp nhận button');

  // 3. AccountingResolved and Succeeded have NO mutation action (chỉ Chi tiết, Xem đơn)
  const resolvedItems = [
    { id: 'ref-res-1', orderId: 'ord-3', orderCode: 'ORD003', amount: 200000, status: 'AccountingResolved' },
    { id: 'ref-suc-1', orderId: 'ord-4', orderCode: 'ORD004', amount: 150000, status: 'Succeeded' }
  ];
  context.rawRefundsList = resolvedItems;
  context.renderRefundsTable(resolvedItems);
  html = elements.get('refundsTableBody').innerHTML;
  assert.ok(!html.includes('openAssignModal'), 'Resolved/Succeeded must NOT have Tiếp nhận button');
  assert.ok(!html.includes('openResolveModal'), 'Resolved/Succeeded must NOT have Hoàn tất button');
  assert.ok(html.includes('Chi tiết'), 'Resolved/Succeeded must have Chi tiết button');
  assert.ok(html.includes('Xem đơn'), 'Resolved/Succeeded must have Xem đơn button');

  // 4. RetryScheduled has NO mutation action (kế toán không xử lý khi đang retry tự động)
  const retryItem = [{
    id: 'ref-ret-1',
    orderId: 'ord-5',
    orderCode: 'ORD005',
    amount: 500000,
    status: 'RetryScheduled',
    retryCount: 1,
    attemptCount: 1
  }];
  context.rawRefundsList = retryItem;
  context.renderRefundsTable(retryItem);
  html = elements.get('refundsTableBody').innerHTML;
  assert.ok(!html.includes('openAssignModal'), 'RetryScheduled must NOT have Tiếp nhận button');
  assert.ok(!html.includes('openResolveModal'), 'RetryScheduled must NOT have Hoàn tất button');
  assert.ok(html.includes('Chi tiết'), 'RetryScheduled must have Chi tiết button');
  assert.ok(html.includes('Xem đơn'), 'RetryScheduled must have Xem đơn button');
});

// =========================================================================
// 15. Customer cannot see mutation actions in auditor table
// =========================================================================
test('15. customer role does not see mutation actions in auditor table', () => {
  const { context, elements } = createDOMContext(
    { user_info: JSON.stringify({ roles: ['Customer'] }) },
    ''
  );
  vm.runInContext(extractScript(auditorHtml), context);

  const sampleList = [
    { id: 'ref-1', orderId: 'ord-1', orderCode: 'ORD1', amount: 500000, status: 'ManualReviewRequired' },
    { id: 'ref-2', orderId: 'ord-2', orderCode: 'ORD2', amount: 600000, status: 'AccountingProcessing' }
  ];

  context.rawRefundsList = sampleList;
  context.renderRefundsTable(sampleList);

  const html = elements.get('refundsTableBody').innerHTML;
  assert.ok(!html.includes('openAssignModal'), 'Customer must not see Tiếp nhận button');
  assert.ok(!html.includes('openResolveModal'), 'Customer must not see Hoàn tất button');
  assert.ok(html.includes('Chi tiết'), 'Customer can still see Chi tiết');
  assert.ok(html.includes('Xem đơn'), 'Customer can still see Xem đơn');
});

// =========================================================================
// 16. Modal confirm for assign accounting displays 6 required fields
// =========================================================================
test('16. modal confirm for assign accounting displays 6 required fields', () => {
  const { context, elements } = createDOMContext(
    { user_info: JSON.stringify({ roles: ['Auditor'] }) },
    ''
  );
  vm.runInContext(extractScript(auditorHtml), context);

  const sample = {
    id: 'ref-confirm-assign-1234',
    orderId: 'ord-guid-9999',
    orderCode: 'ORD777888',
    amount: 750000,
    status: 'ManualReviewRequired',
    errorCode: 'GATEWAY_TIMEOUT',
    errorMessage: 'Cổng thanh toán không phản hồi sau 3 lần thử',
    reason: 'Hủy vé sự kiện do bận việc'
  };

  context.rawRefundsList = [sample];
  context.openAssignModal(sample.id);

  // 1. Action to be performed
  const actionText = elements.get('assignModalAction').textContent;
  assert.ok(actionText.length > 0);
  assert.ok(actionText.includes('tiếp nhận') || actionText.includes('Tiếp nhận'));

  // 2. Refund ID
  assert.equal(elements.get('assignModalRefundId').textContent, sample.id);

  // 3. Order ID / Order Code
  assert.ok(elements.get('assignModalOrderId').textContent.includes('ORD777888'));

  // 4. Amount
  assert.ok(elements.get('assignModalAmount').textContent.includes('750.000'));

  // 5. Current status
  assert.ok(elements.get('assignModalStatus').innerHTML.includes('ManualReviewRequired') ||
            elements.get('assignModalStatus').innerHTML.includes('Chờ xử lý'));

  // 6. Gateway reason
  assert.ok(elements.get('assignModalGatewayReason').textContent.includes('Cổng thanh toán không phản hồi'));

  // Modal is marked active
  assert.ok(elements.get('modalConfirmAssign').classList.contains('active'));
});

// =========================================================================
// 17. Modal confirm for resolve accounting displays 6 required fields
// =========================================================================
test('17. modal confirm for resolve accounting displays 6 required fields', () => {
  const { context, elements } = createDOMContext(
    { user_info: JSON.stringify({ roles: ['Auditor'] }) },
    ''
  );
  vm.runInContext(extractScript(auditorHtml), context);

  const sample = {
    id: 'ref-confirm-resolve-5678',
    orderId: 'ord-guid-4321',
    orderCode: 'ORD555666',
    amount: 1200000,
    status: 'AccountingProcessing',
    errorCode: 'ACCOUNT_FROZEN',
    errorMessage: 'Tài khoản nhận tiền bị khóa bởi ngân hàng',
    reason: 'Yêu cầu hoàn trả vé'
  };

  context.rawRefundsList = [sample];
  context.openResolveModal(sample.id);

  // 1. Action to be performed
  const actionText = elements.get('modalResolveAction').textContent;
  assert.ok(actionText.length > 0);
  assert.ok(actionText.includes('hoàn tiền thủ công') || actionText.includes('Hoàn tất'));

  // 2. Refund ID
  assert.equal(elements.get('modalResolveRefundId').textContent, sample.id);

  // 3. Order ID / Order Code
  assert.ok(elements.get('modalOrderCode').textContent.includes('ORD555666'));

  // 4. Amount
  assert.ok(elements.get('modalRefundAmount').textContent.includes('1.200.000'));

  // 5. Current status
  assert.ok(elements.get('modalResolveCurrentStatus').innerHTML.includes('AccountingProcessing') ||
            elements.get('modalResolveCurrentStatus').innerHTML.includes('Đang xử lý'));

  // 6. Gateway reason
  assert.ok(elements.get('modalResolveGatewayReason').textContent.includes('Tài khoản nhận tiền bị khóa'));

  // Modal is marked active
  assert.ok(elements.get('modalResolveAccounting').classList.contains('active'));
});

// =========================================================================
// 18. API failure preserves old status without faking UI state
// =========================================================================
test('18. API failure preserves old status without faking UI state', async () => {
  const fetchCalls = [];
  const { context, elements } = createDOMContext(
    { user_info: JSON.stringify({ roles: ['Auditor'] }) },
    ''
  );

  // Mock fetch that fails for assign-accounting
  context.fetch = async (url, opts) => {
    fetchCalls.push({ url, method: opts?.method });
    if (url.includes('assign-accounting')) {
      return {
        ok: false,
        status: 400,
        json: async () => ({ message: 'Không thể tiếp nhận lệnh ở trạng thái này.' })
      };
    }
    return { ok: true, status: 200, json: async () => ({ data: [] }) };
  };

  vm.runInContext(extractScript(auditorHtml), context);

  const sample = {
    id: 'ref-error-test',
    orderId: 'ord-error-test',
    orderCode: 'ORDERR',
    amount: 100000,
    status: 'ManualReviewRequired'
  };
  context.rawRefundsList = [sample];
  context.renderRefundsTable([sample]);

  // Open modal and attempt submit
  context.openAssignModal(sample.id);
  await context.submitAssignAccounting();

  // Assert API call was made to real endpoint
  assert.equal(fetchCalls.length, 1);
  assert.ok(fetchCalls[0].url.includes('/api/v1/payments/refunds/ref-error-test/assign-accounting'));
  assert.equal(fetchCalls[0].method, 'POST');

  // Status in rawRefundsList has NOT changed falsely
  assert.equal(context.rawRefundsList[0].status, 'ManualReviewRequired');
});

// =========================================================================
// 19. Scripts parse successfully in all modified pages
// =========================================================================
test('19. all modified HTML inline scripts parse cleanly via vm.Script', () => {
  const reloadedAuditorHtml = fs.readFileSync(path.join(root, 'auditor.html'), 'utf8');
  const pages = [
    { name: 'order-details.html', html: orderDetailsHtml },
    { name: 'auditor.html', html: reloadedAuditorHtml },
    { name: 'my-orders.html', html: myOrdersHtml },
    { name: 'order-audit-logs.html', html: orderAuditLogsHtml }
  ];

  for (const page of pages) {
    for (const match of page.html.matchAll(/<script\b([^>]*)>([\s\S]*?)<\/script>/gi)) {
      if (/\bsrc=|application\/ld\+json/.test(match[1])) continue;
      assert.doesNotThrow(() => {
        new vm.Script(match[2], { filename: page.name });
      }, `Syntax error in ${page.name}`);
    }
  }
});
