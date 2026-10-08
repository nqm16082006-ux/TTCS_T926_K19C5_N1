import http from 'k6/http';
import ws from 'k6/ws';
import { check, sleep } from 'k6';

export const options = {
  stages: [
    { duration: '30s', target: 500 },  // Ramp up to 500 users
    { duration: '1m', target: 2000 },  // Ramp up to 2000 users
    { duration: '2m', target: 2000 },  // Hold at 2000 users for 2 mins
    { duration: '30s', target: 0 },    // Ramp down
  ],
  thresholds: {
    'http_req_duration': ['p(95)<500'], // 95% API calls under 500ms
    'ws_connecting': ['p(95)<1000'],    // Websocket connection under 1s
    'ws_session_duration': ['p(95)>60000'] // Keep WS alive for at least 60s
  }
};

const BASE_URL = 'http://localhost:5012';
// Bạn cần thay thế bằng một ShowtimeId có thật trong database của bạn
const SHOWTIME_ID = 'f9f8037b-4f0e-48dc-95d2-d08837a21faf'; 

export default function () {
  // 1. Giả lập Load sơ đồ ghế khi vừa vào trang
  const res = http.get(`${BASE_URL}/api/v1/showtimes/${SHOWTIME_ID}/seats`);
  check(res, {
    'Get seats status is 200': (r) => r.status === 200,
  });

  // 2. Kết nối SignalR để nhận real-time updates
  // B2.1: Gọi API Negotiate của SignalR
  const negotiateRes = http.post(`${BASE_URL}/hubs/seat-status/negotiate?negotiateVersion=1`);
  
  if (check(negotiateRes, { 'Negotiate is 200': (r) => r.status === 200 })) {
    const connectionToken = negotiateRes.json('connectionToken');
    const wsUrl = `ws://localhost:5012/hubs/seat-status?id=${connectionToken}`;

    // B2.2: Mở kết nối WebSocket
    const res = ws.connect(wsUrl, function (socket) {
      
      socket.on('open', function () {
        // Gửi handshake bắt buộc của SignalR (kết thúc bằng ký tự 0x1E)
        socket.send('{"protocol":"json","version":1}' + String.fromCharCode(0x1e));
      });

      socket.on('message', function (message) {
        // SignalR messages ngăn cách bởi 0x1e
        const msgs = message.split(String.fromCharCode(0x1e));
        
        for (let msg of msgs) {
          if (!msg) continue;
          
          if (msg === '{}') {
            // Handshake response OK
            // Sau khi handshake xong, gửi lệnh JoinShowtimeGroup
            const joinCmd = JSON.stringify({
              type: 1, // 1 = Invocation
              invocationId: "0",
              target: "JoinShowtimeGroup",
              arguments: [SHOWTIME_ID]
            }) + String.fromCharCode(0x1e);
            
            socket.send(joinCmd);
          } else {
            const data = JSON.parse(msg);
            if (data.type === 1 && data.target === 'SeatStatusChanged') {
              // Nhận được update ghế
              // console.log(`Received update: ${JSON.stringify(data.arguments)}`);
            } else if (data.type === 6) {
              // Ping message từ server (keep-alive)
            }
          }
        }
      });

      socket.on('close', function () {
        // Bị ngắt kết nối
      });

      socket.on('error', function (e) {
        if (e.error() != 'websocket: close sent') {
          console.log('An error occurred: ', e.error());
        }
      });

      // Giữ kết nối trong 60 giây để xem sự kiện
      socket.setTimeout(function () {
        socket.close();
      }, 60000);
    });

    check(res, { 'WS connected successfully': (r) => r && r.status === 101 });
  }

  // 3. User nghỉ một chút giữa các thao tác (giả lập người dùng đọc trang)
  sleep(Math.random() * 5 + 2); 
}
