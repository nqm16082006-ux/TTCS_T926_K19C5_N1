const test=require('node:test'),assert=require('node:assert/strict'),fs=require('node:fs'),vm=require('node:vm');
const root='src/EventTicketBooking.Api/wwwroot/';
test('Scanner loads the readmission helper used by online rejection handling',()=>{
 const html=fs.readFileSync(root+'staff-scanner.html','utf8');
 assert.match(html,/<script\s+src="\.\/scanner-readmission\.js"><\/script>/);
 const context={};vm.createContext(context);vm.runInContext(fs.readFileSync(root+'scanner-readmission.js','utf8'),context);
 assert.equal(typeof context.attachReadmissionAction,'function');
 assert.match(html,/data\.reason === 'ALREADY_CHECKED_IN'\) attachReadmissionAction/);
});
