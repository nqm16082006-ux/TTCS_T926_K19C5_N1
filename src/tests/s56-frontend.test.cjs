const test = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const vm = require('node:vm');

const root = path.join(__dirname, '../EventTicketBooking.Api/wwwroot');
const orderDetailsHtml = fs.readFileSync(path.join(root, 'order-details.html'), 'utf8');
const eventDetailHtml = fs.readFileSync(path.join(root, 'event-detail.html'), 'utf8');

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
    setFetch: fn => { context.fetch = fn; }
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
