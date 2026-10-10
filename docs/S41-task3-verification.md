# S-41 task 3 — kết quả kiểm thử

Ngày kiểm tra: 07/10/2026.

## Kết quả đã thực hiện

| Nhóm | Kết quả | Phạm vi |
|---|---|---|
| BE regression | 135/135 đạt | Toàn bộ EventTicketBooking.Tests, gồm 7 kiểm thử giới hạn |
| FE regression | 11/11 đạt | 5 kiểm thử giới hạn + 6 kiểm thử FE hiện có |
| Đồng hồ giữ vé | Đạt | Restore, hết hạn, hết hạn đồng thời, múi giờ |
| Redis thực tế | Đạt | Hai limiter dùng cùng Redis, 20 lần thử đồng thời chỉ cho phép 5, đổi IP không vượt ngưỡng tài khoản, hết TTL được gọi lại, 3 tài khoản chung IP bị chặn ở ngưỡng 2 |
| Test web thủ công | Người dùng xác nhận đạt | Người dùng báo đã test OK task 1 và task 2; ảnh Console trước đó cho thấy 5 phản hồi 400 và phản hồi thứ 6 là 429 |

## Kiểm thử HTTP bổ sung

TicketRateLimitHttpTests khởi động Kestrel tại cổng ngẫu nhiên, đi qua routing và middleware thực tế. Xác minh 401 khi thiếu danh tính, 20 yêu cầu đồng thời (5 được phép/15 bị giới hạn), body RATE_LIMITED, header Retry-After, Cache-Control no-store, hai policy độc lập, API public không bị chặn, thời gian chờ giảm và yêu cầu được mở lại đúng hạn. Test riêng xác minh hai action controller thật có đúng metadata policy.

HTTP fixture dùng endpoint tối giản và danh tính giả lập để cô lập rate limiter; không thay thế kiểm thử toàn bộ đăng nhập/phân quyền và giao dịch mua vé. FE tự động dùng phản hồi API giả lập; xác nhận trên web là thông tin do người dùng cung cấp.

## Chạy lại

```powershell
dotnet test src/tests/EventTicketBooking.Tests/EventTicketBooking.Tests.csproj --no-restore
node --test src/tests/booking-rate-limit.test.cjs src/tests/frontend-audit.test.cjs
node src/tests/hold-countdown-check.cjs
dotnet run --project tools/RateLimitSmoke/RateLimitSmoke.csproj
```

RateLimitSmoke mặc định kết nối localhost:6379. Có thể đặt S41_TEST_REDIS để chọn Redis test; không in chuỗi kết nối. Mỗi lần chạy dùng policy ngẫu nhiên và TTL 2 giây, không thay đổi bộ đếm mua vé hay flush dữ liệu Redis. Chạy trên Redis thử nghiệm/local. Redis không kết nối được sẽ trả exit code 2 thay vì báo đạt.

## Điều kiện trước mở bán thật

- Ngưỡng IP 600/phút/policy vẫn là cấu hình đề xuất; cần nhóm xác nhận và kiểm thử tải theo lượng người dùng chung mạng dự kiến.
- Kiểm tra ReverseProxy:KnownProxies trên môi trường triển khai để lấy IP người mua đúng; chưa kiểm chứng proxy triển khai thật.
- API phòng chờ chưa có trong repository; chưa nằm trong kết quả kiểm thử này.
- Các cảnh báo nullable từ bộ kiểm thử hiện có vẫn xuất hiện; không có lỗi build hoặc test thất bại trong lần chạy này.

Task 3 đã có kiểm thử tự động và kết quả Redis thực tế. Báo cáo này không xác nhận khả năng chịu tải production hoặc cấu hình proxy production.
