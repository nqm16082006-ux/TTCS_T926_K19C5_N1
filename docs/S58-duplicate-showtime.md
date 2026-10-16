# S-58 Nhân bản suất diễn kèm sơ đồ và bảng giá

Chức năng nằm ở trang quản lý chi tiết sự kiện, trong danh sách suất diễn. Nhân bản tạo một suất mới trong cùng sự kiện, không tạo sự kiện khác.

## Cách kiểm thử

1. Đăng nhập Organizer sở hữu sự kiện hoặc Admin.
2. Mở quản lý sự kiện, vào Chi tiết và danh sách suất diễn.
3. Tại suất nguồn, bấm **Nhân bản suất diễn**.
4. Chọn thời gian bắt đầu và kết thúc mới (mặc định gợi ý ngày kế tiếp), bấm **Tạo bản sao**.
5. Kiểm tra suất mới xuất hiện trong cùng sự kiện ở trạng thái Nháp.
6. Kiểm tra hàng và số ghế, tên hạng, giá, ảnh sơ đồ, giới hạn vé giống nguồn; tổng ghế bằng số ghế thực tế của nguồn.
7. Nếu nguồn có ghế đang giữ, đã bán hoặc đã soát, ghế tương ứng ở bản sao phải trống. Không sao chép đơn hàng, thanh toán, vé, giữ chỗ hoặc lượt soát vé.
8. Suất nguồn vẫn giữ trạng thái, lịch và dữ liệu đặt vé cũ.
9. Lịch kết thúc trước/bằng bắt đầu, lịch thiếu hoặc trùng hoàn toàn lịch nguồn phải bị từ chối. Organizer khác không được nhân bản; Admin được phép.

## API

`POST /api/events/{eventId}/showtimes/{showtimeId}/duplicate`

```json
{
  "startTime": "2026-10-16T12:00:00Z",
  "endTime": "2026-10-16T14:00:00Z"
}
```

Trả HTTP 201 và `ApiResponse<ShowtimeResponseDto>`. Dữ liệu lịch lưu UTC. Tạo suất, hạng và ghế trong một lần SaveChanges để lưu atomically trên database quan hệ.

API cũ `POST /api/events/{id}/duplicate` và nút nhân bản sự kiện được gỡ vì không đúng yêu cầu S-58. Không cần migration mới; chức năng dùng các bảng hiện có. Không xóa hoặc sửa các sự kiện đã được nhân bản trước đây.

## Kiểm thử tự động

```powershell
dotnet test src/tests/EventTicketBooking.Tests/EventTicketBooking.Tests.csproj --filter 'FullyQualifiedName~DuplicateShowtimeS58Tests|FullyQualifiedName~AuditHttpFlowTests'
node --test src/tests/showtime-duplicate.test.cjs
```
