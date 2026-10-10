# Đồng bộ migration giữa các máy

Chạy từ thư mục gốc repository, với .env của máy cần kiểm tra:

```powershell
dotnet run --project tools/MigrationAudit
```

Công cụ chỉ đọc lịch sử migration và metadata cột, không gọi seeder hoặc áp dụng migration. Nó so sánh các cột được model ánh xạ về sự tồn tại, kiểu dữ liệu và nullability. Mã thoát 1 nghĩa là cần xem xét migration pending, lịch sử ngoài source hoặc khác biệt schema/snapshot; không có nghĩa công cụ chạy bị hỏng. Không gửi .env hoặc thông tin kết nối cho người khác; chia sẻ kết quả audit.

## Kết quả audit ngày 6 tháng 10 năm 2026

- Trước bản sửa: source có 18 migration, database có 19 bản ghi; không pending.
- Database có lịch sử `20261001060648_AddEventImageUrl` không có trong source hoặc lịch sử Git hiện có. Chưa xác định nội dung; giữ nguyên bản ghi, không tạo lại một migration giả cùng ID. Cần lấy file gốc từ máy hoặc nhánh đã tạo nó.
- `Events.ImageUrl`: database `character varying(1000)`, model `text`.
- `orders.Status`: database `order_status`, model `text`. Dữ liệu có cả `Paid` và `paid`. Index đơn chờ dùng nhãn enum `pending`, model dùng chuỗi `Pending`.
- Thời gian hold hiện tại là `timestamp with time zone`; không thay đổi timezone.

Migration mới `20261006120000_AlignLegacyOrderStatusAndImageUrl` chuẩn hóa trạng thái về tên enum C#, chuyển cột sang text, dựng lại unique index đơn chờ và mở rộng ImageUrl sang text. Không sửa migration cũ hoặc snapshot. Migration dừng nếu có trạng thái không biết hoặc đơn Pending trùng sau chuẩn hóa. Các câu lệnh chạy trong transaction mặc định của EF; không dùng suppressTransaction. Down chủ động từ chối vì không thể khôi phục chính xác nhãn enum/giới hạn cột của từng database cũ; cần bản sao lưu và kế hoạch khôi phục đã duyệt.

## Kiểm tra trước khi áp dụng

1. Cả nhóm dùng cùng phiên bản source. Chạy audit trên từng máy trước khi khởi động API, vì Development seeder sẽ tự gọi migration.
2. Tạo SQL để review, chưa thực thi:

   ```powershell
   dotnet run --project tools/MigrationAudit -- --script
   ```

3. Kiểm thử migration trên một database mới dành riêng cho thử nghiệm và một bản sao database cũ. Không dùng database đang làm việc. Kiểm tra số bản ghi, nội dung URL, trạng thái đơn và unique index trước/sau. Các bước này chưa được thực hiện trên database tại thời điểm viết hướng dẫn.
4. Nếu audit phát hiện timestamp without time zone, dừng và xác định ý nghĩa giờ của dữ liệu; migration mới này không xử lý timezone.
5. Chỉ sau khi kiểm thử và được phép áp dụng lên database đích, dùng:

   ```powershell
   dotnet ef database update --project src/EventTicketBooking.Api
   ```

6. Chạy audit lại, kiểm thử tạo đơn/expiry/RemainingSeats. Lịch sử ngoài source vẫn cần điều tra dù các cột đã khớp. Không xóa lịch sử để làm audit đạt.

Không reset database, xóa volume, sửa migration đã chia sẻ hoặc tự suy diễn timezone. Audit cột đạt chưa chứng minh toàn bộ index, constraint và dữ liệu đồng bộ.

## Kết quả kiểm thử PostgreSQL riêng

Chạy `dotnet run --project tools/MigrationValidation` từ thư mục gốc. Công cụ cần quyền CREATEDB, tạo database thử với tên ngẫu nhiên và giữ lại chúng, không drop database. Không dùng công cụ này trên tài khoản production. Không gọi entry point API hoặc seeder trên database đang dùng.

Ngày 6 tháng 10 năm 2026, hai kịch bản đã đạt:

- Database trống: toàn bộ migration chạy thành công, chạy lại thành công, hai cột mục tiêu là text, không pending.
- Database cũ mô phỏng: áp dụng đến migration trước bản sửa, tạo fixture enum order_status và ImageUrl varchar(1000), rồi áp dụng bản sửa. Năm trạng thái đơn, số tiền, URL và bản ghi lịch sử ngoài source được giữ nguyên; unique index chặn đơn Pending trùng; chạy lại thành công.

Database thử đạt được giữ lại: `eventpulse_validation_fresh_441ea63dd52249259eb3c85a3bd00a04` và `eventpulse_validation_legacy_441ea63dd52249259eb3c85a3bd00a04`.

Lần dựng fixture đầu tiên dừng vì chưa tạo kiểu enum legacy; lỗi thiết lập bài thử đã được sửa. Hai database của lần đó cũng được giữ lại, không xóa.

Kịch bản legacy là mô phỏng các khác biệt đã audit, không phải bản sao đầy đủ dữ liệu database đang dùng. Chưa kiểm thử các trường hợp trạng thái không biết/đơn trùng trước migration, không chứng minh mọi schema legacy của thành viên khác tương thích. Database đang dùng chưa được áp dụng migration mới.

## Kiểm thử bản sao đầy đủ database đang dùng

Đã dùng pg_dump/pg_restore PostgreSQL 16.15 để sao lưu database gốc và khôi phục sang `eventpulse_copytest_41ac6cb5174d41b1bbc3f8c6d0fa3043`. Archive nằm tại `artifacts/database-backups/event_ticket_before_migration.dump` và được loại khỏi Git cùng thư mục artifacts. Bộ công cụ tải từ EDB nằm trong .tmp, cũng bị loại khỏi Git.

Migration mới chạy thành công trên bản sao. SHA-256 nội dung của cả 12 bảng trước/sau khớp, sau khi chuẩn hóa cách biểu diễn Status của orders để so sánh. EF đọc được 12 đơn sau migration. Hai cột mục tiêu là text, index unique Pending hợp lệ, lịch sử cũ được giữ và chỉ thêm một migration. Chạy migration lại thành công. Database gốc chưa được cập nhật; bản sao và archive được giữ lại.

Công cụ `tools/MigrationCopyCheck` chỉ nhận tên database bắt đầu bằng `eventpulse_copytest_`, từ chối database gốc và dừng trước áp dụng nếu không đúng một migration pending dự kiến. Bài thử này kiểm chứng migration và dữ liệu, chưa thay thế kiểm thử luồng S-23 trên trình duyệt.

## Áp dụng lên database đang dùng sau khi được cho phép

Đã tạo backup mới `artifacts/database-backups/event_ticket_immediately_before_apply.dump`, sau đó áp dụng đúng migration `20261006120000_AlignLegacyOrderStatusAndImageUrl` qua startup Development. Backend chạy tại cổng 5012. Audit sau áp dụng: 19 migration trong source, 20 lịch sử đã áp dụng, không pending; cả 75 cột mapped khớp kiểu/nullability và snapshot khớp model. Migration lịch sử ngoài source vẫn được giữ nguyên để điều tra. HTTP trang chủ trả 200 và health API trả Healthy. Việc áp dụng thành công không thay thế xác nhận countdown trên trình duyệt.
