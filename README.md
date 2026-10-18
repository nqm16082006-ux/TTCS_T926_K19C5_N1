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

1. Đã có file `.env.example` làm mẫu cấu hình.
2. Tạo/chỉnh sửa file `.env` tại thư mục gốc dự án:

```env
# App settings
ASPNETCORE_ENVIRONMENT=Development
PORT=5012

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

## 🌐 Sử Dụng PostgreSQL Cloud Dùng Chung (Khuyên Dùng Cho Cả Nhóm)

Để tất cả thành viên trong nhóm luôn có dữ liệu đồng nhất theo thời gian thực (realtime) mà **không cần chạy Docker Desktop**:
1. Đăng ký một cơ sở dữ liệu PostgreSQL miễn phí trên **[Neon.tech](https://neon.tech/)** hoặc **[Supabase](https://supabase.com/)**.
2. Copy chuỗi kết nối (`Connection String`) và dán vào file `.env` trên máy của tất cả thành viên:
   ```env
   ConnectionStrings__DefaultConnection=Host=ep-xxx.ap-southeast-1.neon.tech;Database=neondb;Username=neondb_owner;Password=mat_khau_cua_ban;SSL Mode=Require;Trust Server Certificate=true
   ```
3. Chạy lệnh migrate khởi tạo cấu trúc bảng lên Cloud (chỉ cần 1 bạn chạy 1 lần):
   ```powershell
   dotnet ef database update --project src/EventTicketBooking.Api
   ```
4. Bật ứng dụng Backend:
   ```powershell
   dotnet run --project src/EventTicketBooking.Api
   ```
   *(Hệ thống sẽ tự động kích hoạt `DataSeeder` để nạp sẵn 5 Vai trò, 2 Tài khoản demo Admin/Organizer và các Sự kiện mẫu lên Cloud).*

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
- **Swagger UI**: Access tại [http://localhost:5012/swagger](http://localhost:5012/swagger) (hoặc URL hiển thị trên terminal).
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

## Sơ đồ ghế hiện tại

Giao diện sử dụng DOM/CSS, có lọc khu vực, màu theo hạng vé và phóng to/thu nhỏ. File mẫu 3.000 ghế: data/seat-map-3000.json. Các số đo hiệu năng cần kiểm tra trên môi trường triển khai thực tế.

---

## 🛡️ Sao Lưu Cơ Sở Dữ Liệu & Khôi Phục Thảm Họa (Story S-55: Disaster Recovery)

Hệ thống sao lưu tự động và diễn tập phục hồi sự cố cho PostgreSQL được quản lý qua bộ script PowerShell tại thư mục `scripts/`:

- **Chi tiết kiến trúc & Runbook:** Xem tài liệu đầy đủ tại [docs/S55-database-backup-drill.md](docs/S55-database-backup-drill.md).
- **Sao lưu hằng ngày (Daily Backup):**
  - Chạy thủ công: `.\scripts\backup-db.ps1`
  - Định dạng: PostgreSQL Custom Format (`-Fc`, nén zlib) kèm Manifest JSON (mã băm SHA-256, kích thước, timestamp UTC).
  - Chính sách lưu trữ: Tự động duy trì **14 bản backup hợp lệ gần nhất**; fail-safe không xóa bản cũ nếu backup mới gặp lỗi.
  - Lịch chạy tự động đề xuất: 02:00 AM hằng đêm qua Windows Task Scheduler hoặc Linux cron.
- **Sao lưu Ngoại vi & Mã hóa (Offsite Backup - Phase 3):**
  - Mã hóa Client-Side bằng **`age`** (khóa bất đối xứng X25519, chỉ cần public key `age1...` trên server; private key bảo quản an toàn ngoại tuyến).
  - Đồng bộ lên **Cloudflare R2** qua `rclone` (S3 API).
  - Xác minh remote độc lập bằng cách tải về thư mục tạm và đối soát mã băm SHA-256 từng bit.
  - Retention ngoại vi: Giữ 14 bản hợp lệ trên bucket; cô lập namespace không xóa nhầm dữ liệu lạ.
- **Diễn tập Khôi phục (Disaster Recovery Drill):**
  - Chạy diễn tập tự động: `.\scripts\restore-drill.ps1`
  - Cơ chế phòng vệ an toàn: Chỉ cho phép restore vào database tạm local cô lập `eventpulse_drill_*`; từ chối tuyệt đối máy chủ Cloud/Production (Deny-by-default host guard); đối soát quyền sở hữu (Session ID marker).
  - Tiêu chí đối soát toàn vẹn: Kiểm tra migrations (`__EFMigrationsHistory`), bảng nghiệp vụ, Foreign Keys, Indexes, đếm số bản ghi từng bảng và kiểm tra ngưỡng RTO $\le 15$ phút (900 giây).
  - Đối chiếu row counts với baseline ghi trong manifest lúc backup (nếu manifest có `tableRowCounts`).
- **Diễn tập End-to-End kèm Smoke Test (Docker cô lập):**
  - `.\scripts\run-drill-env.ps1` — dựng PostgreSQL + Redis + API tạm trong Docker (mạng/volume riêng, mật khẩu ngẫu nhiên), restore bản backup hợp lệ mới nhất, khởi động API với **Mock payment + SMTP tắt + AutoMigrate/AutoSeed tắt**, chạy smoke test (`/api/health`, `/api/public/events`, `/api/public/showtimes`), đo RTO và tự dọn dẹp có xác minh ownership.
  - Tham số hữu ích: `-DumpFile <file>`, `-SkipApiSmoke`, `-KeepResources` (giữ lại để điều tra khi lỗi), `-PgPort 5544 -ApiPort 5013`.
- **Lập lịch tự động (02:00 Asia/Ho_Chi_Minh):**
  - Windows: `.\scripts\register-backup-task.ps1` (mặc định dry-run an toàn; `-Register` để đăng ký, `-Remove` để gỡ, `-Force` để cập nhật). Chỉ đăng ký **một** cơ chế cho một database nguồn.
  - Linux: dùng cron (xem hướng dẫn in ra khi chạy script dry-run).
  - Job ghi log qua `-LogFile` (không ghi secrets); kiểm tra lần chạy gần nhất bằng `Get-ScheduledTaskInfo`.
- **Upload Offsite khi chạy backup:**
  - `.\scripts\backup-db.ps1 -EnableOffsite -AgeRecipientKey <age1...>` (đọc cấu hình R2 từ env `R2_ACCOUNT_ID`, `R2_BUCKET_NAME`, `R2_ACCESS_KEY_ID`, `R2_SECRET_ACCESS_KEY`; cần `age` và `rclone` trong PATH).
  - Bản mã hóa có đuôi `.dump.age`; manifest v2 ghi `plaintextSha256` + `encryptedSha256`; upload chỉ được đánh dấu `VerifiedUploaded` sau khi tải về xác minh SHA-256 khớp 100%.
- **Kiểm thử offline (không DB, không Docker, không credentials cloud):**
  - `pwsh -File tests/test-backup-retention-s55.ps1`
  - `pwsh -File tests/test-s55-blockers-offline.ps1`
  - `pwsh -File tests/test-s55-guards-offline.ps1`
  - `pwsh -File tests/test-s55-phase3-offline.ps1`
  - Các bộ test này chạy trong CI (job `s55-offline-tests` của `.github/workflows/ci.yml`).

