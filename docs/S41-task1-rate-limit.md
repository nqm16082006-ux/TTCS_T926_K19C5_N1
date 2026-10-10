# S-41 task 1: giới hạn yêu cầu mua vé

## Phạm vi và quy tắc

- POST /api/showtimes/{showtimeId}/seats/hold: policy `seat-hold`.
- POST /api/v1/orders/showtimes/{showtimeId}: policy `order-create`.
- Mỗi policy có bộ đếm tài khoản và IP độc lập, dùng chung giữa các showtime.
- Mặc định: 5 yêu cầu/tài khoản/60 giây; 600 yêu cầu/IP/60 giây. IP 600 là đề xuất ban đầu, cần kiểm thử tải và xác nhận trước mở bán thật.
- Cửa sổ bắt đầu từ yêu cầu đầu tiên của từng bộ đếm. Vượt một trong hai ngưỡng trả 429; nếu vượt cả hai, thời gian chờ là giá trị lớn hơn.
- Đếm các lần thử đã qua xác thực/phân quyền, bao gồm yêu cầu không hợp lệ về nghiệp vụ và yêu cầu bị giới hạn. Các lần thử tiếp theo không gia hạn cửa sổ.
- Không huỷ lượt, giữ ghế hoặc đơn khi giới hạn; không chạm webhook thanh toán, xem sự kiện hoặc các API khác.
- Repository chưa có API phòng chờ/lấy lượt: chưa triển khai các API đó. Khi có, thêm policy và gắn TicketRateLimitAttribute; ngưỡng đề xuất 5/phút khi vào, 12/phút khi đọc trạng thái.

## Hợp đồng cho FE

HTTP 429, header `Retry-After` (giây), `Cache-Control: no-store`:

```json
{"code":"RATE_LIMITED","message":"Bạn thao tác quá nhanh. Vui lòng thử lại sau.","retryAfterSeconds":30}
```

FE đọc body để hiển thị thời gian chờ. 30 là ví dụ; số thực tế là thời gian còn lại đến khi bộ đếm hết hạn.

## Cấu hình và vận hành

`TicketRateLimit:Policies:<policy>:AccountLimit`, `IpLimit`, `WindowSeconds` đều phải dương. Có thể cấu hình bằng biến môi trường, ví dụ `TicketRateLimit__Policies__seat-hold__IpLimit=1200`.

Redis Lua cập nhật hai bộ đếm nguyên tử và tự xoá bằng TTL. Các instance phải dùng chung Redis và cấu hình. Thiết kế cửa sổ cố định có thể cho phép burst ở ranh giới hai cửa sổ; đây không phải giới hạn trượt hay giải pháp chống bot đầy đủ.

Staging/production yêu cầu Redis; khi Redis không khả dụng, các API được bảo vệ trả 503 với code `RATE_LIMIT_UNAVAILABLE` và thời gian thử lại 5 giây. Development cho phép bộ đếm trong bộ nhớ; chỉ có hiệu lực trong một process và mất khi khởi động lại.

IP lấy từ Connection.RemoteIpAddress. Khi đặt sau proxy, cấu hình `ReverseProxy:KnownProxies` với IP proxy tin cậy và cấu hình proxy gửi X-Forwarded-For đúng. Không tin trực tiếp header do client gửi. Nếu chưa cấu hình proxy, nhiều người có thể bị tính chung theo IP proxy. Kiểm tra đường đi proxy thực tế trước mở bán.

## Kiểm chứng

Chạy `dotnet test src/tests/EventTicketBooking.Tests/EventTicketBooking.Tests.csproj --filter FullyQualifiedName~TicketRateLimitTests`.

Các kiểm thử bao gồm cạnh tranh đồng thời, đổi IP cùng tài khoản, nhiều tài khoản chung IP, hết cửa sổ, thời gian chờ không bị kéo dài, lỗi Redis và phản hồi middleware 429. Cần kiểm thử Redis thực tế và tải trên môi trường triển khai trước chọn ngưỡng IP cuối cùng.
