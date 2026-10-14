const test = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const vm = require('node:vm');
const path = require('node:path');
const root = path.join(__dirname, '../EventTicketBooking.Api/wwwroot');
const source = fs.readFileSync(path.join(root, 'public-events.html'), 'utf8');

test('event cards link to schedule selection, have real minimum prices and no per-showtime seat claim', () => {
  const cards = [];
  const classList = { add() {}, remove() {} };
  const grid = { innerHTML: '', classList, appendChild: card => cards.push(card) };
  const context = vm.createContext({ document: {
    getElementById: id => id === 'eventsGrid' ? grid : { classList }, createElement: () => ({})
  }, escapeHtml: value => String(value), formatCurrency: value => value + ' đ', formatDateTime: () => 'Lịch sắp tới', getEventImage: () => 'poster.png' });
  vm.runInContext(source.slice(source.indexOf('    function renderEventsGrid'), source.indexOf('    function filterEvents')), context);
  context.renderEventsGrid([{ eventId: 'event-a', eventTitle: 'Concert', showtimeCount: 3, minPrice: 800000 },
    { eventId: 'event-b', eventTitle: 'Free', showtimeCount: 1, minPrice: 0 }]);
  assert.equal(cards.length, 2);
  assert.match(cards[0].innerHTML, /event-info\.html\?id=event-a/);
  assert.doesNotMatch(cards[0].innerHTML, /seat-map\.html|showtimeId=|Còn lại:/);
  assert.match(cards[0].innerHTML, /3 suất diễn đang mở bán/);
  assert.match(cards[0].innerHTML, /Chọn suất diễn/);
  assert.match(cards[1].innerHTML, /Miễn phí/);
});

test('public list uses event pagination and organizer removes demo percentages; modified scripts parse', () => {
  assert.match(source, /api\/public\/events\?limit=50/);
  assert.match(source, /while \(cursor\)/);
  for (const file of ['public-events.html', 'events.html', 'event-info.html']) {
    const html = fs.readFileSync(path.join(root, file), 'utf8');
    for (const match of html.matchAll(/<script\b[^>]*>([\s\S]*?)<\/script>/gi)) if (match[1].trim()) new vm.Script(match[1]);
    if (file === 'events.html') assert.doesNotMatch(html, /pct = 65/);
  }
});
