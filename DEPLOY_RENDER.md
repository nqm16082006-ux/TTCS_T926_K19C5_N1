# Đưa EventPulse lên Render

Ứng dụng gồm API .NET 9 và giao diện HTML phục vụ chung một Web Service Docker.
`render.yaml` là cấu hình Production có PostgreSQL và disk lưu ảnh. **Các tài nguyên trong cấu hình này có phí**: xem tổng chi phí trên Render trước khi tạo. Chưa có dịch vụ nào được triển khai bằng việc thêm các file này.

## Triển khai Production

1. Commit đầy đủ source, migration, JavaScript/CSS và `render.yaml` lên nhánh GitHub bạn chọn. Không đưa `.env`, bản patch audit hoặc dữ liệu người dùng lên GitHub. File chưa commit không được Render sử dụng.
2. Đăng nhập https://dashboard.render.com, chọn **New → Blueprint**, kết nối repository và chọn đúng nhánh. Render đọc `render.yaml` ở thư mục gốc.
3. Nhập các giá trị Render yêu cầu: email/mật khẩu admin ban đầu (ít nhất 12 ký tự), Client ID/API Key/Checksum Key PayOS, tài khoản SMTP/App Password và Sender Email. Điền trực tiếp trên Render, không commit secret. JWT được Render tạo ngẫu nhiên.
4. Kiểm tra tài nguyên và chi phí trước khi bấm tạo. Cấu hình tắt tự động deploy; chủ động deploy commit cần demo.
5. Kiểm tra log build/startup và `/api/health` trả HTTP 200. Truy cập `/` hoặc `/public-events.html`; đăng nhập `/login.html` bằng admin đã cấu hình. Admin có thể cấp vai trò Organizer cho tài khoản đã đăng ký/xác thực.
6. Trong PayOS, đặt webhook HTTPS: `https://<tên-dịch-vụ>.onrender.com/api/v1/payments/webhook`. Return/Cancel URL tự lấy từ `RENDER_EXTERNAL_URL`. Nếu dùng domain riêng, thêm `App__PublicBaseUrl=https://domain-cua-ban`.
7. Nếu dùng Google Login, thêm origin HTTPS mới vào cấu hình OAuth trên Google Cloud; Client ID frontend và backend phải cùng một ứng dụng OAuth.
8. Thử tạo sự kiện, thêm suất, import `data/seat-map-3000.json`, cấu hình giá, mở bán, chọn/giữ ghế, đặt vé, thanh toán và check-in. Sau một lần redeploy, kiểm tra ảnh và dữ liệu vẫn còn.

Migrations chạy khi startup vì `Database__AutoMigrate=true`; lỗi migration sẽ dừng startup. Database mới chỉ có vai trò và admin ban đầu, chưa có sự kiện/dữ liệu ở máy bạn. Nếu cần chuyển dữ liệu cũ, thực hiện backup/restore PostgreSQL riêng và chuyển thư mục ảnh tương ứng, không import lại tùy tiện vào database đã có dữ liệu.

Disk `/var/data` lưu ảnh ở `/var/data/uploads`; các file trả về bằng URL `/uploads/events/...`. Redis không bắt buộc trong cấu hình một instance này: cache dùng bộ nhớ, còn ghế/đơn và giao dịch giữ ghế lưu trong PostgreSQL. Không tăng số instance hoặc chạy nhiều bản startup migration đồng thời mà chưa kiểm tra lại.

## Nếu chỉ cần demo miễn phí

Không dùng nguyên cấu hình Production trên để mong không phát sinh phí. Render Free không hỗ trợ persistent disk, chặn SMTP 25/465/587; PostgreSQL miễn phí hết hạn sau 30 ngày, và Web Service ngủ khi không hoạt động. Do đó cần một cấu hình demo riêng với thanh toán giả lập, cách xác thực email phù hợp và tài khoản demo có mật khẩu riêng. Không đặt Production sang Development một cách tùy tiện vì tài khoản seed mặc định là công khai.

Nguồn: https://render.com/docs/blueprint-spec, https://render.com/docs/free, https://render.com/docs/disks.

## Chạy khi clone về máy mới

Yêu cầu .NET SDK 9, Docker Desktop (hoặc PostgreSQL 16 và Redis 7 cài riêng). Từ thư mục repository:

```powershell
Copy-Item .env.example .env
docker compose up -d
dotnet restore EventTicketBooking.sln
dotnet run --project src/EventTicketBooking.Api
```

Chỉnh `.env` nếu thông tin PostgreSQL khác. Mở http://localhost:5012/public-events.html. Development tự migrate/seed trên database mới; tài khoản demo `organizer@eventticket.com` / `Organizer@123456` và `admin@eventticket.com` / `Admin@123456`. Email không gửi thật nếu SMTP chưa cấu hình; thanh toán mặc định là Mock. Không dùng tài khoản/mật khẩu này trên website công khai.

Kiểm tra backend/frontend:

```powershell
dotnet test EventTicketBooking.sln -c Release
node --test src/tests/frontend-audit.test.cjs
```

Kiểm tra migrations từ PostgreSQL trống (kết nối phải có quyền CREATEDB; công cụ chỉ tạo rồi xóa database tạm của chính nó):

```powershell
$env:DEPLOYMENT_TEST_PG = 'Host=localhost;Database=postgres;Username=postgres;Password=<mat-khau-local>'
dotnet run --project tools/DeploymentSmoke -c Release
```
