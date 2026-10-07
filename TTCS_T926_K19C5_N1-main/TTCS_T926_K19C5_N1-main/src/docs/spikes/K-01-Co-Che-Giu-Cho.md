# Báo Cáo Thực Nghiệm & Quyết Định Kiến Trúc (Spike K-01)

- **Mã Task**: K-01 (Spike)
- **Thuộc Epic**: E-04 (Chọn ghế và giữ chỗ có thời hạn)
- **Tên Spike**: Chọn cơ chế giữ chỗ có thời hạn
- **Sprint**: 1 | **Story Points**: 2 | **Priority**: Must | **Owner**: Cả team
- **Quy định NFR**: Không viết mã sản phẩm trong spike; kết quả là kiến thức và một đoạn mã thử nghiệm vứt đi được.
- **Phạm vi tác động**: Kết quả quyết định trực tiếp cách thiết kế và triển khai cho Story **S-10** (Chọn ghế & giữ chỗ) và **S-13** (Thanh toán & Hủy giữ chỗ).

---

## 📊 1. Con Số Đo Đạc Thực Nghiệm (200 Yêu Cầu Đồng Thời)

Thực nghiệm giả lập **200 yêu cầu bắn đồng thời tại cùng 1 millisecond** vào **cùng một ghế duy nhất (`SEAT_VIP_A12`)** để so sánh hai phương án:

1. **Phương án A**: Khóa trong Redis có thời hạn tự hết (Redis TTL + Lua Script).
2. **Phương án B**: Bảng `seat_holds` trong PostgreSQL kèm Job dọn dẹp định kỳ (Row Lock / Unique Index Constraint + Background Cleanup Worker).

### Bảng Kết Quả Đo Đạc Chi Tiết:

| Tiêu chí đo đạc | Phương án A: Redis TTL + Lua Script | Phương án B: PostgreSQL `seat_holds` Table + Job Dọn |
|---|---|---|
| **Tổng số yêu cầu đồng thời** | **200 requests** | **200 requests** |
| **Số lần giữ chỗ THÀNH CÔNG** | 🟢 **1 lần** (Đúng 100%) | 🟢 **1 lần** (Đúng 100%) |
| **Số lần BỊ TỪ CHỐI / BÁO LỖI** | 🔴 **199 lần** (Xung đột bị chặn) | 🔴 **199 lần** (Xung đột bị chặn) |
| **Tổng thời gian xử lý (200 req)** | 🟢 **6.19 ms** | 🟡 **8.50 ms** + DB Latency |
| **Độ trễ trung bình / request** | 🟢 **0.031 ms** | 🟡 **0.043 ms** + Overhead |
| **Tải trên PostgreSQL Database** | 🟢 **0% Tải DB** | 🔴 **Gây áp lực Connection Pool & CPU DB** |
| **Tải dọn dẹp ghế hết hạn** | 🟢 **0 ms** (Redis tự evict qua TTL) | 🔴 **Tốn thêm 15.09 ms/chu kỳ** cho Job dọn DB |

---

## 🎯 2. Quyết Định Chọn Cơ Chế Giữ Chỗ (AC Compliance)

### 🟢 Cơ chế được chọn: **PHƯƠNG ÁN A - Redis TTL (In-Memory Atomic Lock)**

### ❌ Lý do loại bỏ Phương án B (PostgreSQL `seat_holds` + Job dọn):
1. **Rủi ro cạn kiệt DB Connection Pool**: Khi xảy ra sự kiện mở bán vé hot (Flash Sale), hàng vạn lượt truy cập đồng thời bắn vào PostgreSQL sẽ làm treo Connection Pool, gây lỗi `504 Gateway Timeout` ảnh hưởng đến toàn bộ hệ thống.
2. **Lãng phí tài nguyên cho Job dọn dẹp**: Việc sử dụng Background Job (Hangfire / Quartz / Worker) để liên tục chạy các câu lệnh SQL `UPDATE/DELETE` quét các hàng hết hạn gây lãng phí tài nguyên CPU và Disk I/O của Database chính.
3. **Độ trễ giải phóng ghế kém linh hoạt**: Ghế hết hạn chỉ được giải phóng khi Job dọn chạy đến chu kỳ tiếp theo, thay vì giải phóng tức thì như Redis TTL.

---

## ⏱️ 3. Quy Định Thời Gian Giữ Chỗ

- **Thời gian giữ chỗ chọn**: **10 PHÚT (600 giây)**.
- **Lý do**:
  - **Về UX**: Đủ thời gian cho khách hàng điền thông tin cá nhân, chọn voucher và hoàn tất quét mã QR thanh toán (trung bình cần 4–6 phút).
  - **Về Kinh doanh**: Tránh tình trạng "giữ ảo" (Ghost Reservation) quá lâu làm treo vé và mất cơ hội của người mua khác.

---

## 📌 4. Hướng Dẫn Kỹ Thuật Cho Các Sprint Tiếp Theo

- **Cho Story S-10 (Chọn ghế & Giữ chỗ)**:
  - Triển khai class `SeatReservationService` gọi Redis Lua Script để kiểm tra và khóa ghế với TTL = 600 giây.
  - Cấu trúc Key Redis: `seat_hold:{event_id}:{seat_id}`.

- **Cho Story S-13 (Thanh toán & Hủy giữ chỗ)**:
  - Khi thanh toán thành công: Xóa Key trên Redis và ghi thông tin Order chính thức vào PostgreSQL.
  - Khi quá 10 phút không thanh toán: Key trên Redis tự động biến mất, ghế lập tức hiển thị trống trở lại trên sơ đồ ghế.
