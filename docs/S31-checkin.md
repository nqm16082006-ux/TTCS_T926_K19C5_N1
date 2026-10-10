# S31: chống soát vé hai lần

Base: origin/main a30e9b3 (S30).

- POST /api/v1/checkin/scan giữ định dạng S30. Quét trùng trả HTTP 400 với Reason=ALREADY_CHECKED_IN, PreviousGate, PreviousCheckInTime và OrderItemId. Thời gian trong thông báo dùng Asia/Ho_Chi_Minh.
- UPDATE có điều kiện !IsCheckedIn là lớp bảo đảm cuối cùng trong PostgreSQL. Khi mất lượt cập nhật, đọc lại lần vào thành công để trả cùng thông tin cửa/giờ.
- Cho vào bổ sung dùng cùng API với AllowReadmission=true, ReadmissionReason (1–1000 ký tự), RequestId (UUID cho một hành động). Cần vé đã vào, đơn Paid, đúng suất, nhân viên/Admin hợp lệ. Actor và tên nhân viên lấy từ danh tính đăng nhập và database.
- Bảng TicketReadmissions lưu từng lần bổ sung: vé, cửa, thời gian, lý do, mã/tên nhân viên. Không ghi đè lần vào đầu tiên. Khóa chính RequestId ngăn gửi trùng; yêu cầu lặp trả 409.
- Giao diện staff-scanner hiển thị ghi chú và nút xác nhận chủ vé khi bị từ chối vì đã vào. Camera tạm dừng để nhân viên nhập lý do; nút quét tiếp khôi phục camera.

## Kiểm thử

`dotnet test src/tests/EventTicketBooking.Tests --filter FullyQualifiedName~TicketReadmissionS31Tests`

Đặt S31_POSTGRES_CONNECTION để chạy thêm bài kiểm thử PostgreSQL. Test tạo database tên s31_test_<GUID> riêng, giữ database này để kiểm tra và không xóa database. Hai scanner được đồng bộ ngay trước câu UPDATE để kiểm chứng tranh chấp thật; hai yêu cầu vào bổ sung cùng RequestId chỉ tạo một bản ghi.

Kiểm thử hồi quy: TicketCheckInControllerTests, StaffScannerS28Tests, TicketQrSignatureTests.

## Demo

1. Quét vé Paid đúng suất tại cửa A: thành công.
2. Quét lại tại cửa B: báo cửa A và giờ đã vào.
3. Xác nhận chủ vé, nhập lý do rồi bấm Cho vào có ghi chú: tạo một lần vào bổ sung ghi tên nhân viên.
4. Quét bình thường lần nữa: vẫn bị từ chối, thông tin lần vào đầu tiên không đổi.
