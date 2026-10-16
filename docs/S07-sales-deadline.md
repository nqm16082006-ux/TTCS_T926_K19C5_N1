# S-07 — Hạn bán vé theo suất diễn

- BTC đặt hạn bán riêng cho từng suất; bỏ trống dùng giờ bắt đầu diễn. Hạn không vượt giờ kết thúc suất.
- Suất cũ được bổ sung hạn bằng giờ bắt đầu. Không sửa lịch diễn hoặc vé đã bán.
- BTC có thể đóng bán sớm. Đến hạn (bao gồm đúng thời điểm hạn), backend chặn giữ ghế và tạo đơn mới dù job chưa chạy.
- Job mỗi 15 giây chuyển suất OnSale đã hết hạn sang Closed. Danh sách công khai chỉ lấy các suất còn thời gian bán, chi tiết trực tiếp trả trạng thái Closed khi hết hạn.
- Đơn tạo trước hạn vẫn được thanh toán khi suất đã đóng bán. Hạn đơn = min(thời điểm tạo + 15 phút, giờ kết thúc suất). Không tạo thanh toán mới khi suất đã kết thúc; webhook của giao dịch đã thực hiện vẫn theo quy tắc idempotency hiện có.
- Hạn bán hiển thị theo múi giờ thiết bị (giao diện hiện tại); API và database lưu UTC.
- Nhân bản suất mặc định hạn bán bằng giờ bắt đầu suất mới, BTC có thể nhập hạn riêng.

## Kiểm thử nghiệm thu

1. Tạo suất ngày mai, đặt hạn bán sau hiện tại vài phút và mở bán.
2. Giữ ghế, tạo một đơn trước hạn; giữ một ghế khác nhưng chưa tạo đơn.
3. Đến hạn: giữ ghế mới và tạo đơn từ ghế đã giữ đều bị từ chối; không còn suất trong danh sách công khai.
4. Đơn đã tạo vẫn được thanh toán trong hạn của đơn. Trạng thái quản trị tự chuyển Closed trong tối đa khoảng 15 giây.
5. Suất khác: đóng bán thủ công trước hạn, xác nhận không giữ ghế/tạo đơn mới được.
6. Nhập hạn sau giờ kết thúc: API từ chối; suất và sơ đồ hiện có không bị đổi.
7. Suất đang diễn, BTC cho phép bán đến sát giờ kết thúc: hạn thanh toán của đơn mới không vượt giờ kết thúc suất.
