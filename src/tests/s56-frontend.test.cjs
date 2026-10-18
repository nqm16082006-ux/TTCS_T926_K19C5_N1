const test = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const vm = require('node:vm');

const root = path.join(__dirname, '../EventTicketBooking.Api/wwwroot');
const orderDetailsHtml = fs.readFileSync(path.join(root, 'order-details.html'), 'utf8');
const eventDetailHtml = fs.readFileSync(path.join(root, 'event-detail.html'), 'utf8');
const seatMapHtml = fs.readFileSync(path.join(root, 'seat-map.html'), 'utf8');

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
  function createElement(tag = 'div') {
    const classes = new Set();
    const attrs = new Map();
    let value = '';
    const node = {
      tagName: tag.toUpperCase(),
      textContent: '',
      innerHTML: '',
      title: '',
      href: '',
      disabled: false,
      get value() { return value; },
      set value(v) { value = String(v); },
      style: {},
      classList: {
        add: (...vals) => vals.forEach(v => classes.add(v)),
        remove: (...vals) => vals.forEach(v => classes.delete(v)),
        replace: (oldVal, newVal) => { classes.delete(oldVal); classes.add(newVal); },
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
      focus: () => {},
      querySelector: () => createElement('span'),
      querySelectorAll: () => []
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

  const fetchCalls = [];
  const fetchMock = async (url, options = {}) => {
    const call = { url: String(url), options };
    fetchCalls.push(call);
    return {
      ok: true,
      json: async () => ({
        success: true,
        data: {
          id: 'test-order-id',
          status: 'Pending',
          originalAmount: 500000,
          discountAmount: 50000,
          totalAmount: 450000,
          discountCode: 'TEST50'
        }
      })
    };
  };

  const documentObj = {
    body: createElement('body'),
    createElement: tag => createElement(tag),
    getElementById: id => {
      if (!elements.has(id)) elements.set(id, createElement());
      return elements.get(id);
    },
    querySelectorAll: () => [],
    querySelector: () => createElement('span'),
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
    parseInt,
    parseFloat,
    setInterval: () => 1,
    clearInterval: () => {},
    setTimeout: (fn) => { if (typeof fn === 'function') fn(); return 1; },
    clearTimeout: () => {},
    URLSearchParams,
    tailwind: { config: {} },
    window: windowObj,
    location: windowObj.location,
    history: windowObj.history,
    document: documentObj,
    localStorage: storage,
    sessionStorage: storage,
    navigator: navigatorObj,
    alert: () => {},
    confirm: () => true,
    fetch: fetchMock
  });

  return {
    context,
    elements,
    document: documentObj,
    storage,
    windowObj,
    getFetchCalls: () => fetchCalls,
    setFetch: fn => {
      context.fetch = async (url, options = {}) => {
        fetchCalls.push({ url: String(url), options });
        return fn(url, options);
      };
    }
  };
}

// =========================================================================
// 1. Order-details HTML contains customer discount elements
// =========================================================================
test('1. order-details.html contains customer discount input, apply button and breakdown rows', () => {
  assert.ok(orderDetailsHtml.includes('id="discountCodeInput"'), 'missing discountCodeInput');
  assert.ok(orderDetailsHtml.includes('id="btnApplyDiscount"'), 'missing btnApplyDiscount');
  assert.ok(orderDetailsHtml.includes('id="discountError"'), 'missing discountError');
  assert.ok(orderDetailsHtml.includes('id="discountAppliedSection"'), 'missing discountAppliedSection');
  assert.ok(orderDetailsHtml.includes('id="appliedCodeText"'), 'missing appliedCodeText');
  assert.ok(orderDetailsHtml.includes('id="appliedDiscountBadge"'), 'missing appliedDiscountBadge');
  assert.ok(orderDetailsHtml.includes('id="originalAmountDisplay"'), 'missing originalAmountDisplay');
  assert.ok(orderDetailsHtml.includes('id="discountRow"'), 'missing discountRow');
  assert.ok(orderDetailsHtml.includes('id="discountAmountDisplay"'), 'missing discountAmountDisplay');
  assert.ok(orderDetailsHtml.includes('id="totalAmountDisplay"'), 'missing totalAmountDisplay');
});

// =========================================================================
// 2. Customer discount script uses access_token convention (NOT token)
// =========================================================================
test('2. order-details discount handler uses access_token / accessToken auth convention', async () => {
  const tokenValue = 'jwt_customer_s56_secret_token';
  const { context, document, getFetchCalls } = createDOMContext(
    { access_token: tokenValue },
    '?id=3fa85f64-5717-4562-b3fc-2c963f66afa6'
  );

  const script = extractScript(orderDetailsHtml);
  vm.runInContext(script, context);

  // Set code in input element
  const inputEl = document.getElementById('discountCodeInput');
  inputEl.value = 'GIAM10';
  context.currentOrderId = '3fa85f64-5717-4562-b3fc-2c963f66afa6';

  await context.applyDiscountCode();

  const applyCall = getFetchCalls().find(c => c.url.includes('/apply-discount'));
  assert.ok(applyCall, 'apply-discount fetch should have been called');
  assert.ok(applyCall.url.includes('/api/v1/orders/3fa85f64-5717-4562-b3fc-2c963f66afa6/apply-discount'));
  assert.equal(applyCall.options.method, 'POST');
  assert.equal(applyCall.options.headers['Authorization'], `Bearer ${tokenValue}`);
  assert.deepEqual(JSON.parse(applyCall.options.body), { code: 'GIAM10' });
});

// =========================================================================
// 3. Customer discount script supports accessToken alternate storage key
// =========================================================================
test('3. order-details discount handler supports accessToken in localStorage', async () => {
  const tokenValue = 'alt_accessToken_jwt';
  const { context, document, getFetchCalls } = createDOMContext(
    { accessToken: tokenValue },
    '?id=3fa85f64-5717-4562-b3fc-2c963f66afa6'
  );

  const script = extractScript(orderDetailsHtml);
  vm.runInContext(script, context);

  const inputEl = document.getElementById('discountCodeInput');
  inputEl.value = 'SALE20';
  context.currentOrderId = '3fa85f64-5717-4562-b3fc-2c963f66afa6';

  await context.applyDiscountCode();

  const applyCall = getFetchCalls().find(c => c.url.includes('/apply-discount'));
  assert.ok(applyCall, 'apply-discount fetch should have been called');
  assert.equal(applyCall.options.headers['Authorization'], `Bearer ${tokenValue}`);
});

// =========================================================================
// 4. Render order pricing breakdown: original, discount, and final total
// =========================================================================
test('4. renderOrder renders original total, discount amount, applied badge and final total', () => {
  const { context, document } = createDOMContext({}, '?id=3fa85f64-5717-4562-b3fc-2c963f66afa6');
  const script = extractScript(orderDetailsHtml);
  vm.runInContext(script, context);

  const mockOrder = {
    id: '3fa85f64-5717-4562-b3fc-2c963f66afa6',
    status: 'Pending',
    createdAt: new Date().toISOString(),
    expiresAt: new Date(Date.now() + 600000).toISOString(),
    originalAmount: 1000000,
    discountAmount: 150000,
    totalAmount: 850000,
    discountCode: 'VIP15',
    items: [
      { id: 'item-1', seatName: 'A1', seatCategory: 'VIP', price: 1000000 }
    ]
  };

  context.renderOrder(mockOrder);

  // Check original price text
  const originalText = document.getElementById('originalAmountDisplay').textContent;
  assert.ok(originalText.includes('1.000.000'), 'original total displayed');

  // Check discount row
  const discountRow = document.getElementById('discountRow');
  assert.equal(discountRow.classList.contains('hidden'), false, 'discount row visible');
  const discountText = document.getElementById('discountAmountDisplay').textContent;
  assert.ok(discountText.includes('150.000'), 'discount amount displayed');

  // Check applied section badge
  const appliedSection = document.getElementById('discountAppliedSection');
  assert.equal(appliedSection.classList.contains('hidden'), false, 'applied section visible');
  assert.equal(document.getElementById('appliedCodeText').textContent, 'VIP15');
  assert.ok(document.getElementById('appliedDiscountBadge').textContent.includes('150.000'));

  // Check final total
  const totalText = document.getElementById('totalAmountDisplay').textContent;
  assert.ok(totalText.includes('850.000'), 'final total displayed');

  // Discount input section should be hidden when code is applied
  const inputSection = document.getElementById('discountInputSection');
  assert.equal(inputSection.classList.contains('hidden'), true, 'input section hidden after applied');
});

// =========================================================================
// 5. Render order with NO discount: input section is visible for active Pending
// =========================================================================
test('5. renderOrder with no discount displays input section for pending order', () => {
  const { context, document } = createDOMContext({}, '?id=3fa85f64-5717-4562-b3fc-2c963f66afa6');
  const script = extractScript(orderDetailsHtml);
  vm.runInContext(script, context);

  const mockOrder = {
    id: '3fa85f64-5717-4562-b3fc-2c963f66afa6',
    status: 'Pending',
    createdAt: new Date().toISOString(),
    expiresAt: new Date(Date.now() + 600000).toISOString(),
    originalAmount: 500000,
    discountAmount: 0,
    totalAmount: 500000,
    discountCode: null,
    items: [
      { id: 'item-1', seatName: 'B1', seatCategory: 'Standard', price: 500000 }
    ]
  };

  context.renderOrder(mockOrder);

  // Applied section and discount row hidden
  assert.equal(document.getElementById('discountAppliedSection').classList.contains('hidden'), true);
  assert.equal(document.getElementById('discountRow').classList.contains('hidden'), true);

  // Input section visible for pending active order
  assert.equal(document.getElementById('discountInputSection').classList.contains('hidden'), false);
  assert.ok(document.getElementById('totalAmountDisplay').textContent.includes('500.000'));
});

// =========================================================================
// 6. Organizer event-detail.html contains discount form & table
// =========================================================================
test('6. event-detail.html contains organizer create discount form and table', () => {
  assert.ok(eventDetailHtml.includes('id="tabBtnDiscounts"'), 'missing tabBtnDiscounts');
  assert.ok(eventDetailHtml.includes('id="formCreateDiscount"'), 'missing formCreateDiscount');
  assert.ok(eventDetailHtml.includes('id="orgDiscountCodeInput"'), 'missing orgDiscountCodeInput');
  assert.ok(eventDetailHtml.includes('id="orgDiscountTypeSelect"'), 'missing orgDiscountTypeSelect');
  assert.ok(eventDetailHtml.includes('id="orgDiscountValueInput"'), 'missing orgDiscountValueInput');
  assert.ok(eventDetailHtml.includes('id="orgDiscountUsageLimitInput"'), 'missing orgDiscountUsageLimitInput');
  assert.ok(eventDetailHtml.includes('id="orgDiscountExpiresAtInput"'), 'missing orgDiscountExpiresAtInput');
  assert.ok(eventDetailHtml.includes('id="btnSubmitCreateDiscount"'), 'missing btnSubmitCreateDiscount');
  assert.ok(eventDetailHtml.includes('id="discountCodesTableBody"'), 'missing discountCodesTableBody');
});

// =========================================================================
// 7. Organizer loadDiscountCodes renders table rows
// =========================================================================
test('7. event-detail script loadDiscountCodes renders discount code items', async () => {
  const { context, document, setFetch } = createDOMContext(
    { access_token: 'org_token_123' },
    '?id=e1234567-0000-0000-0000-000000000000'
  );

  const mockCodes = [
    {
      id: 'd1',
      code: 'SUMMER2026',
      discountType: 'Percent',
      discountValue: 10,
      usedCount: 3,
      usageLimit: 50,
      expiresAt: new Date(Date.now() + 86400000).toISOString(),
      isActive: true
    },
    {
      id: 'd2',
      code: 'FIXED50K',
      discountType: 'FixedAmount',
      discountValue: 50000,
      usedCount: 10,
      usageLimit: 10,
      expiresAt: new Date(Date.now() - 86400000).toISOString(),
      isActive: false
    }
  ];

  setFetch(async (url) => {
    if (url.includes('/discount-codes')) {
      return {
        ok: true,
        json: async () => ({
          success: true,
          data: mockCodes
        })
      };
    }
    return { ok: true, json: async () => ({}) };
  });

  const script = extractScript(eventDetailHtml);
  vm.runInContext(script, context);

  await context.loadDiscountCodes('e1234567-0000-0000-0000-000000000000');

  const tableBody = document.getElementById('discountCodesTableBody');
  const renderedHtml = tableBody.innerHTML;

  assert.ok(renderedHtml.includes('SUMMER2026'), 'SUMMER2026 code rendered');
  assert.ok(renderedHtml.includes('10%'), 'Percent value rendered');
  assert.ok(renderedHtml.includes('3 / 50'), 'Used count rendered');
  assert.ok(renderedHtml.includes('FIXED50K'), 'FIXED50K code rendered');
  assert.ok(renderedHtml.includes('50.000'), 'Fixed amount formatted');
  assert.ok(renderedHtml.includes('10 / 10'), 'Full usage rendered');
});

// =========================================================================
// 8. Organizer CreateDiscount maps Percent -> 0 and does NOT send string
// =========================================================================
test('8. handleCreateDiscountSubmit maps Percent to numeric 0 and does not send string enum', async () => {
  const { context, document, setFetch } = createDOMContext(
    { access_token: 'org_token_123' },
    '?id=e1234567-0000-0000-0000-000000000000'
  );

  let capturedBody = null;
  setFetch(async (url, options) => {
    if (url.includes('/discount-codes')) {
      capturedBody = JSON.parse(options.body);
      return {
        ok: true,
        json: async () => ({
          success: true,
          message: 'Tạo mã thành công',
          data: { code: capturedBody.code }
        })
      };
    }
    return { ok: true, json: async () => ({}) };
  });

  const script = extractScript(eventDetailHtml);
  vm.runInContext(script, context);

  document.getElementById('orgDiscountCodeInput').value = 'PERCENT10';
  document.getElementById('orgDiscountTypeSelect').value = 'Percent';
  document.getElementById('orgDiscountValueInput').value = '10';
  document.getElementById('orgDiscountUsageLimitInput').value = '100';
  document.getElementById('orgDiscountExpiresAtInput').value = '2026-12-31T23:59';
  document.getElementById('formCreateDiscount').reset = () => {};

  await context.handleCreateDiscountSubmit({ preventDefault: () => {} });

  assert.ok(capturedBody, 'API was called');
  assert.strictEqual(capturedBody.discountType, 0, 'discountType must be numeric 0 for Percent');
  assert.notStrictEqual(capturedBody.discountType, 'Percent', 'discountType must not be string');
  assert.strictEqual(capturedBody.code, 'PERCENT10');
  assert.strictEqual(capturedBody.discountValue, 10);
});

// =========================================================================
// 9. Organizer CreateDiscount maps FixedAmount -> 1 and does NOT send string
// =========================================================================
test('9. handleCreateDiscountSubmit maps FixedAmount to numeric 1 and does not send string enum', async () => {
  const { context, document, setFetch } = createDOMContext(
    { access_token: 'org_token_123' },
    '?id=e1234567-0000-0000-0000-000000000000'
  );

  let capturedBody = null;
  setFetch(async (url, options) => {
    if (url.includes('/discount-codes')) {
      capturedBody = JSON.parse(options.body);
      return {
        ok: true,
        json: async () => ({
          success: true,
          message: 'Tạo mã thành công',
          data: { code: capturedBody.code }
        })
      };
    }
    return { ok: true, json: async () => ({}) };
  });

  const script = extractScript(eventDetailHtml);
  vm.runInContext(script, context);

  document.getElementById('orgDiscountCodeInput').value = 'FIXED50';
  document.getElementById('orgDiscountTypeSelect').value = 'FixedAmount';
  document.getElementById('orgDiscountValueInput').value = '50000';
  document.getElementById('orgDiscountUsageLimitInput').value = '20';
  document.getElementById('orgDiscountExpiresAtInput').value = '2026-12-31T23:59';
  document.getElementById('formCreateDiscount').reset = () => {};

  await context.handleCreateDiscountSubmit({ preventDefault: () => {} });

  assert.ok(capturedBody, 'API was called');
  assert.strictEqual(capturedBody.discountType, 1, 'discountType must be numeric 1 for FixedAmount');
  assert.notStrictEqual(capturedBody.discountType, 'FixedAmount', 'discountType must not be string');
  assert.strictEqual(capturedBody.code, 'FIXED50');
  assert.strictEqual(capturedBody.discountValue, 50000);
});

// =========================================================================
// 10. Organizer CreateDiscount displays safe backend error message
// =========================================================================
test('10. handleCreateDiscountSubmit safely displays backend error message and validation errors', async () => {
  const { context, document, setFetch } = createDOMContext(
    { access_token: 'org_token_123' },
    '?id=e1234567-0000-0000-0000-000000000000'
  );

  // Subtest 10a: API error message
  setFetch(async () => ({
    ok: false,
    json: async () => ({
      success: false,
      message: 'Mã giảm giá đã tồn tại trên sự kiện này.'
    })
  }));

  const script = extractScript(eventDetailHtml);
  vm.runInContext(script, context);

  document.getElementById('orgDiscountCodeInput').value = 'DUPLICATE';
  document.getElementById('orgDiscountTypeSelect').value = 'Percent';
  document.getElementById('orgDiscountValueInput').value = '5';
  document.getElementById('orgDiscountUsageLimitInput').value = '10';
  document.getElementById('orgDiscountExpiresAtInput').value = '2026-12-31T23:59';

  await context.handleCreateDiscountSubmit({ preventDefault: () => {} });

  const alertDiv = document.getElementById('orgCreateDiscountAlert');
  assert.strictEqual(alertDiv.textContent, 'Mã giảm giá đã tồn tại trên sự kiện này.');

  // Subtest 10b: Validation error object
  setFetch(async () => ({
    ok: false,
    json: async () => ({
      title: 'One or more validation errors occurred.',
      errors: {
        Code: ['Mã giảm giá tối đa 50 ký tự']
      }
    })
  }));

  await context.handleCreateDiscountSubmit({ preventDefault: () => {} });
  assert.strictEqual(alertDiv.textContent, 'Mã giảm giá tối đa 50 ký tự');
});

// =========================================================================
// 11. Discount input visibility is role-neutral and based on own payable order
// =========================================================================
test('11. discount input visibility is role-neutral and based on own payable order', async () => {
  const script = extractScript(orderDetailsHtml);

  // Case A: Customer user + own Pending order -> input VISIBLE
  {
    const { context, document } = createDOMContext(
      {
        access_token: 'cust_token',
        user_info: JSON.stringify({ id: 'cust-1', email: 'cust1@example.com', roles: ['Customer'] })
      },
      '?id=order-1'
    );
    vm.runInContext(script, context);

    const pendingOrder = {
      id: 'order-1',
      userId: 'cust-1',
      customerEmail: 'cust1@example.com',
      status: 'Pending',
      expiresAt: new Date(Date.now() + 600000).toISOString(),
      originalAmount: 100000,
      discountAmount: 0,
      totalAmount: 100000,
      discountCode: null
    };

    context.renderOrder(pendingOrder);
    assert.strictEqual(
      document.getElementById('discountInputSection').classList.contains('hidden'),
      false,
      'Customer with own active pending order should see discount input'
    );
  }

  // Case B: Organizer user + own Pending order -> input VISIBLE
  {
    const { context, document } = createDOMContext(
      {
        access_token: 'org_token',
        user_info: JSON.stringify({ id: 'org-1', email: 'org1@example.com', roles: ['Organizer'] })
      },
      '?id=order-2'
    );
    vm.runInContext(script, context);

    const pendingOrder = {
      id: 'order-2',
      userId: 'org-1',
      customerEmail: 'org1@example.com',
      status: 'Pending',
      expiresAt: new Date(Date.now() + 600000).toISOString(),
      originalAmount: 100000,
      discountAmount: 0,
      totalAmount: 100000,
      discountCode: null
    };

    context.renderOrder(pendingOrder);
    assert.strictEqual(
      document.getElementById('discountInputSection').classList.contains('hidden'),
      false,
      'Organizer buying for themselves with active pending order should see discount input'
    );
  }

  // Case C: Admin user + own Pending order -> input VISIBLE
  {
    const { context, document } = createDOMContext(
      {
        access_token: 'admin_token',
        user_info: JSON.stringify({ id: 'admin-1', email: 'admin1@example.com', roles: ['Admin'] })
      },
      '?id=order-3'
    );
    vm.runInContext(script, context);

    const pendingOrder = {
      id: 'order-3',
      userId: 'admin-1',
      customerEmail: 'admin1@example.com',
      status: 'Pending',
      expiresAt: new Date(Date.now() + 600000).toISOString(),
      originalAmount: 100000,
      discountAmount: 0,
      totalAmount: 100000,
      discountCode: null
    };

    context.renderOrder(pendingOrder);
    assert.strictEqual(
      document.getElementById('discountInputSection').classList.contains('hidden'),
      false,
      'Admin buying for themselves with active pending order should see discount input'
    );
  }

  // Case D: Admin user viewing someone else\'s Pending order -> input HIDDEN & apply denied
  {
    const { context, document } = createDOMContext(
      {
        access_token: 'admin_token',
        user_info: JSON.stringify({ id: 'admin-1', email: 'admin1@example.com', roles: ['Admin'] })
      },
      '?id=order-4'
    );
    vm.runInContext(script, context);
    await new Promise(r => setTimeout(r, 10));

    const otherOrder = {
      id: 'order-4',
      userId: 'other-cust',
      customerEmail: 'other@example.com',
      status: 'Pending',
      expiresAt: new Date(Date.now() + 600000).toISOString(),
      originalAmount: 100000,
      discountAmount: 0,
      totalAmount: 100000,
      discountCode: null
    };

    context.renderOrder(otherOrder);
    assert.strictEqual(
      document.getElementById('discountInputSection').classList.contains('hidden'),
      true,
      'Admin inspecting someone else order must NOT see discount input'
    );

    // Attempting applyDiscountCode on non-owner order must be rejected
    document.getElementById('discountCodeInput').value = 'TRYDISCOUNT';
    await context.applyDiscountCode();

    const errDiv = document.getElementById('discountError');
    const errMsg = document.getElementById('discountErrorMsg');
    assert.strictEqual(errDiv.classList.contains('hidden'), false);
    assert.strictEqual(errMsg.textContent, 'Bạn không có quyền thao tác đơn này.');
  }

  // Case E: Customer user viewing another user\'s order -> input HIDDEN & apply denied
  {
    const { context, document } = createDOMContext(
      {
        access_token: 'cust_token',
        user_info: JSON.stringify({ id: 'cust-1', email: 'cust1@example.com', roles: ['Customer'] })
      },
      '?id=order-5'
    );
    vm.runInContext(script, context);
    await new Promise(r => setTimeout(r, 10));

    const otherOrder = {
      id: 'order-5',
      userId: 'cust-2',
      customerEmail: 'cust2@example.com',
      status: 'Pending',
      expiresAt: new Date(Date.now() + 600000).toISOString(),
      originalAmount: 100000,
      discountAmount: 0,
      totalAmount: 100000,
      discountCode: null
    };

    context.renderOrder(otherOrder);
    assert.strictEqual(
      document.getElementById('discountInputSection').classList.contains('hidden'),
      true,
      'Customer inspecting someone else order must NOT see discount input'
    );

    document.getElementById('discountCodeInput').value = 'TRYDISCOUNT';
    await context.applyDiscountCode();

    const errDiv = document.getElementById('discountError');
    const errMsg = document.getElementById('discountErrorMsg');
    assert.strictEqual(errDiv.classList.contains('hidden'), false);
    assert.strictEqual(errMsg.textContent, 'Bạn không có quyền thao tác đơn này.');
  }

  // Case F: Paid order -> input HIDDEN
  {
    const { context, document } = createDOMContext(
      {
        access_token: 'cust_token',
        user_info: JSON.stringify({ id: 'cust-1', email: 'cust1@example.com', roles: ['Customer'] })
      },
      '?id=order-6'
    );
    vm.runInContext(script, context);

    const paidOrder = {
      id: 'order-6',
      userId: 'cust-1',
      customerEmail: 'cust1@example.com',
      status: 'Paid',
      expiresAt: new Date(Date.now() + 600000).toISOString(),
      originalAmount: 100000,
      discountAmount: 0,
      totalAmount: 100000,
      discountCode: null
    };

    context.renderOrder(paidOrder);
    assert.strictEqual(
      document.getElementById('discountInputSection').classList.contains('hidden'),
      true,
      'Paid order must hide discount input'
    );
  }

  // Case G: Expired order -> input HIDDEN
  {
    const { context, document } = createDOMContext(
      {
        access_token: 'cust_token',
        user_info: JSON.stringify({ id: 'cust-1', email: 'cust1@example.com', roles: ['Customer'] })
      },
      '?id=order-7'
    );
    vm.runInContext(script, context);

    const expiredOrder = {
      id: 'order-7',
      userId: 'cust-1',
      customerEmail: 'cust1@example.com',
      status: 'Pending',
      expiresAt: new Date(Date.now() - 600000).toISOString(),
      originalAmount: 100000,
      discountAmount: 0,
      totalAmount: 100000,
      discountCode: null
    };

    context.renderOrder(expiredOrder);
    assert.strictEqual(
      document.getElementById('discountInputSection').classList.contains('hidden'),
      true,
      'Expired order must hide discount input'
    );
  }

  // Case H: Already discounted order -> show badge, hide input
  {
    const { context, document } = createDOMContext(
      {
        access_token: 'cust_token',
        user_info: JSON.stringify({ id: 'cust-1', email: 'cust1@example.com', roles: ['Customer'] })
      },
      '?id=order-8'
    );
    vm.runInContext(script, context);

    const discountedOrder = {
      id: 'order-8',
      userId: 'cust-1',
      customerEmail: 'cust1@example.com',
      status: 'Pending',
      expiresAt: new Date(Date.now() + 600000).toISOString(),
      originalAmount: 100000,
      discountAmount: 20000,
      totalAmount: 80000,
      discountCode: 'PROMO20'
    };

    context.renderOrder(discountedOrder);
    assert.strictEqual(
      document.getElementById('discountInputSection').classList.contains('hidden'),
      true,
      'Already discounted order must hide discount input'
    );
    assert.strictEqual(
      document.getElementById('discountAppliedSection').classList.contains('hidden'),
      false,
      'Already discounted order must show applied badge section'
    );
    assert.strictEqual(
      document.getElementById('appliedCodeText').textContent,
      'PROMO20'
    );
  }
});

// =========================================================================
// 12. Seat-map redirects to order-details.html checkout screen
// =========================================================================
test('12. seat-map redirects to order-details.html checkout screen upon creating pending order', () => {
  assert.ok(
    seatMapHtml.includes("window.location.href = `./order-details.html?id=${encodeURIComponent(order.id)}`"),
    'seat-map should redirect to order-details.html?id= after creating order'
  );
});

// =========================================================================
// 13. Discount section appears before payment action in order-details.html
// =========================================================================
test('13. discount section appears before payment action in order-details.html', () => {
  const discountInputIdx = orderDetailsHtml.indexOf('id="discountInputSection"');
  const discountAppliedIdx = orderDetailsHtml.indexOf('id="discountAppliedSection"');
  const repayBtnIdx = orderDetailsHtml.indexOf('id="btnRepay"');

  assert.ok(discountInputIdx > 0, 'discountInputSection must exist');
  assert.ok(discountAppliedIdx > 0, 'discountAppliedSection must exist');
  assert.ok(repayBtnIdx > 0, 'btnRepay must exist');

  assert.ok(
    discountInputIdx < repayBtnIdx,
    'discountInputSection must appear before btnRepay'
  );
  assert.ok(
    discountAppliedIdx < repayBtnIdx,
    'discountAppliedSection must appear before btnRepay'
  );
});

// =========================================================================
// 14. 400/401/403/404/network errors rendered separately without stack traces
// =========================================================================
test('14. applyDiscountCode renders 400, 401, 403, 404, and network errors separately', async () => {
  const script = extractScript(orderDetailsHtml);

  // Subtest 14a: 401 Unauthorized
  {
    const { context, document, setFetch } = createDOMContext(
      { access_token: 'valid_token' },
      '?id=order-401'
    );
    vm.runInContext(script, context);
    await new Promise(r => setTimeout(r, 10));

    setFetch(async () => ({
      status: 401,
      ok: false,
      json: async () => ({ success: false, message: 'Unauthorized' })
    }));

    document.getElementById('discountCodeInput').value = 'CODE401';
    await context.applyDiscountCode();

    const errDiv = document.getElementById('discountError');
    const errMsg = document.getElementById('discountErrorMsg');
    assert.strictEqual(errDiv.classList.contains('hidden'), false);
    assert.strictEqual(errMsg.textContent, 'Phiên đăng nhập đã hết hạn.');
  }

  // Subtest 14b: 403 Forbidden
  {
    const { context, document, setFetch } = createDOMContext(
      { access_token: 'valid_token' },
      '?id=order-403'
    );
    vm.runInContext(script, context);
    await new Promise(r => setTimeout(r, 10));

    setFetch(async () => ({
      status: 403,
      ok: false,
      json: async () => ({ success: false, message: 'Forbidden' })
    }));

    document.getElementById('discountCodeInput').value = 'CODE403';
    await context.applyDiscountCode();

    const errDiv = document.getElementById('discountError');
    const errMsg = document.getElementById('discountErrorMsg');
    assert.strictEqual(errDiv.classList.contains('hidden'), false);
    assert.strictEqual(errMsg.textContent, 'Bạn không có quyền thao tác đơn này.');
  }

  // Subtest 14c: 404 Not Found (Invalid or wrong event)
  {
    const { context, document, setFetch } = createDOMContext(
      { access_token: 'valid_token' },
      '?id=order-404'
    );
    vm.runInContext(script, context);
    await new Promise(r => setTimeout(r, 10));

    setFetch(async () => ({
      status: 404,
      ok: false,
      json: async () => ({ success: false, message: 'Not Found' })
    }));

    document.getElementById('discountCodeInput').value = 'NOTFOUND';
    await context.applyDiscountCode();

    const errDiv = document.getElementById('discountError');
    const errMsg = document.getElementById('discountErrorMsg');
    assert.strictEqual(errDiv.classList.contains('hidden'), false);
    assert.strictEqual(
      errMsg.textContent,
      'Mã giảm giá không hợp lệ hoặc không áp dụng cho sự kiện này.'
    );
  }

  // Subtest 14d: 400 Bad Request with safe backend business message
  {
    const { context, document, setFetch } = createDOMContext(
      { access_token: 'valid_token' },
      '?id=order-400'
    );
    vm.runInContext(script, context);
    await new Promise(r => setTimeout(r, 10));

    setFetch(async () => ({
      status: 400,
      ok: false,
      json: async () => ({
        success: false,
        message: 'Mã giảm giá đã hết lượt sử dụng hoặc đã hết hạn.'
      })
    }));

    document.getElementById('discountCodeInput').value = 'EXPIRED';
    await context.applyDiscountCode();

    const errDiv = document.getElementById('discountError');
    const errMsg = document.getElementById('discountErrorMsg');
    assert.strictEqual(errDiv.classList.contains('hidden'), false);
    assert.strictEqual(
      errMsg.textContent,
      'Mã giảm giá đã hết lượt sử dụng hoặc đã hết hạn.'
    );
  }

  // Subtest 14e: Network error / connection failure
  {
    const { context, document, setFetch } = createDOMContext(
      { access_token: 'valid_token' },
      '?id=order-net'
    );
    vm.runInContext(script, context);
    await new Promise(r => setTimeout(r, 10));

    setFetch(async (url) => {
      if (String(url).includes('apply-discount')) {
        throw new TypeError('Failed to fetch');
      }
      return { ok: true, json: async () => ({ success: true, data: [] }) };
    });

    document.getElementById('discountCodeInput').value = 'NETERR';
    await context.applyDiscountCode();

    const errDiv = document.getElementById('discountError');
    const errMsg = document.getElementById('discountErrorMsg');
    assert.strictEqual(errDiv.classList.contains('hidden'), false);
    assert.strictEqual(
      errMsg.textContent,
      'Không thể kết nối máy chủ. Vui lòng thử lại.'
    );
  }

  // Subtest 14f: 500 Server error
  {
    const { context, document, setFetch } = createDOMContext(
      { access_token: 'valid_token' },
      '?id=order-500'
    );
    vm.runInContext(script, context);
    await new Promise(r => setTimeout(r, 10));

    setFetch(async () => ({
      status: 500,
      ok: false,
      json: async () => ({ success: false, message: 'Internal Server Error' })
    }));

    document.getElementById('discountCodeInput').value = 'CODE500';
    await context.applyDiscountCode();

    const errDiv = document.getElementById('discountError');
    const errMsg = document.getElementById('discountErrorMsg');
    assert.strictEqual(errDiv.classList.contains('hidden'), false);
    assert.strictEqual(
      errMsg.textContent,
      'Không thể kết nối máy chủ. Vui lòng thử lại.'
    );
  }
});

// =========================================================================
// 15. Applied code updates price summary and badge
// =========================================================================
test('15. applied code updates price summary, badge, and hides discount input', async () => {
  const script = extractScript(orderDetailsHtml);
  const { context, document, setFetch } = createDOMContext(
    { access_token: 'valid_token' },
    '?id=order-applied'
  );
  vm.runInContext(script, context);

  const updatedOrder = {
    id: 'order-applied',
    status: 'Pending',
    expiresAt: new Date(Date.now() + 600000).toISOString(),
    originalAmount: 1000000,
    discountAmount: 200000,
    totalAmount: 800000,
    discountCode: 'SALE20'
  };

  setFetch(async () => ({
    status: 200,
    ok: true,
    json: async () => ({
      success: true,
      message: 'Áp dụng mã giảm giá thành công.',
      data: updatedOrder
    })
  }));

  document.getElementById('discountCodeInput').value = 'SALE20';
  await context.applyDiscountCode();

  // Input section should be hidden
  assert.strictEqual(
    document.getElementById('discountInputSection').classList.contains('hidden'),
    true
  );
  // Applied section should be visible
  assert.strictEqual(
    document.getElementById('discountAppliedSection').classList.contains('hidden'),
    false
  );
  assert.strictEqual(
    document.getElementById('appliedCodeText').textContent,
    'SALE20'
  );
  // Price rows
  assert.strictEqual(
    document.getElementById('discountRow').classList.contains('hidden'),
    false
  );
  assert.ok(
    document.getElementById('discountAmountDisplay').textContent.includes('200.000')
  );
  assert.ok(
    document.getElementById('totalAmountDisplay').textContent.includes('800.000')
  );
});

// =========================================================================
// 16. Payment action uses server-authoritative TotalAmount flow
// =========================================================================
test('16. checkout payment action requests payment without sending client-tampered amount', async () => {
  const script = extractScript(orderDetailsHtml);
  const { context, document, getFetchCalls, setFetch } = createDOMContext(
    { access_token: 'valid_token' },
    '?id=order-pay'
  );
  vm.runInContext(script, context);

  setFetch(async (url, options) => {
    return {
      status: 200,
      ok: true,
      json: async () => ({
        success: true,
        data: {
          paymentUrl: 'https://payment-gateway.example/pay/123'
        }
      })
    };
  });

  const btnElement = document.getElementById('btnRepay');
  await context.handleRepayment('order-pay', btnElement);

  const paymentCall = getFetchCalls().find(c => c.url.includes('/payments/orders/order-pay'));
  assert.ok(paymentCall, 'handleRepayment must call payment endpoint for the order');
  assert.strictEqual(paymentCall.options.method, 'POST');
  assert.strictEqual(paymentCall.options.headers['Authorization'], 'Bearer valid_token');
  // Confirm NO client-side amount was passed in request body
  const body = paymentCall.options.body ? JSON.parse(paymentCall.options.body) : null;
  assert.strictEqual(body, null, 'Client must NOT send custom amount; server is authoritative');
});

