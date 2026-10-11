# S-62: Xuất danh sách khách vào cửa theo suất diễn

## 1. Mô tả tính năng
Tính năng cho phép Người tổ chức sự kiện (Organizer), Quản trị viên (Admin) và Nhân viên kiểm soát vé (Staff) xuất danh sách toàn bộ khách đã vào cửa theo từng suất diễn ra tệp CSV chuẩn hóa. Tệp CSV được tối ưu hóa đặc biệt với UTF-8 BOM để mở trên Microsoft Excel mà không bị vỡ font Tiếng Việt.

## 2. API Endpoint
`GET /api/shows/{showId}/checkins/export-csv`

Bí danh hỗ trợ: `GET /api/v1/checkin/showtimes/{showId}/export-csv`

### Tham số Query String (tùy chọn)
- `gateName` / `doorName` / `gateId` / `doorId` (string, tùy chọn): Lọc theo tên cửa hoặc cổng soát vé cụ thể.
- `onlyConflicts` (bool, tùy chọn): Nếu `true`, chỉ xuất các vé/lượt quét có cờ xung đột.
- `includeNotCheckedIn` (bool, tùy chọn, mặc định `false`): Nếu `true`, bao gồm cả các vé chưa vào cửa.

### Phản hồi
- **Mã HTTP**: 200 OK
- **Content-Type**: `text/csv; charset=utf-8`
- **Content-Disposition**: `attachment; filename="danh-sach-checkin-suat-{showId}.csv"`
- **Mã hóa**: UTF-8 có BOM (`0xEF, 0xBB, 0xBF`).

## 3. Cấu trúc tệp CSV xuất bản
Header tệp CSV:
```csv
Mã vé,Số ghế,Thời gian vào,Cửa vào,Cờ xung đột,Ghi chú xung đột
```

### Các trường dữ liệu:
1. **Mã vé (`TicketCode`)**: Mã vé điện tử ngẫu nhiên bảo mật của vé.
2. **Số ghế (`SeatNumber` / `SeatCode`)**: Vị trí ghế ngồi (ghép Hàng ghế + Số ghế, ví dụ: `A12`, `VIP-01`).
3. **Thời gian vào (`CheckinTime`)**: Thời gian khách qua cửa (giờ Việt Nam UTC+7, định dạng `yyyy-MM-dd HH:mm:ss`).
4. **Cửa vào (`GateName` / `DoorName`)**: Cửa hoặc cổng soát vé (ví dụ: `Cổng A`, `Cổng VIP`).
5. **Cờ xung đột (`IsConflict` / `HasConflict`)**: Hiển thị `Có` hoặc `Không`.
6. **Ghi chú xung đột (`ConflictNote`)**: Lý do xung đột nếu có.

## 4. Các quy tắc phát hiện Cờ xung đột
1. **Vé quét lại nhiều lần / Vào bổ sung (Re-admissions)**:
   - Vé có bản ghi trong bảng `TicketReadmissions` (S-31).
   - Lượt check-in ban đầu được gắn cờ xung đột kèm ghi chú số lượt vào bổ sung.
   - Mỗi lượt vào bổ sung được xuất thành một dòng riêng biệt kèm lý do và tên nhân viên duyệt.
2. **Vé chưa thanh toán / Hủy / Quá hạn nhưng có dữ liệu quét**:
   - Đơn hàng ở trạng thái khác `OrderStatus.Paid` (ví dụ: `Cancelled`, `Expired`, `Pending`) nhưng vẫn ghi nhận lượt vào cửa.
3. **Vé sai suất diễn / Xung đột ngoại tuyến**:
   - Các lượt quét ngoại tuyến được ghi nhận trong `OfflineCheckInRecords` (S-35 & S-36) có `IsConflict = true` (quét ở nhiều cửa cùng lúc, quét sai suất diễn...).

## 5. Kiểm thử tự động
Chạy kiểm thử riêng cho S-62:
```powershell
dotnet test src/tests/EventTicketBooking.Tests --filter FullyQualifiedName~CheckinExportS62Tests
```
Toàn bộ 10 bài test bao phủ:
- Kiểm tra validation `showId` không tồn tại (404) hoặc rỗng (400).
- Kiểm tra tính toán cờ xung đột cho các trường hợp nghiệp vụ.
- Kiểm tra bộ lọc theo cổng và bộ lọc chỉ lấy xung đột.
- Kiểm tra định dạng CSV có UTF-8 BOM và chuẩn hóa ký tự RFC 4180.
- Kiểm tra endpoint controller trả về file download chính xác.
