# S-62 — Xuất danh sách khách vào cửa theo suất diễn

## Phạm vi và quy tắc

CSV có mã vé, ghế, thời gian theo UTC+7, cửa vào, cờ xung đột, ghi chú, loại lượt và kết quả.

- Mặc định chỉ xuất lượt đã vào cửa. Mỗi lượt là một dòng; số vé duy nhất được thống kê riêng.
- Lượt vào lại được nhân viên duyệt theo S-31 không tự động bị coi là xung đột.
- Check-in thành công được tổng hợp từ OrderItem. Bản ghi đồng bộ offline thành công không tạo dòng vào cửa thứ hai.
- Vé có xung đột offline được gắn cờ ở dòng đã vào để ban tổ chức biết cần đối soát.
- Chế độ nhật ký đối soát bổ sung từng lượt offline xung đột, với đúng cửa/giờ quét và kết quả “Xung đột”. Dòng này không xác nhận khách đã vào.
- Xuất mặc định không gồm vé chưa vào. Tham số `includeNotCheckedIn` cũ chỉ được giữ cho API tương thích; giao diện mới không bật tùy chọn này.
- Không có dữ liệu: xem trước báo rỗng, CSV chỉ có tiêu đề.

## Quyền truy cập

Admin được xuất; Organizer chỉ được xem/xuất suất diễn thuộc sự kiện mình sở hữu. Staff và Customer không được xuất. Hiện chưa có quan hệ phân công Staff theo suất diễn nên không cấp quyền xuất toàn hệ thống cho Staff. Cả API xem trước và CSV dùng chung kiểm tra quyền.

Trang `checkin-export.html` sử dụng phiên đăng nhập hiện tại, không chứa thông tin đăng nhập demo và không tự chuyển sang tài khoản Admin. Các liên kết xuất CSV ở danh sách sự kiện, chi tiết suất diễn, overview và live-checkin trỏ tới trang này. Trang PDF S-60 giữ luồng riêng.

## API

- `GET /api/shows/{showId}/checkins/export-csv`
- `GET /api/shows/{showId}/checkins/export-preview`
- URL CSV cũ `/api/v1/checkin/showtimes/{showId}/export-csv` gọi cùng action, cùng quyền.

Bộ lọc chung: `gateName`, `onlyConflicts`, `includeScanLog` (mặc định false). CSV hỗ trợ các bí danh cửa cũ `doorName`, `gateId`, `doorId` dưới dạng chuỗi tên cửa; không có bảng mã cổng riêng.

JSON xem trước trả tối đa 200 dòng cùng tổng số dòng, lượt đã vào, vé duy nhất và dòng có xung đột sau khi lọc. CSV xuất toàn bộ kết quả. UTF-8 BOM, CRLF, escape dấu phẩy/nháy kép/xuống dòng và thêm dấu nháy đơn cho dữ liệu có thể được Excel diễn giải thành công thức. Phản hồi có `Cache-Control: no-store`.

Không thay đổi schema database. Dữ liệu được đọc từ Tickets, OrderItems, TicketReadmissions và OfflineCheckInRecords. Chưa có nhật ký độc lập cho mọi lần quét online bị từ chối; những lần không được lưu không thể khôi phục từ S-62. Hiện việc tổng hợp vẫn giữ danh sách trong bộ nhớ; cần bổ sung phân trang/streaming hoặc tác vụ nền nếu triển khai xuất dữ liệu quy mô rất lớn.

## Chạy riêng trên local (PowerShell 7, Docker Desktop, .NET 9)

```powershell
./scripts/start-s62-local.ps1 -SeedDemo
```

Script tạo cấu hình riêng trong `.tmp/s62-local/.env`, PostgreSQL riêng `s62_local` tại `127.0.0.1:55432`, Redis riêng `127.0.0.1:56379` và chạy API tại `http://localhost:5013`. Không dùng file `.env` ở gốc và không truy cập database cloud. Giữ container/volume để có thể chạy lại. Chỉ dừng process local được ghi trong `app.pid` sau khi xác minh đường dẫn. Không commit cấu hình riêng.

- Mở `http://localhost:5013/login.html`, đăng nhập tài khoản seed local `organizer@eventticket.com` / `Organizer@123456`.
- Mở `http://localhost:5013/checkin-export.html`, chọn sự kiện `[LOCAL S62] Đối soát vào cửa`.
- Mặc định: 4 lượt đã vào, 3 vé duy nhất; vé A4 chưa vào không được xuất.
- Chọn nhật ký đối soát: 5 dòng, có lượt xung đột tại Cửa C.
- Lọc Cửa C: 1 dòng xung đột, 0 lượt đã vào.
- Lượt vào lại Cửa B có ghi chú nhiều dòng và không bị gắn cờ xung đột.

Các tài khoản/mật khẩu ở trên chỉ dành cho dữ liệu demo trên local. Tool `tools/S62LocalDemo` chỉ chấp nhận đúng database local nêu trên. Tool tạo fixture lặp lại an toàn và kiểm tra API thật với Admin, Organizer sở hữu, Organizer khác, Staff và request chưa đăng nhập. CSV kiểm tra được lưu ở `.tmp/s62-local/s62-audit.csv`.

Lưu ý có sẵn của repository: database mới chạy migrations hiện chưa có bảng `refund_requests` của S-48; worker hoàn tiền có thể ghi lỗi thiếu bảng. Đây là phần ngoài S-62, không ngăn xuất check-in. Không tự sửa schema S-48 bằng tool demo S-62.

## Kiểm thử

```powershell
dotnet test src/tests/EventTicketBooking.Tests --filter 'FullyQualifiedName~CheckinExportS62Tests|FullyQualifiedName~StoryS02AuthTests|FullyQualifiedName~AuditHttpFlowTests'
```

S-62 bao phủ dữ liệu đã/chưa vào, vào lại hợp lệ, xung đột đúng cửa, không nhân đôi lượt offline thành công, định dạng CSV và phòng công thức Excel. Kiểm thử quyền sở hữu được cập nhật để dùng controller chung. `start-s62-local.ps1 -SeedDemo` bổ sung kiểm tra tích hợp với PostgreSQL thật và HTTP.

## Trạng thái đơn tại thời điểm vào cửa

S-62 tra nhật ký thay đổi trạng thái đơn để đánh giá từng lượt vào và vào lại. Đơn được hủy/hoàn tiền sau lượt vào không tự động biến lượt vào hợp lệ trước đó thành xung đột. Thông tin hủy sau lượt vào được giữ ở ghi chú. Lượt vào sau khi đơn đã hủy vẫn bị gắn cờ; xung đột offline vẫn được giữ độc lập. Dữ liệu cũ không có lịch sử trạng thái tiếp tục được kiểm tra thận trọng theo trạng thái hiện tại để đối soát.

## Tải Excel có định dạng

Nút Tải Excel (.xlsx) gọi `/api/shows/{showId}/checkins/export-excel`, dùng cùng quyền, dữ liệu và bộ lọc với CSV. File XLSX có 8 cột thật, tiêu đề sự kiện/suất diễn, số liệu tổng hợp, ngày giờ kiểu ngày Excel (UTC+7), độ rộng cột, ngắt dòng ghi chú, tiêu đề cố định, bộ lọc và màu cảnh báo xung đột. Chuỗi luôn được lưu dạng văn bản, không tạo công thức từ nội dung đầu vào. CSV vẫn giữ định dạng chuẩn, không thêm dòng `sep=`; nên dùng XLSX để mở trực tiếp với định dạng sẵn.
