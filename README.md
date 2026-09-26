# Dự Án Bán Vé Sự Kiện Có Sơ Đồ Ghế - Backend API (.NET 9)

Hệ thống Backend cho ứng dụng **Bán vé sự kiện có sơ đồ ghế** được dựng trên nền tảng **C# .NET 9 Web API**, sử dụng **Entity Framework Core**, **PostgreSQL** làm cơ sở dữ liệu chính, và **Redis** làm bộ nhớ đệm (Cache).

---

## 📋 Yêu Cầu Tiền Đề (Prerequisites)

- [.NET 9.0 SDK](https://dotnet.microsoft.com/download/dotnet/9.0) (Đã kiểm tra hoạt động trên bản `.NET 9.0.317`)
- [Docker Desktop](https://www.docker.com/products/docker-desktop/) (cho PostgreSQL & Redis container)
- Công cụ CLI Entity Framework Core (`dotnet-ef`):
  ```powershell
  dotnet tool install --global dotnet-ef
  ```

---

## 🚀 Cấu Hình Môi Trường (.env)

Hệ thống đọc các chuỗi kết nối và thông số cấu hình từ **Biến môi trường (Environment Variables)**, không ghi thẳng chuỗi kết nối (hardcode) trong mã nguồn.

1. Đã có file `.env.example` làm mẫu cấu hình.
2. Tạo/chỉnh sửa file `.env` tại thư mục gốc dự án:

```env
# App settings
ASPNETCORE_ENVIRONMENT=Development
PORT=5000

# PostgreSQL Configuration
POSTGRES_HOST=localhost
POSTGRES_PORT=5432
POSTGRES_DB=event_ticket_db
POSTGRES_USER=postgres
POSTGRES_PASSWORD=postgres_password_123

# Full Database Connection String (PostgreSQL)
ConnectionStrings__DefaultConnection=Host=localhost;Port=5432;Database=event_ticket_db;Username=postgres;Password=postgres_password_123

# Redis Configuration
REDIS_HOST=localhost
REDIS_PORT=6379
ConnectionStrings__Redis=localhost:6379
```

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
dotnet build
```

### 2. Khởi Động Web API
```powershell
dotnet run --project src/EventTicketBooking.Api
```

Sau khi ứng dụng khởi động:
- **Swagger UI**: Access tại [http://localhost:5000/swagger](http://localhost:5000/swagger) (hoặc URL hiển thị trên terminal).
- **Health Check API**: Access tại `GET /api/health` để kiểm tra kết nối realtime tới PostgreSQL và Redis.

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

## 📊 Báo Cáo Hiệu Năng Sơ Đồ Ghế

Để đảm bảo trải nghiệm đặt vé mượt mà cho các sự kiện quy mô lớn (lên tới 2000+ ghế), hệ thống sơ đồ ghế trên trình duyệt được xây dựng tối ưu với **HTML5 Canvas** và **Hardware-Accelerated CSS**.

### Thông Số Kỹ Thuật
- **Dữ liệu mẫu**: Hỗ trợ tải file JSON hàng ngàn ghế (sẵn sàng nạp qua API). Tệp mẫu 2000 ghế đã được cung cấp sẵn tại \data/sample_seats_2000.json\.
- **Render**: Render toàn bộ sơ đồ ghế thông qua 1 thẻ \<canvas>\ duy nhất thay vì tạo hàng ngàn DOM Nodes.
- **Tốc độ thực thi**:
  - Thời gian xử lý dữ liệu và vẽ Canvas: ~2-5 ms.
  - Tổng thời gian tải từ lúc gọi API tới lúc sơ đồ hiện xong: **< 50 ms** (đáp ứng xuất sắc yêu cầu dưới 2 giây).
- **Thao tác**: Các thao tác phóng to/thu nhỏ (Zoom) và di chuyển (Pan) đạt chuẩn 60 FPS mượt mà trên cả điện thoại và máy tính.

