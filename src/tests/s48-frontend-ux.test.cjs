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
    return {
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
      removeAttribute: k => attrs.delete(k)
    };
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
// 13. Scripts parse successfully in all modified pages
// =========================================================================
test('13. all modified HTML inline scripts parse cleanly via vm.Script', () => {
  const pages = [
    { name: 'order-details.html', html: orderDetailsHtml },
    { name: 'auditor.html', html: auditorHtml },
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
