# S-36: Xung đột soát vé ngoại tuyến ở nhiều cửa

## Quy tắc

- Các lượt ngoại tuyến khác cửa của cùng vé được so sánh theo `ScannedAtDevice` (UTC), không theo thứ tự máy chủ nhận dữ liệu.
- Lượt sớm nhất là `SYNCED`; các lượt còn lại là `CONFLICT`. Nếu bằng giờ, dùng `OfflineScanId` theo thứ tự ordinal.
- Khi lượt sớm hơn đến sau, cập nhật `OrderItem.CheckInGate/CheckInTime`, chuyển lượt hợp lệ cũ thành xung đột và cập nhật snapshot của tất cả lượt xung đột.
- Giữ timestamp thiết bị gốc (chuẩn hóa UTC) để đối chiếu. Quy tắc S-35 giới hạn giờ ghi nhận trong tương lai quá 5 phút vẫn áp dụng cho `OrderItem.CheckInTime`.
- Quét lại cùng cửa vẫn bị từ chối theo S-35. Lượt trực tuyến không có bản ghi ngoại tuyến hợp lệ vẫn giữ cách xử lý cũ.
- Kết quả phản hồi là trạng thái tại thời điểm đồng bộ; có thể thay đổi khi máy khác đồng bộ. Các kết quả cùng batch được đối chiếu lại trước khi trả về.
- Không cần migration mới; dùng bảng `OfflineCheckInRecords` của S-35. Dữ liệu cũ không tự động được xử lý lại; được đối chiếu khi có lượt ngoại tuyến mới khác cửa.

## Báo cáo

`GET /api/events/showtimes/{showtimeId}/checkin-conflicts?page=1&pageSize=20`

Organizer chỉ xem suất thuộc sự kiện của mình; Admin được xem tất cả. Yêu cầu đăng nhập; `page >= 1`, `1 <= pageSize <= 100`.

Phản hồi `ApiResponse` có `totalTickets` (vé có xung đột), `totalConflicts` (lượt xung đột), và `items`: `ticketCode`, `validGate`, `validTime`, `conflictGate`, `conflictTime`. Phân trang theo lượt xung đột, mỗi lượt là một dòng đối chiếu với lượt hợp lệ hiện tại.

Trong `overview.html`, mở **Hiệu suất theo suất diễn**, chọn **Xung đột soát vé — Xem chi tiết**. Giờ hiển thị ở múi giờ Việt Nam, có giây. Có cập nhật, trang trước/sau và trạng thái không có dữ liệu hoặc lỗi tải.

## Demo nghiệm thu

1. Chuẩn bị một suất diễn và vé đã thanh toán có QR hợp lệ.
2. Mở hai phiên trình duyệt độc lập (ví dụ thường và ẩn danh), đăng nhập Staff. Hai tab cùng phiên dùng chung localStorage nên không đại diện hai máy độc lập.
3. Mỗi phiên chọn cùng suất và tải danh sách vé khi có mạng. Đặt cửa A và cửa B.
4. Bật ngoại tuyến ở cả hai phiên. Quét cùng QR ở A trước, chờ vài giây rồi quét ở B.
5. Tắt ngoại tuyến ở B trước, đồng bộ hàng đợi. Sau đó tắt ngoại tuyến và đồng bộ A.
6. Kiểm tra máy chủ: cửa hợp lệ A, giờ vào theo lượt A; lượt B là xung đột. Có đúng một lượt `SYNCED`.
7. Đăng nhập Organizer sở hữu sự kiện, mở báo cáo: thấy một vé xung đột, cửa A/giờ A và cửa B/giờ B.
8. Dùng vé khác lặp lại với A đồng bộ trước; kết quả vẫn chọn A.
9. Dùng vé khác thử thêm cửa C quét sớm nhất nhưng đồng bộ cuối: báo cáo có hai lượt xung đột, cùng đối chiếu với C.
10. Gửi lại cùng `OfflineScanId`: không thêm bản ghi hoặc tăng số xung đột. Organizer khác không xem được báo cáo; Admin xem được.

## Kiểm thử tự động

```powershell
dotnet test src/tests/EventTicketBooking.Tests/EventTicketBooking.Tests.csproj --no-restore --filter 'FullyQualifiedName~OfflineCheckInSyncS35Tests|FullyQualifiedName~TicketReadmissionS31Tests|FullyQualifiedName~OfflineTicketCheckInS34Tests|FullyQualifiedName~EventControllerSaleTests'
node --test src/tests/scanner-offline.test.cjs src/tests/s36-conflicts.test.cjs
```

Bộ test S-36 nằm trong `OfflineCheckInSyncS35Tests.cs` để tái sử dụng dữ liệu và hạ tầng S-35; bao gồm InMemory và SQLite. Khóa khi hòa giải dùng cập nhật không đổi trên dòng vé trong transaction, để các transaction cùng vé chờ nhau. Cần kiểm tra thêm trên PostgreSQL thực tế trước khi triển khai lên môi trường dùng chung.
