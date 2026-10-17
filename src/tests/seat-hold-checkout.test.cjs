const test=require('node:test'),assert=require('node:assert/strict'),vm=require('node:vm'),fs=require('node:fs');
const source=fs.readFileSync('src/EventTicketBooking.Api/wwwroot/seat-map.html','utf8');
function fn(name){const start=source.search(new RegExp('(?:async )?function '+name+'\\('));const tail=source.slice(start);const next=tail.slice(1).search(/\n    (?:async )?function /);return tail.slice(0,next+1)}
test('Successful hold stays on seat map; only a second explicit action creates order',async()=>{
 let orders=0,holds=0,timers=0;const button={innerHTML:'Giữ vé',disabled:false};
 const c=vm.createContext({console,URLSearchParams,document:{getElementById:id=>id==='btnPrimaryAction'?button:null},window:{location:{search:'',href:'seat-map.html'}},sessionStorage:{getItem:()=>null},getValidToken:()=> 'token',currentBookingPolicy:()=>"seat-hold",bookingRetrySeconds:()=>0,updateBookingRateLimitUI:()=>{},updateCartPanel:()=>{},updateSeatButtonState:()=>{},restoreMyHoldsOnLoad:async()=>{timers++},showSuccessToast:()=>{},handleBookingRateLimit:()=>false,createOrderAndProceed:async()=>{orders++},fetch:async()=>{holds++;return {ok:true,headers:{get:()=>null},json:async()=>({success:true,data:{seatIds:['seat']}})}}});
 vm.runInContext(`const selectedSeatIds=new Set(['seat']);const myHeldSeatIds=new Set();const seatMap=new Map([['seat',{status:'AVAILABLE'}]]);let maxTicketsPerUser=10,purchasedCountForUser=0;const showtimeId='show',API_BASE='';${fn('performPrimaryAction')}`,c);
 await c.performPrimaryAction();assert.equal(holds,1);assert.equal(orders,0);assert.equal(timers,1);assert.equal(c.window.location.href,'seat-map.html');assert.equal(button.innerHTML,'Giữ vé');
 await c.performPrimaryAction();assert.equal(orders,1);assert.equal(holds,1);
});
test('Countdown does not expire a hold during its final fractional second',()=>{
 let elapsed=0,expired=0;const clock={classList:{add(){},remove(){}},textContent:''};const c=vm.createContext({Math,String,performance:{now:()=>elapsed},document:{getElementById:id=>id==='countdown-clock'?clock:null},stopHoldCountdown:()=>{},handleHoldExpired:()=>expired++});
 vm.runInContext(`let holdExpiresAtMs=10000,holdRefServerTimeMs=9000,holdRefLocalPerfMs=0;const DEFAULT_HOLD_DURATION_SECONDS=600;${fn('updateCountdownTick')}`,c);
 elapsed=500;c.updateCountdownTick();assert.equal(expired,0);assert.equal(clock.textContent,'00:01');elapsed=1000;c.updateCountdownTick();assert.equal(expired,1);
});

