const fs = require('fs');
const vm = require('vm');
const assert = require('node:assert/strict');
const html = fs.readFileSync('src/EventTicketBooking.Api/wwwroot/seat-map.html', 'utf8');
for (const match of html.matchAll(/<script\b[^>]*>([\s\S]*?)<\/script>/gi)) new vm.Script(match[1]);
function extract(name) {
 const start = html.indexOf('    function ' + name + '(') >= 0 ? html.indexOf('    function ' + name + '(') : html.indexOf('    async function ' + name + '(');
 const next = html.indexOf('\n    function ', start + 1);
 const asyncNext = html.indexOf('\n    async function ', start + 1);
 return html.slice(start, Math.min(...[next, asyncNext].filter(x => x >= 0)));
}
let reloads = 0, modals = 0, timers = 0, expired = 0;
let payload;
const ctx = vm.createContext({console, Date, Number, performance: {now: () => 0},
 showtimeId: 'test', API_BASE: '', holdExpirationInProgress: false, holdExpiresAtMs: 0,
 myHeldSeatIds: new Set(), selectedSeatIds: new Set(), seatMap: new Map(),
 document: {getElementById: () => ({classList: {remove(){}}})},
 sessionStorage: {setItem(){}, removeItem(){}},
 stopHoldCountdown(){ctx.holdExpiresAtMs=0;}, updateCountdownTick(){}, updateCartPanel(){}, updateSeatButtonState(){},
 getValidToken: () => 'test', setInterval(){timers++; return 1;},
 loadSeats: async () => {reloads++;}, openExpiredModal: () => modals++,
 fetch: async () => ({ok:true, headers:{get:()=>null}, json:async()=>({success:true,data:payload})})});
vm.runInContext(extract('startHoldCountdown') + '\n' + extract('handleHoldExpired') + '\n' + extract('restoreMyHoldsOnLoad'), ctx);
(async()=>{
 const server='2026-10-06T10:00:00Z';
 payload={seatIds:['seat'],expiresAt:'2026-10-06T09:59:00Z',serverTime:server};
 await ctx.restoreMyHoldsOnLoad();
 assert.equal(reloads,0); assert.equal(timers,0); assert.equal(ctx.myHeldSeatIds.size,0);
 await Promise.all([ctx.handleHoldExpired(),ctx.handleHoldExpired()]);
 assert.equal(reloads,1); assert.equal(modals,1); assert.equal(ctx.holdExpirationInProgress,false);
 const original=ctx.handleHoldExpired; ctx.handleHoldExpired=()=>expired++;
 ctx.startHoldCountdown('bad',server,null); ctx.startHoldCountdown('2026-10-06T10:10:00',server,null);
 assert.equal(expired,0); assert.equal(timers,0);
 ctx.startHoldCountdown('2026-10-06T09:59:00Z',server,null,false); assert.equal(expired,0);
 ctx.startHoldCountdown('2026-10-06T09:59:00Z',server,null); assert.equal(expired,1);
 payload={seatIds:['seat'],expiresAt:'2026-10-06T10:10:00Z',serverTime:server};
 await ctx.restoreMyHoldsOnLoad(); assert.equal(ctx.myHeldSeatIds.size,1); assert.equal(timers,1);
 assert.equal(ctx.holdExpiresAtMs-ctx.holdRefServerTimeMs,600000);
 ctx.handleHoldExpired=original;
 ctx.startHoldCountdown('2026-10-06T17:10:00+07:00',server,null); assert.equal(timers,2);
 console.log('PASS: script syntax, expired restore, concurrent expiry, invalid/missing timezone, new hold, UTC countdown and explicit offset');
})().catch(e=>{console.error(e);process.exitCode=1;});
