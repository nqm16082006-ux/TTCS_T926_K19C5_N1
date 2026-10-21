# S-57 Chuyển nhượng vé cho người khác (Ticket Transfer - MVP)

## 1. Tổng quan

Chức năng cho phép người mua vé hợp lệ (`OrderStatus.Paid`) chuyển nhượng quyền sở hữu vé cho một tài khoản khác trong hệ thống khi không thể tham gia sự kiện.

- **Mã Story:** S-57
- **Epic:** E-06 (Vé điện tử và soát vé)
- **Mức độ ưu tiên:** Could / MVP gọn
- **Phương án triển khai:** Chuyển nhượng tức thì (Instant Transfer) qua email người nhận đã có tài khoản trên hệ thống.

---

## 2. Quy tắc nghiệp vụ (Business Rules)

1. **Điều kiện chuyển nhượng:**
   - Người gửi phải là chủ sở hữu hợp pháp hiện tại của vé (`Ticket.CurrentUserId ?? Order.UserId == CurrentUserId`).
   - Đơn hàng phải ở trạng thái đã thanh toán (`OrderStatus.Paid`).
   - Vé chưa từng check-in vào cửa (`OrderItem.IsCheckedIn == false`).
   - Sự kiện cho phép chuyển nhượng (`Event.AllowTicketTransfer == true`).
   - Trước thời điểm đóng chuyển nhượng (`Showtime.StartTime - Event.TransferCutoffHours > UtcNow`, mặc định 2 tiếng trước giờ diễn).
   - Vé chưa vượt quá số lần chuyển nhượng tối đa (`Ticket.TransferCount < 1` - MVP tối đa 1 lần).
2. **Xử lý mã vé & QR:**
   - Khi chuyển nhượng thành công, mã vé cũ (`OldTicketCode`) bị thu hồi và thay bằng một mã vé ngẫu nhiên mới (`NewTicketCode`).
   - Mã QR cũ của người gửi lập tức mất hiệu lực.
   - Nếu nhân viên soát vé quét mã QR cũ tại cổng, hệ thống trả về lỗi từ chối rõ ràng: `TICKET_TRANSFERRED` (*"Vé này đã được chuyển nhượng cho người khác. Mã vé cũ không còn hiệu lực."*).
3. **Hiển thị & Quyền truy cập:**
   - Người nhận: Vé mới xuất hiện trong màn hình "Vé của tôi", kèm mã QR mới và nhãn *"Nhận từ: [email người gửi]"*.
   - Người gửi: Vé chuyển sang trạng thái *"Đã chuyển nhượng cho: [email người nhận]"*, không còn mã QR, không thể quét vào cổng.
4. **Kiểm toán (Audit Log):**
   - Lưu lịch sử vào bảng `ticket_transfers`.
   - Ghi bản ghi vào `order_audit_logs` với action `TICKET_TRANSFERRED` (Story S-49).

---

## 3. Danh sách API

### 3.1 Chuyển nhượng vé
- **Endpoint:** `POST /api/v1/tickets/{ticketId}/transfer`
- **Yêu cầu:** Đăng nhập tài khoản sở hữu vé (`[RequireRole]`).
- **Request Body:**
  ```json
  {
    "recipientEmail": "nguoinhan@example.com",
    "note": "Tặng vé xem kịch nhé bạn!"
  }
  ```
- **Response HTTP 200:**
  ```json
  {
    "success": true,
    "message": "Chuyển nhượng vé thành công tới tài khoản nguoinhan@example.com.",
    "data": {
      "transferId": "...",
      "ticketId": "...",
      "oldTicketCode": "TK-XXXX-YYYY-ZZZZ-WWWW",
      "newTicketCode": "TK-AAAA-BBBB-CCCC-DDDD",
      "fromEmail": "sender@example.com",
      "toEmail": "nguoinhan@example.com",
      "recipientName": "Trần Tahị Người Nhận",
      "transferredAt": "2026-10-17T20:50:00Z"
    }
  }
  ```

### 3.2 Lấy danh sách vé cá nhân
- **Endpoint:** `GET /api/v1/tickets/my-tickets`
- **Yêu cầu:** Đăng nhập tài khoản (`[RequireRole]`).
- **Response HTTP 200:** Trả về danh sách vé bao gồm vé mua ban đầu và vé được nhận chuyển nhượng.

### 3.3 Xem lịch sử chuyển nhượng của vé
- **Endpoint:** `GET /api/v1/tickets/{ticketId}/transfers`
- **Yêu cầu:** Admin hoặc người tham gia giao dịch (người gửi/người nhận).

---

## 4. Hướng dẫn kiểm thử thủ công

1. **Chuẩn bị 2 tài khoản:** Tài khoản A (`sender@example.com`) và tài khoản B (`recipient@example.com`).
2. **Tài khoản A:**
   - Mua vé thành công một suất diễn còn trên 2 tiếng trước giờ bắt đầu.
   - Truy cập trang `my-tickets.html` (Vé của tôi).
   - Tại vé hợp lệ, bấm nút **Chuyển vé**.
   - Nhập email của B: `recipient@example.com` và bấm **Xác nhận chuyển**.
   - Kiểm tra vé của A chuyển sang nhãn **Đã chuyển**, không còn nút "Mở mã QR", hiển thị *"Chuyển cho: recipient@example.com"*.
3. **Tài khoản B:**
   - Đăng nhập tài khoản B, vào trang `my-tickets.html`.
   - Kiểm tra thấy vé vừa được chuyển nhượng, có nhãn *"Nhận từ: sender@example.com"*, mở mã QR mới bình thường.
4. **Kiểm tra soát vé (Staff Scanner):**
   - Dùng ảnh QR cũ của A quét tại trang `staff-scanner.html`: Hệ thống từ chối với lý do `TICKET_TRANSFERRED`.
   - Quét mã QR mới của B: Hệ thống check-in thành công.

---

## 5. Kiểm thử tự động (Automated Tests)

```powershell
# Chạy toàn bộ 12 test cases của Story S-57
dotnet test src/tests/EventTicketBooking.Tests/EventTicketBooking.Tests.csproj --filter "FullyQualifiedName~TicketTransferS57Tests"

# Chạy kiểm thử giao diện frontend
node --test src/tests/frontend-audit.test.cjs
```
