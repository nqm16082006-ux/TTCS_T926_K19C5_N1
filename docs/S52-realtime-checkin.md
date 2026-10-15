# S-52: Ban tổ chức xem số khách đã vào cửa theo thời gian thực

## 1. Mục tiêu & Nghiệp vụ
Tính năng phục vụ Ban tổ chức (Organizer) và Quản trị viên (Admin) theo dõi tiến độ soát vé, tổng số khách đã vào cửa so với tổng vé bán ra theo thời gian thực (Real-time). Giúp Ban tổ chức nắm bắt chính xác lưu lượng khán giả tại các cổng để quyết định thời điểm mở màn chương trình biểu diễn hoặc điều phối nhân sự chống ùn tắc.

## 2. Các chỉ số cốt lõi (Core Metrics)
1. **Số liệu tổng quan (Overview Metrics)**:
   - **Tổng vé bán ra (`TotalSoldTickets`)**: Các vé thuộc đơn hàng đã thanh toán thành công (`OrderStatus.Paid`).
   - **Đã vào cửa (`CheckedInCount`)**: Số lượng vé đã check-in thành công (`IsCheckedIn = true`).
   - **Chưa vào cửa (`RemainingCount`)**: Số vé chưa qua cửa soát vé (`TotalSoldTickets - CheckedInCount`).
   - **Tỉ lệ vào cửa (`CheckInPercentage`)**: Tỉ lệ phần trăm đã vào cửa (`(CheckedInCount / TotalSoldTickets) * 100%`).

2. **Vận hành theo thời gian & vị trí (Velocity & Breakdown)**:
   - **Tốc độ qua cửa (`CurrentVelocityPerMinute`)**: Số lượng khách/phút trung bình trong 5 phút gần nhất.
   - **Lưu lượng theo phút (`Timeline`)**: Lịch sử số lượng khách vào cửa trong 30 phút gần nhất (bucket theo từng phút) phục vụ vẽ biểu đồ.
   - **Bóc tách theo lối vào (`GateBreakdown`)**: Số lượng và tỉ lệ % khách vào phân bổ theo từng cửa (`Cổng A`, `Cổng B`, `Cổng VIP`...).

3. **Xử lý dữ liệu ngoại tuyến (Offline Sync S-35/S-36)**:
   - **Số vé sync ngoại tuyến (`OfflineSyncedCount`)**: Số lượt quét từ thiết bị ngoại tuyến đã được đồng bộ hợp lệ lên server.
   - **Mốc thời gian sync gần nhất (`LastSyncTime`)**: Thời điểm máy chủ tiếp nhận lô vé đồng bộ gần nhất.

---

## 3. Kiến trúc kỹ thuật & API

### 3.1. REST API Endpoint
`GET /api/v1/checkin/showtimes/{showtimeId}/live-stats`

- **Phân quyền**: `[RequireRole("Admin", "Organizer", "Staff")]`
  - `Admin`: Được xem số liệu của mọi sự kiện.
  - `Organizer`: Kiểm tra quyền sở hữu sự kiện (`showtime.Event.OwnerId == currentUserId`).
- **Phản hồi mẫu (200 OK)**:
```json
{
  "showtimeId": "3fa85f64-5717-4562-b3fc-2c963f66afa6",
  "eventId": "2cb14f24-2117-4562-a3fc-1c963f66afa1",
  "eventTitle": "Live Concert Hòa Nhạc 2026",
  "startTime": "2026-11-20T19:00:00Z",
  "endTime": "2026-11-20T22:30:00Z",
  "totalSoldTickets": 3000,
  "checkedInCount": 2450,
  "remainingCount": 550,
  "checkInPercentage": 81.7,
  "currentVelocityPerMinute": 42.0,
  "timeline": [
    { "minuteBucket": "2026-11-20T18:30:00Z", "count": 25 },
    { "minuteBucket": "2026-11-20T18:31:00Z", "count": 48 }
  ],
  "gateBreakdown": [
    { "gateName": "Cổng A", "checkedInCount": 1400, "percentageOfTotal": 57.1 },
    { "gateName": "Cổng B", "checkedInCount": 1050, "percentageOfTotal": 42.9 }
  ],
  "offlineSyncedCount": 45,
  "lastSyncTime": "2026-11-20T18:45:12Z",
  "generatedAt": "2026-11-20T18:50:00Z"
}
```

### 3.2. SignalR Real-Time Hub
- **Hub Route**: `/hubs/checkin-live`
- **Tên Group**: `showtime_{showtimeId}`
- **Events phát ra**:
  - `CheckinUpdated`: Kích hoạt khi có lượt quét vé online thành công tại cổng (S-30/S-31).
  - `CheckinBatchSynced`: Kích hoạt khi có một lô vé ngoại tuyến đồng bộ bù thành công (S-35).

---

## 4. Giao diện Ban tổ chức (Frontend)
1. **Trực tiếp từ màn hình Tổng quan (`overview.html`)**:
   - Thêm nút **"Soát vé Real-time (S-52)"** tại từng dòng suất diễn trong bảng hiệu suất.
2. **Dashboard chuyên dụng Fullscreen / Kiosk (`live-checkin.html`)**:
   - URL: `/live-checkin.html?showId={showtimeId}`
   - Thiết kế giao diện trung tâm điều hành hiện đại, sắc nét.
   - Thẻ KPI lớn, thanh tiến độ trực quan, biểu đồ cột mini lưu lượng 30 phút và bảng phân bổ các cổng.
   - Tự động kết nối WebSocket SignalR và có cơ chế Fallback Polling (10s) khi mạng chập chờn.
   - Nút liên kết xuất tệp CSV danh sách khách vào cửa (S-62).

---

## 5. Kiểm thử tự động (Automated Tests)
Chạy bộ kiểm thử cho Story S-52:
```powershell
dotnet test src/tests/EventTicketBooking.Tests --filter FullyQualifiedName~CheckinLiveStatisticsS52Tests
```
Bao phủ 11 bài kiểm thử:
- Kiểm tra tính toán các chỉ số tổng quan (TotalSold, CheckedIn, Remaining, Percentage).
- Kiểm tra phân loại và tính % tải theo từng cổng soát vé.
- Kiểm tra tính tốc độ vào cửa (Velocity: khách/phút trong 5 phút gần nhất).
- Kiểm tra chia bucket lưu lượng theo từng phút trong 30 phút.
- Kiểm tra tích hợp dữ liệu đồng bộ ngoại tuyến từ `OfflineCheckInRecords`.
- Kiểm tra phân quyền: Organizer sở hữu, Organizer khác (403), Admin (200), mã suất không hợp lệ (400), không tồn tại (404).
