# Dự Án Bán Vé Sự Kiện Có Sơ Đồ Ghế - Backend API (.NET 9)

Hệ thống Backend cho ứng dụng **Bán vé sự kiện có sơ đồ ghế** được dựng trên nền tảng **C# .NET 9 Web API**, sử dụng **Entity Framework Core**, **PostgreSQL** làm cơ sở dữ liệu chính, và **Redis** làm bộ nhớ đệm (Cache).

Hướng dẫn clone/chạy trên máy mới và triển khai Render: [DEPLOY_RENDER.md](DEPLOY_RENDER.md). Cấu hình Render hiện tại dùng tài nguyên có phí; xem chi phí trước khi tạo dịch vụ.

---

## 📋 Yêu Cầu Tiền Đề (Prerequisites)

- [.NET 9.0 SDK](https://dotnet.microsoft.com/download/dotnet/9.0) (Đã kiểm tra hoạt động trên bản `.NET 9.0.317`)
- [Docker Desktop](https://www.docker.com/products/docker-desktop/) (cho PostgreSQL & Redis container)
- Công cụ CLI Entity Framework Core (`dotnet-ef`):
  ```powershell
  dotnet tool install --global dotnet-ef --version 9.0.2
  ```

---

## 🚀 Cấu Hình Môi Trường (.env)

Hệ thống đọc các chuỗi kết nối và thông số cấu hình từ **Biến môi trường (Environment Variables)**, không ghi thẳng chuỗi kết nối (hardcode) trong mã nguồn.

1. File `.env.example` là mẫu an toàn; không dùng mật khẩu mẫu trong production.
2. Tạo file `.env` riêng bằng script sinh mật khẩu PostgreSQL và JWT ngẫu nhiên. Chạy từ thư mục gốc repository:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\setup-local.ps1
```

Lệnh trên chỉ nới execution policy trong tiến trình PowerShell dùng để chạy script, không thay đổi chính sách toàn máy. Script giữ nguyên `.env` nếu file đã tồn tại. Khi đổi mật khẩu PostgreSQL, cập nhật đồng bộ cấu hình của ứng dụng và PostgreSQL.

---

## 🐳 Khởi Động PostgreSQL & Redis bằng Docker Compose

Chạy lệnh sau để khởi chạy PostgreSQL 16 và Redis 7 container cho cả team dùng chung cấu hình:

```powershell
docker compose up -d
```

Để kiểm tra trạng thái các container:
```powershell
docker compose ps
```

Dừng các container khi không sử dụng:
```powershell
docker compose down
```

---

## 🔄 Quản Lý Migration Database (EF Core)

Dự án sử dụng Entity Framework Core Migrations hỗ trợ thực thi sạch cả **Tiến (Up)** lẫn **Lùi (Down / Rollback)**.

### 1. Chạy Migration Tiến (Áp dụng Schema vào Database)
```powershell
dotnet ef database update --project src/EventTicketBooking.Api
```

### 2. Chạy Migration Lùi (Rollback toàn bộ về vị trí ban đầu)
```powershell
dotnet ef database update 0 --project src/EventTicketBooking.Api
```

### 3. Tạo thêm Migration mới khi thay đổi Entity / Model
```powershell
dotnet ef migrations add <TenMigrationMoi> --project src/EventTicketBooking.Api
```

---

## 💻 Chạy Dự Án Local

### 1. Build Dự Án
```powershell
dotnet build .\EventTicketBooking.sln
```

### 2. Chạy kiểm thử

Dự án này là .NET, không có `package.json` và không dùng npm. Nếu PowerShell đang ở `D:\NGUYEN VAN ĐOÁN>` như ví dụ, thư mục dự án đã có sẵn; không cần clone lại hoặc chạy `npm install`. Vào thư mục chứa solution và xác nhận đúng vị trí:

```powershell
Set-Location ".\TTCS_T926_K19C5_N1-main\TTCS_T926_K19C5_N1-main"
Test-Path .\EventTicketBooking.sln
```

Kết quả `Test-Path` phải là `True`. Chạy kiểm thử .NET bằng:

```powershell
dotnet restore .\EventTicketBooking.sln
dotnet test .\EventTicketBooking.sln
```

Các kiểm tra giao diện viết bằng Node.js chạy riêng; đây không phải lệnh cài dependency npm:

```powershell
node --test .\src\tests\frontend-audit.test.cjs
node .\src\tests\hold-countdown-check.cjs
```

Nếu khôi phục NuGet đã thành công trước đó, có thể bỏ qua `dotnet restore` và dùng `dotnet test .\EventTicketBooking.sln --no-restore`. Nếu lệnh `node` không được nhận diện, hãy cài Node.js rồi mở lại PowerShell. Nếu .NET báo thiếu runtime, cài runtime tương ứng với target framework `net9.0` của dự án.

### 3. Khởi Động Web API
```powershell
dotnet run --project .\src\EventTicketBooking.Api\EventTicketBooking.Api.csproj
```

Sau khi ứng dụng khởi động:
- **Swagger UI**: Access tại [http://localhost:5012/swagger](http://localhost:5012/swagger) (hoặc URL hiển thị trên terminal).
- **Health Check API**: Access tại `GET /api/health` để kiểm tra kết nối realtime tới PostgreSQL và Redis.

### 4. Báo cáo vé bán theo suất diễn

Sau khi đăng nhập bằng tài khoản Organizer hoặc Admin, mở `/overview.html` để xem vé đã thanh toán, ghế đang được giữ còn hạn, ghế còn khả dụng và doanh thu thực thu theo từng hạng vé. Dashboard tự tải lại báo cáo sau mỗi 60 giây; có thể cập nhật thủ công bất cứ lúc nào.

API `GET /api/events/sales-by-showtime` dùng cùng quyền truy cập: Organizer chỉ xem sự kiện của mình, Admin xem toàn hệ thống. Doanh thu tính theo giá đã lưu trên từng vé đã thanh toán, không tính đơn đang chờ thanh toán.

---

## 🔍 Kiểm Tra Health Check API (`/api/health`)

Endpoint `GET /api/health` sẽ trả về trạng thái kết nối PostgreSQL và Redis dạng JSON:

```json
{
  "status": "Healthy",
  "timestamp": "2026-09-22T09:30:00Z",
  "database": {
    "status": "Connected",
    "error": null
  },
  "redis": {
    "status": "Connected",
    "error": null
  },
  "environment": "Development"
}
```

---

## 📂 Cấu Trúc Thư Mục Dự Án

```
TTCS_T926_K19C5_N1/
├── .env                              # File biến môi trường local (giữ kín, không commit)
├── .env.example                      # File biến môi trường mẫu
├── .gitignore                        # File quy định các thư mục/file cần ignore khi commit
├── docker-compose.yml                # Cấu hình container PostgreSQL & Redis cho cả team
├── README.md                         # Hướng dẫn khởi chạy dự án
├── EventTicketBooking.sln            # Solution file
└── src/
    └── EventTicketBooking.Api/
        ├── EventTicketBooking.Api.csproj
        ├── Program.cs                # Entry point, nạp .env & đăng ký DI services
        ├── appsettings.json          # Cấu hình ứng dụng
        ├── Controllers/
        │   └── HealthController.cs   # Controller kiểm tra kết nối DB & Redis
        ├── Data/
        │   └── AppDbContext.cs       # EF Core DbContext
        ├── Migrations/               # Thư mục lưu các file Migration (Up & Down)
        └── Models/
            └── Event.cs              # Entity mẫu Sự kiện
```

---

## Sơ đồ ghế hiện tại

Giao diện sử dụng DOM/CSS, có lọc khu vực, màu theo hạng vé và phóng to/thu nhỏ. File mẫu 3.000 ghế: data/seat-map-3000.json. Các số đo hiệu năng cần kiểm tra trên môi trường triển khai thực tế.
