# S-60 — Xuất sơ đồ ghế đã bán dạng PDF

## 1. Tổng quan và Mục tiêu nghiệp vụ

- **Story ID**: S-60 (Jira TTKN-127)
- **Thuộc Epic**: E-09 — Báo cáo và đối soát cho ban tổ chức
- **Mức độ ưu tiên**: Could | Story Points: 2 SP
- **Phụ thuộc**: S-09 (Xem sơ đồ ghế còn trống của một suất diễn)
- **User Story**: 
  > *Là ban tổ chức tôi muốn in sơ đồ ghế với trạng thái đã bán để dán ở cửa và lưu hồ sơ sự kiện.*
- **Tiêu chí chấp nhận (Acceptance Criteria)**:
  - Xuất tệp PDF đúng **1 trang A4 Landscape duy nhất**, có sơ đồ tô màu theo trạng thái và chú giải.
  - Dữ liệu phản ánh chính xác trạng thái ghế tại thời điểm xuất theo thời gian thực (Real-time).
  - Kèm ký hiệu phân biệt trạng thái (`[✕]` Đã bán, `[~]` Đang giữ chỗ, `[○]` Còn trống) hỗ trợ người khiếm thị màu hoặc khi in đen trắng (photocopy) dán ở cửa sự kiện theo tinh thần S-09.

---

## 2. Quy tắc nghiệp vụ và Trạng thái ghế

1. **Trạng thái Đã bán (SOLD)**:
   - Ghế có `Seat.Status == "SOLD"` hoặc ghế nằm trong các đơn hàng đã thanh toán thành công (`Order.Status == OrderStatus.Paid`).
   - Màu sắc: Đỏ nổi bật (`#EF4444`, viền `#DC2626`), ký hiệu `[✕]`.
2. **Trạng thái Đang giữ chỗ (HELD)**:
   - Ghế đang có bản ghi giữ chỗ `SeatHold` với `Status == "ACTIVE"` và thời gian hết hạn `ExpiresAt > now`.
   - Các lượt giữ chỗ đã hết hạn (`ExpiresAt <= now`) tự động hoàn về trạng thái Còn trống (AVAILABLE).
   - Màu sắc: Vàng hổ phách (`#F59E0B`, viền `#D97706`), ký hiệu `[~]`.
3. **Trạng thái Còn trống (AVAILABLE)**:
   - Các ghế còn lại chưa bán và không bị giữ chỗ còn hiệu lực.
   - Màu sắc: Xanh lục tươi (`#10B981`, viền `#059669`), ký hiệu `[○]`.
4. **Quy chuẩn hiển thị PDF**:
   - Khổ giấy: **A4 Landscape**.
   - Bố cục:
     - **Header**: Huy hiệu "S-60 REPORT", Tiêu đề sự kiện, Địa điểm, Tên suất diễn, Khung thời gian và Thời điểm kết xuất (giờ Việt Nam UTC+7).
     - **Screen / Stage**: Thanh định hướng sân khấu chính để người xem dễ dàng định vị phương hướng chỗ ngồi.
     - **Seat Grid**: Lưới ma trận ghế phân bổ theo hàng (A, B, C...) và số thứ tự ghế, tự động co giãn (`ScaleToFit`) vừa khít 1 trang dù quy mô nhỏ hay lớn (đã kiểm thử stress test 3.000 ghế).
     - **Footer**: Bảng chú giải đầy đủ mã màu, ký hiệu, số lượng ghế và tỷ lệ phần trăm từng trạng thái, cùng số lượng tổng cộng.

---

## 3. Phân quyền truy cập (Authorization)

- **Admin & Staff**: Có quyền xem và xuất PDF sơ đồ ghế của tất cả sự kiện và suất diễn trên toàn hệ thống.
- **Organizer (Ban tổ chức)**: Chỉ có quyền xem và xuất PDF các suất diễn thuộc sự kiện do chính mình sở hữu (`Event.OwnerId == CurrentUserId`). Mọi nỗ lực truy cập sự kiện của Organizer khác đều trả về `403 Forbidden`.
- **Khách hàng (Customer) / Chưa xác thực**: Bị từ chối truy cập (`401 Unauthorized` hoặc `403 Forbidden`).

---

## 4. Đặc tả API

### 4.1. Xuất tệp PDF sơ đồ ghế
- **Phương thức**: `GET`
- **Đường dẫn**:
  - `GET /api/v1/showtimes/{showtimeId}/seats/export-pdf`
  - `GET /api/shows/{showtimeId}/seats/export-pdf`
  - `GET /api/showtimes/{showtimeId}/export-seatmap-pdf`
- **Query Parameters**:
  - `nowOverride` *(DateTime?, tùy chọn)*: Dùng trong kiểm thử để cố định mốc thời gian đánh giá thời hạn giữ chỗ.
- **Phản hồi**:
  - `200 OK`: File stream dạng `application/pdf`, tên tệp `so-do-ghe-{EventTitle}-{ShowtimeId:8}.pdf`.
  - `400 BadRequest`: Mã suất diễn không hợp lệ.
  - `401 Unauthorized`: Chưa xác thực token JWT.
  - `403 Forbidden`: Người dùng không có quyền trên sự kiện này.
  - `404 NotFound`: Không tìm thấy suất diễn.

### 4.2. Lấy dữ liệu thống kê sơ đồ ghế (JSON Metadata)
- **Phương thức**: `GET`
- **Đường dẫn**:
  - `GET /api/v1/showtimes/{showtimeId}/seats/export-data`
  - `GET /api/shows/{showtimeId}/seats/export-data`
- **Phản hồi**:
  ```json
  {
    "showtimeId": "3fa85f64-5717-4562-b3fc-2c963f66afa6",
    "showtimeName": "Đêm Diễn 1",
    "startTime": "2026-10-25T19:00:00Z",
    "endTime": "2026-10-25T22:00:00Z",
    "eventId": "e6a2b841-...",
    "eventTitle": "Concert Mùa Thu",
    "location": "Nhà hát Lớn Hà Nội",
    "exportedAt": "2026-10-21T14:30:00Z",
    "totalSeats": 100,
    "soldCount": 45,
    "heldCount": 5,
    "availableCount": 50,
    "soldPercentage": 45.0,
    "heldPercentage": 5.0,
    "availablePercentage": 50.0,
    "seats": [
      {
        "id": "...",
        "row": "A",
        "seatNumber": 1,
        "seatCode": "A1",
        "categoryName": "VIP",
        "price": 500000,
        "status": "SOLD"
      }
    ]
  }
  ```

---

## 5. Điểm chạm giao diện người dùng (UI Integration)

1. **Trang Báo cáo Sơ đồ ghế (`export-test.html` / `export_test.html`)**:
   - Tab 1: Xuất sơ đồ ghế PDF (Single-page A4).
   - Tự động nạp danh sách suất diễn và huy hiệu số liệu trực tiếp (`[✕] Đã bán`, `[~] Giữ chỗ`, `[○] Còn trống`).
   - Nút **Xem trước PDF trên trang**: Tích hợp iframe hiển thị bản in PDF trực tiếp không cần rời trang.
   - Nút **Tải tệp PDF (A4 1 Trang)**: Tải tệp PDF về máy tính để in ấn hoặc lưu kho.
2. **Trang Quản lý Sự kiện (`events.html`)**:
   - Menu ngữ cảnh từng dòng (`row-context-menu`): Bổ sung mục *"Xuất sơ đồ ghế đã bán (PDF)"*.
   - Nút thao tác nhanh trên từng hàng bảng và thẻ sự kiện: Icon `picture_as_pdf` chuyển thẳng tới trang xuất với `showId`.
3. **Trang Chi tiết Sự kiện (`event-detail.html`)**:
   - Trong menu "Thao tác khác" của từng suất diễn: Tùy chọn *"Xuất sơ đồ ghế (PDF)"*.
4. **Trang Tổng quan Ban tổ chức (`overview.html`)**:
   - Trong bảng tiến độ suất diễn: Bổ sung liên kết nhanh *"Sơ đồ PDF"*.
5. **Trang Đối soát Vào cửa (`checkin-export.html`)**:
   - Bổ sung nút *"Xuất sơ đồ ghế (PDF)"* cạnh các nút xuất CSV / Excel.
6. **Trang Chọn ghế Khách hàng (`seat-map.html`)**:
   - Tự động nhận diện tài khoản Admin / Organizer để hiển thị nút *"Xuất PDF (S-60)"* trên thanh công cụ sơ đồ ghế.

---

## 6. Kiểm thử tự động

### 6.1. Unit & Integration Tests (XUnit)
Chạy bộ kiểm thử 9 kịch bản tự động:
```powershell
dotnet test --filter "FullyQualifiedName~SeatMapPdfExportS60Tests"
```
Bao phủ:
- Báo lỗi `KeyNotFoundException` khi suất diễn không tồn tại.
- Tính toán chính xác trạng thái ghế: SOLD, HELD, AVAILABLE và xử lý giữ chỗ đã hết hạn.
- Kiểm tra tính hợp lệ của tệp PDF (Header `%PDF-`).
- Kiểm tra Controller trả về đúng `FileContentResult` với ContentType `application/pdf`.
- Kiểm tra phân quyền: Trả về `403 Forbidden` khi Organizer không sở hữu sự kiện hoặc thiếu claim danh tính.
- Kiểm tra quyền Admin: Cho phép xuất PDF mọi sự kiện.
- Stress test 3.000 ghế: Đảm bảo kết xuất PDF gói gọn đúng 1 trang A4 và thời gian thực thi dưới 10 giây.
- Kiểm tra API JSON Metadata `GetSeatMapData`.

### 6.2. Kịch bản E2E PowerShell
Chạy kịch bản kiểm tra toàn diện qua HTTP API thực tế:
```powershell
./test_export_s60.ps1
```
