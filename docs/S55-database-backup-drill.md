# S-55: Sao lưu cơ sở dữ liệu hằng ngày và diễn tập khôi phục (Database Backup & Disaster Recovery Drill)

Tài liệu hướng dẫn vận hành, kiến trúc an toàn và quy trình diễn tập khôi phục cho Story S-55: Hệ thống Sao lưu Dữ liệu Tự động & Diễn tập Khôi phục Sự cố (Disaster Recovery).

---

## 1. Mục tiêu & Chỉ số SLA Vận hành

Hệ thống sao lưu và diễn tập S-55 được thiết kế nhằm bảo vệ toàn vẹn dữ liệu cho nền tảng bán vé sự kiện EventPulse:

1. **RPO (Recovery Point Objective - Điểm khôi phục mục tiêu):** 
   - **Mục tiêu thiết kế:** $\le 24$ giờ.
   - **Cơ chế:** Thiết lập lịch sao lưu tự động chạy hằng ngày vào khung giờ thấp điểm (ví dụ: 02:00 sáng).
   - *Lưu ý quan trọng:* Đây là chỉ số mục tiêu trong thiết kế vận hành cần duy trì và đo lường thông qua lịch chạy thực tế, không phải cam kết dữ liệu thời gian thực (Zero Data Loss).
2. **RTO (Recovery Time Objective - Thời gian khôi phục mục tiêu):**
   - **Mục tiêu thiết kế:** $\le 15$ phút (900 giây) cho quá trình dựng lại dữ liệu từ file dump vào môi trường kiểm thử.
   - **Cơ chế:** Sử dụng định dạng `pg_dump -Fc` (PostgreSQL Custom Format nén zlib) cho tốc độ giải nén và nạp dữ liệu tối ưu qua `pg_restore`. Thời gian RTO thực tế được đo lường tự động sau mỗi phiên diễn tập và so sánh với ngưỡng mục tiêu.
3. **Chính sách Lưu trữ (Retention Policy):**
   - Giữ lại tối đa **14 bản backup hợp lệ gần nhất** (tương đương 2 tuần lịch sử sao lưu).
   - Tự động xóa an toàn các bản cũ quá hạn sau khi bản backup mới được xác nhận hợp lệ.
   - **Nguyên tắc an toàn:** Nếu backup mới gặp lỗi, hệ thống bảo lưu toàn bộ các bản cũ, tuyệt đối không xóa dữ liệu.
   - **Tiêu chuẩn công nhận backup hợp lệ:** Bản backup bắt buộc phải có cả file dump (`.dump`) và manifest JSON (`.json`) đi kèm, manifest có trạng thái `VerifiedValid`, kích thước > 1024 bytes, và mã băm SHA-256 tính toán từ file dump phải khớp 100% với manifest. Tuyệt đối không công nhận file dump thiếu manifest hoặc sai SHA-256.

---

## 2. Yêu cầu Tiền đề & Phân quyền Hệ thống

### 2.1. Yêu cầu Công cụ Khách (PostgreSQL Client Tools)
- Yêu cầu bộ công cụ client **cùng phiên bản hoặc mới hơn máy chủ nguồn**. Máy chủ nguồn hiện tại (Neon cloud dùng chung) là **PostgreSQL 18.6**, trong khi docker-compose local dùng PostgreSQL 16 — pg_dump từ chối dump server mới hơn client (xem Troubleshooting). Runbook diễn tập mặc định dùng image `postgres:18-alpine` (tham số `-PgImage`):
  - `pg_dump`: Công cụ trích xuất dữ liệu.
  - `pg_restore`: Công cụ giải nén, kiểm tra mục lục (TOC) và nạp dữ liệu.
  - `psql`: Công cụ thực thi câu lệnh SQL quản trị (phục vụ tạo database tạm và đối soát).
- Các công cụ trên cần được thêm vào biến môi trường `PATH` của máy chủ/máy trạm vận hành hoặc truyền trực tiếp qua tham số `-PgDumpPath`, `-PgRestorePath`, `-PsqlPath`.

### 2.2. Phân quyền Cơ sở dữ liệu (Database Permissions)
- **Quyền đối với Tác vụ Sao lưu (Backup):**
  - Tài khoản cần có quyền `CONNECT` tới database mục tiêu.
  - Quyền `SELECT` trên toàn bộ các bảng trong schema `public` và quyền `USAGE` trên schema.
- **Quyền đối với Tác vụ Diễn tập Khôi phục (Restore Drill):**
  - Tài khoản trên máy chủ kiểm thử local cần có quyền **`CREATEDB`** để tạo database tạm thời `eventpulse_drill_*` và thực hiện `DROP DATABASE` sau khi kiểm tra xong.

---

## 3. Kiến trúc Bảo mật & Nguyên tắc Phòng vệ

1. **Bảo mật Thông tin xác thực (Credentials Safety):**
   - Mật khẩu cơ sở dữ liệu **tuyệt đối không truyền trên dòng lệnh** (để chống lộ qua `Get-Process` hoặc command line logging).
   - Script sử dụng biến môi trường tạm thời `$env:PGPASSWORD` trong khối `try/finally` và lập tức xóa bỏ ngay khi kết thúc lệnh.
   - Parser connection string hỗ trợ an toàn các mật khẩu chứa ký tự đặc biệt hoặc dấu chấm phẩy (được bao trong nháy kép/nháy đơn).
2. **Cơ chế Staging An toàn khi Tạo Backup:**
   - Trong quá trình `pg_dump`, file được ghi ra dưới tên tạm `eventpulse_backup_yyyyMMdd_HHmmss_in_progress.dump`.
   - Chỉ khi file vượt qua kiểm tra kích thước, kiểm tra mục lục TOC qua `pg_restore -l`, tính SHA-256 và ghi manifest thành công thì mới được đổi tên (atomic move) thành file chính thức.
   - Nếu xảy ra lỗi giữa chừng, file tạm dở dang lập tức bị xóa bỏ để không tạo rác.
3. **Lưu trữ Tách biệt (Isolated Storage):**
   - Thư mục sao lưu mặc định: `artifacts/database-backups/` (đã được loại trừ khỏi Git qua `.gitignore`).
   - **DEPRECATED — KHÔNG dùng file `data/backup_full.sql` làm bản backup:** đây là dump schema cũ (UTF-16, chỉ chứa schema, bảng chữ thường đã lỗi thời), được commit trong Git từ giai đoạn trước. Mọi hoạt động backup/restore phải dùng pipeline S-55 (`backup-db.ps1` + manifest), không dùng file này.
   - Khuyến nghị môi trường Production: Mount một ổ đĩa lưu trữ riêng biệt (External Volume / NAS / S3 Bucket Mount) và truyền qua tham số `-BackupDir` để đảm bảo file backup tồn tại độc lập với vòng đời của container ứng dụng.
4. **Phòng vệ Chống ghi đè Production (Strict Isolation & Deny-by-Default Guard - H1):**
   - Áp dụng nguyên tắc **Deny-by-Default**: Chỉ cho phép khôi phục vào các máy chủ trong Allowlist mặc định (`localhost`, `127.0.0.1`, `::1`, `docker-postgres`, `postgres`, `eventpulse_test_pg_s55`) hoặc được chỉ định tường minh qua `-AllowedHosts` / biến môi trường `$env:DRILL_ALLOWED_HOSTS`. Mọi host lạ bị từ chối ngay lập tức trước bất kỳ lệnh SQL nào.
   - Script diễn tập `restore-drill.ps1` bắt buộc tên database đích phải bắt đầu bằng tiền tố: `eventpulse_drill_`.
   - Tự động lấy thông tin nguồn từ manifest để đối soát phòng vệ nếu không truyền `-SourceConnectionString`.
   - **Từ chối tuyệt đối** nếu máy chủ đích thuộc hạ tầng Cloud (`neon.tech`, `render.com`, `amazonaws.com`,...) hoặc trùng với máy chủ nguồn.
5. **Xác minh Tính toàn vẹn Bản backup Trước khi Restore (H2 Guard):**
   - Bắt buộc kiểm tra file manifest JSON đi kèm và tính mã băm SHA-256 thực tế trước khi chọn file dump và trước khi thực thi `pg_restore`.
   - Từ chối tuyệt đối nếu file dump thiếu manifest, trạng thái không phải `VerifiedValid`, hoặc mã băm SHA-256 không khớp 100%.
6. **Thời gian Chờ Hữu hạn cho Tác vụ Sao lưu (pg_dump Timeout Guard - H3):**
   - Thiết lập timeout hữu hạn mặc định 300 giây (`-DumpTimeoutSeconds 300`) cho lệnh `pg_dump`.
   - Nếu tiến trình bị treo hoặc vượt quá thời gian cho phép, hệ thống tự động kết thúc (kill) tiến trình con an toàn, chờ tiến trình dừng, xóa bỏ file staging `.in_progress`, bảo lưu 100% các bản backup cũ và trả về mã lỗi rõ ràng.
7. **Dấu nhận diện Quyền sở hữu (Ownership Marker):**
   - Trước khi khôi phục, script tạo bảng `__eventpulse_drill_ownership` ghi nhận `Drill Session ID` duy nhất.
   - Script chỉ thực hiện lệnh `DROP DATABASE` dọn dẹp khi thỏa mãn đồng thời:
     1. Database do chính phiên chạy hiện tại tạo ra.
     2. Tên database có tiền tố hợp lệ `eventpulse_drill_`.
     3. Đã xác minh marker sở hữu khớp chính xác `drill_run_id`.
     4. Quá trình restore VÀ toàn bộ các bước đối soát toàn vẹn hoàn tất thành công.
     5. Người dùng không bật cờ `-KeepDrillDb`.
   - **Nếu restore hoặc bất kỳ kiểm tra nào thất bại: Database tạm bắt buộc được GIỮ LẠI NGUYÊN TRẠNG** để phục vụ kỹ thuật viên điều tra lỗi.

---

## 4. Hướng dẫn Vận hành

### 4.1. Chạy Sao lưu thủ công
Từ thư mục gốc của repository:

```powershell
# Chạy sao lưu với chuỗi kết nối từ biến môi trường
.\scripts\backup-db.ps1

# Hoặc truyền chuỗi kết nối và thư mục lưu trữ riêng
.\scripts\backup-db.ps1 -ConnectionString "Host=localhost;Port=5432;Database=event_ticket_db;Username=postgres;Password=your_password" -BackupDir "D:\Backups\EventPulse"
```

**Kết quả đầu ra của bản backup:**
- File dữ liệu nén: `eventpulse_backup_yyyyMMdd_HHmmss.dump`
- File manifest xác thực: `eventpulse_backup_yyyyMMdd_HHmmss.json` (chứa mã băm SHA-256, kích thước, thời gian tạo UTC và phiên bản pg_dump).

### 4.2. Cấu hình Lịch tự động hằng ngày (Windows Task Scheduler)
Để tự động hóa sao lưu hằng ngày vào lúc **02:00 giờ Việt Nam (Asia/Ho_Chi_Minh)** trên Windows Server / Windows Host, dùng script đăng ký có kiểm tra an toàn:

```powershell
# Dry-run (an toàn, không thay đổi gì): in hướng dẫn + kiểm tra môi trường
.\scripts\register-backup-task.ps1

# Đăng ký task (cần xác nhận host lưu trữ database là máy này)
.\scripts\register-backup-task.ps1 -Register -DailyAt 02:00 -BackupArgs '-LogFile artifacts\logs\backup.log'

# Kiểm tra lần chạy gần nhất
Get-ScheduledTaskInfo -TaskName 'EventPulse_Daily_Database_Backup' | Select-Object LastRunTime, LastTaskResult, NextRunTime
```

- Task Scheduler chạy theo **giờ địa phương của máy**: script sẽ cảnh báo nếu máy không ở múi giờ UTC+07:00.
- Connection string được `backup-db.ps1` đọc từ biến môi trường của phiên đăng nhập — **không nhúng secrets vào Task Scheduler**.
- **Chỉ đăng ký MỘT cơ chế scheduler cho MỘT database nguồn** (tránh backup trùng lặp).

### 4.3. Cấu hình Lịch tự động hằng ngày trên Linux / Docker (Crontab)
Nếu triển khai trên môi trường Linux, dùng cron với timezone tường minh:
```bash
CRON_TZ=Asia/Ho_Chi_Minh
0 2 * * * cd /app && pwsh -NoProfile -File ./scripts/backup-db.ps1 -LogFile /var/log/eventpulse_backup.log >> /var/log/eventpulse_backup.log 2>&1
```

### 4.4. Chạy PostgreSQL client qua Docker (máy Windows không cài client)

Khi máy chạy backup/restore **không cài PostgreSQL client**, các script có thể dùng client trong image `postgres:18-alpine` (pg_dump/pg_restore/psql 18.6) qua Docker Desktop:

- **Chọn chế độ:** tham số `-PgToolMode Docker` (backup-db.ps1 / restore-drill.ps1) hoặc biến môi trường `S55_PG_TOOL_MODE=Docker` (mặc định `Native` — tương thích ngược hoàn toàn với cách chạy executable trực tiếp).
- **Image tùy chỉnh:** `-PgDockerImage <image>` (mặc định `postgres:18-alpine`; image phải chứa pg_dump/pg_restore/psql).
- **Điều kiện tiên quyết:** Docker Desktop đang chạy, `docker` trong PATH, thư mục backup nằm trong vùng Docker Desktop cho phép mount (mặc định ổ `C:`).
- **Cơ chế an toàn của Docker mode:**
  - Mật khẩu **KHÔNG xuất hiện trên command line**: ghi vào `--env-file` tạm trong `%TEMP%`, xóa ngay trong khối `finally`. Thư mục env-file được đặt ACL giới hạn (chỉ user hiện tại + SYSTEM, không kế thừa); password chứa ký tự xuống dòng (CR/LF) bị **từ chối** để chống injection vào biến môi trường container. Mỗi lần chạy Docker mode, script quét xóa các thư mục `s55_pgenv_*` cũ hơn 24 giờ còn sót do các lần trước bị kết thúc cứng (bỏ qua reparse point/junction, chỉ xóa đúng mẫu trong TEMP). Giới hạn: nếu chính lần chạy hiện tại bị kill cứng, env-file của lần đó vẫn còn cho đến lần chạy kế tiếp.
  - Chỉ mount **một** thư mục làm việc cần thiết tại `/s55work` (BackupDir với pg_dump; thư mục chứa dump ở chế độ chỉ đọc với pg_restore); đường dẫn host trong tham số được remap sang `/s55work/...`.
  - Host `localhost`/`127.0.0.1`/`::1` được chuyển thành `host.docker.internal` (Docker Desktop Windows không hỗ trợ `--network host`).
  - SSL `verify-full`/`verify-ca`: tự đặt `PGSSLROOTCERT=system`; nếu image báo `root certificate file ... does not exist`, tạo image riêng có `ca-certificates` và truyền qua `-PgDockerImage`.
  - Timeout vẫn có hiệu lực: nếu container treo, script chạy `docker rm -f <container>` trước khi hủy tiến trình; container chạy với `--rm --name s55_<tool>_<id>` nên không để lại container lâu dài.
  - Khi lệnh trong container thất bại, stderr của docker/pg tool được gộp vào thông báo lỗi (đã che mật khẩu) để chẩn đoán.
- **Ví dụ:**
  ```powershell
  .\scripts\backup-db.ps1 -PgToolMode Docker
  .\scripts\restore-drill.ps1 -PgToolMode Docker -KeepDrillDb
  ```
- **Lưu ý:** `run-drill-env.ps1` chạy restore-drill bên trong runner container (đã có sẵn pg tools) — runbook luôn ép `-PgToolMode Native` cho bước này để tránh docker-in-docker.
- **Phạm vi kiểm chứng:** đã xác minh runtime cơ chế wrapper (`pg_dump --version`/`psql --version` qua Docker trả về PostgreSQL 18.6) và 23 test offline; **chưa** chạy backup/restore thật qua Docker mode — thuộc bước xác minh trước lần backup offsite đầu tiên.

---

## 5. Hướng dẫn Diễn tập Khôi phục (Disaster Recovery Drill)

### 5.1. Chạy diễn tập tự động vào Database tạm
Script sẽ tự động chọn bản backup hợp lệ mới nhất, tạo database tạm local `eventpulse_drill_*`, khôi phục, đối soát toàn vẹn và dọn dẹp:

```powershell
.\scripts\restore-drill.ps1
```

> [!IMPORTANT]
> **Bắt buộc cung cấp connection string đích (N1 — fail-closed):** script **KHÔNG fallback** về `localhost` mặc định. Phải có một trong ba: tham số `-TargetConnectionString`, biến `TARGET_PG_CONNECTION` hoặc `ConnectionStrings__DefaultConnection`; thiếu cả ba, script dừng ngay với lỗi rõ ràng trước mọi thao tác kết nối/ghi/xóa database (tránh diễn tập nhầm vào PostgreSQL local đang dùng chung). Runbook `run-drill-env.ps1` tự truyền `TARGET_PG_CONNECTION` của môi trường tạm nên không cần cấu hình thêm.

### 5.2. Chạy diễn tập với file backup cụ thể và giữ lại database để soi dữ liệu
```powershell
.\scripts\restore-drill.ps1 -DumpFile "artifacts/database-backups/eventpulse_backup_20261017_051500.dump" -KeepDrillDb
```

### 5.2b. Diễn tập End-to-End với Smoke Test (Docker cô lập — khuyến nghị)

Runbook `scripts/run-drill-env.ps1` thực hiện toàn bộ quy trình AC4 trong môi trường Docker cô lập:

1. Dựng PostgreSQL + Redis + API tạm (mạng/volume riêng, mật khẩu ngẫu nhiên chỉ tồn tại trong RAM).
2. Chạy `restore-drill.ps1` (H1/H2 guard + integrity check + baseline + RTO) trên bản backup hợp lệ mới nhất; kết quả ghi ra `drill-result.json`.
3. Khởi động API trỏ vào database vừa khôi phục với **`ASPNETCORE_ENVIRONMENT=Drill`** (KHÔNG phải `Production` — guard S-43 cấm `PAYMENT_PROVIDER=Mock` trong Production; KHÔNG phải `Development` — tránh bật nhánh dev), **`PAYMENT_PROVIDER=Mock`**, **SMTP trống (không gửi email thật)**, **`Database__AutoMigrate=false` + `Database__AutoSeed=false`** (không tự ý thay đổi schema).
4. Smoke test: `/api/health`, `/api/public/events`, `/api/public/showtimes` phải trả HTTP 200 trên dữ liệu khôi phục.
5. Dọn dẹp có xác minh ownership marker + snapshot container trước/sau; nếu lỗi thì GIỮ NGUYÊN tài nguyên để điều tra.

```powershell
.\scripts\run-drill-env.ps1                                   # dùng bản backup hợp lệ mới nhất
.\scripts\run-drill-env.ps1 -DumpFile "artifacts\database-backups\eventpulse_backup_xxx.dump"
.\scripts\run-drill-env.ps1 -SkipApiSmoke                     # chỉ drill DB, không khởi động API
.\scripts\run-drill-env.ps1 -KeepResources                    # giữ lại môi trường để điều tra
```

### 5.3. Tiêu chí Đối soát Toàn vẹn Đa cấp độ & Giới hạn Xác minh
Script `restore-drill.ps1` thực hiện đối soát động trên database vừa khôi phục:
1. **Kiểm tra Migrations (`__EFMigrationsHistory`):** Đếm số lượng migration đã áp dụng. Nếu $= 0$ sẽ đánh dấu thất bại ngay lập tức.
2. **Kiểm tra Bảng (`information_schema.tables`):** Đếm số lượng bảng nghiệp vụ trong schema `public` (loại bỏ bảng tiền tố `__`). Nếu $= 0$ sẽ đánh dấu thất bại.
3. **Kiểm tra Khóa ngoại (`FOREIGN KEY` constraints):** Xác nhận các ràng buộc toàn vẹn quan hệ giữa các bảng (`Orders` $\rightarrow$ `Users`, `Tickets` $\rightarrow$ `Showtimes`,...). Nếu số lượng $= 0$ sẽ đánh dấu thất bại ngay lập tức.
4. **Kiểm tra Chỉ mục (`pg_indexes`):** Đảm bảo các chỉ mục tìm kiếm và unique index (như kiểm soát giữ ghế, đơn hàng chờ) được tạo đầy đủ. Nếu số lượng $= 0$ sẽ đánh dấu thất bại ngay lập tức.
5. **Thống kê Số lượng bản ghi:** Liệt kê chi tiết số dòng thực tế của từng bảng sau khi giải nén. Bắt buộc dừng và báo lỗi nếu có bất kỳ bảng nghiệp vụ nào không thể đếm số bản ghi.
6. **Đối chiếu Baseline:** Nếu manifest có `tableRowCounts` (được `backup-db.ps1` ghi lúc backup khi có `psql`), so sánh số dòng từng bảng sau restore với baseline — mọi chênh lệch đều làm diễn tập THẤT BẠI. Bảng xuất hiện thêm so với baseline chỉ cảnh báo (có thể là bảng mới thêm sau lần backup).
6. **Đo lường RTO:** Tính toán số giây thực tế từ khi bắt đầu đến khi hoàn thành diễn tập và so sánh tự động với ngưỡng mục tiêu $\le 900$ giây. Nếu RTO vượt quá 900 giây ($> 900$s) hoặc dữ liệu đo lường không hợp lệ, hệ thống bắt buộc đánh dấu diễn tập THẤT BẠI (FAIL).

> [!IMPORTANT]
> **Xác nhận Giới hạn Kiểm tra (Verification Limitations):**
> Việc đếm số bảng, số dòng, migration, khóa ngoại và chỉ mục là các **kiểm tra cấu trúc và định lượng dữ liệu**, không thay thế cho việc kiểm chứng logic nghiệp vụ chuyên sâu. Khi không có bản lưu số dòng trước đó (baseline), số dòng chỉ phản ánh dữ liệu hiện có trong dump. Sau khi hoàn thành khôi phục thực tế, bắt buộc phải thực hiện thêm **Smoke Test API an toàn** trên môi trường drill (ví dụ: đăng nhập tài khoản, tra cứu sự kiện, kiểm tra lịch diễn) để bảo đảm ứng dụng hoạt động chính xác.

---

## 6. Kịch bản Ứng phó Sự cố Thực tế (Disaster Recovery Runbook)

Khi xảy ra thảm họa mất dữ liệu trên môi trường chính (Production Data Loss / Corruption):

1. **Bước 1 - Cách ly & Ngăn chặn phát sinh lỗi:**
   - Chuyển Backend API sang chế độ bảo trì (`Maintenance Mode`) hoặc tạm dừng Web Service để ngắt toàn bộ kết nối người dùng.
2. **Bước 2 - Lựa chọn bản sao lưu hợp lệ mới nhất:**
   - Truy cập kho lưu trữ backup độc lập.
   - Đọc file manifest `.json` tương ứng để đối soát mã băm SHA-256:
     ```powershell
     Get-FileHash -Algorithm SHA256 "eventpulse_backup_yyyyMMdd_HHmmss.dump"
     ```
   - Chạy `pg_restore -l` kiểm tra mục lục để chắc chắn archive không bị lỗi truyền dẫn.
3. **Bước 3 - Tiến hành khôi phục vào Production (Chỉ thực hiện khi có phê duyệt):**
   - Đổi tên database bị hỏng thành `event_ticket_db_corrupted_<timestamp>` (giữ lại để điều tra, không drop ngay).
   - Tạo database mới sạch: `event_ticket_db`.
   - Nạp dữ liệu:
     ```powershell
     pg_restore -h <prod-host> -U <prod-user> -d event_ticket_db --no-owner --no-privileges "eventpulse_backup_yyyyMMdd_HHmmss.dump"
     ```
4. **Bước 4 - Nghiệm thu sau khôi phục:**
   - Chạy `dotnet run --project tools/MigrationAudit` để xác minh toàn bộ 38 migration và schema khớp 100% (số migration xác minh từ `src/EventTicketBooking.Api/Migrations/` tại thời điểm viết tài liệu).
   - Chạy smoke test API kiểm tra các chức năng trọng yếu.
   - Kiểm tra endpoint `/api/health` trả về `Healthy`.
   - Bật lại ứng dụng cho người dùng.

---

## 7. Xử lý Sự cố Thường gặp (Troubleshooting)

| Lỗi thường gặp | Nguyên nhân | Cách khắc phục |
|---|---|---|
| `Không tìm thấy công cụ 'pg_dump'` / `pg_restore` | Máy chủ chưa có PostgreSQL client trong PATH. | Cài đặt PostgreSQL 16 Command Line Tools, hoặc truyền đường dẫn tường minh qua `-PgDumpPath "C:\Program Files\PostgreSQL\16\bin\pg_dump.exe"`. |
| `pg_restore -l từ chối file archive` | File backup bị ngắt đột ngột khi tải hoặc ghi đĩa, dung lượng 0 bytes. | Kiểm tra dung lượng đĩa còn trống; chạy lại backup; kiểm tra log lỗi của pg_dump. |
| `Tên database đích BẮT BUỘC phải bắt đầu bằng tiền tố eventpulse_drill_` | Cơ chế phòng vệ kích hoạt do tên database đích không đúng quy ước an toàn. | Đổi tên database kiểm thử thành `eventpulse_drill_<tên_tuỳ_chọn>`. |
| `Máy chủ đích thuộc hạ tầng Cloud` | Cơ chế an toàn ngăn chặn diễn tập trên Cloud/Production. | Trỏ `-TargetConnectionString` về máy chủ PostgreSQL local hoặc container test local. |
| `Database sau khi khoi phuc khong co bang nghiep vu nao` | File dump rỗng hoặc quá trình restore bị lỗi nửa chừng. | Kiểm tra lại file dump nguồn; database drill sẽ được giữ lại nguyên trạng để phục vụ điều tra lỗi. |
| `pg_dump: error: invalid sslmode value: "VerifyFull"` | Chuỗi kết nối dùng giá trị SSL mode kiểu Npgsql, không phải libpq. | Đã xử lý tự động: script ánh xạ `VerifyFull`→`verify-full`, `VerifyCA`→`verify-ca` (hàm `ConvertTo-LibPqSslMode` trong `scripts/s55-common.ps1`). |
| `root certificate file "/root/.postgresql/root.crt" does not exist` | Client chạy trong container Linux không có file chứng chỉ gốc mặc định. | Đã xử lý tự động: script đặt `PGSSLROOTCERT=system` trên Linux; cần gói `ca-certificates` trong container runner. |
| `pg_dump: aborting because of server version mismatch` | Server nguồn (Neon 18.6) mới hơn client (16.x). | Dùng client PostgreSQL 18 (image `postgres:18-alpine` hoặc cài client 18). Client mới hơn luôn dump/restore được server cũ hơn. |
| `Error: in 18+, these Docker images are configured...` khi dựng PostgreSQL drill | Image PostgreSQL 18+ từ chối mount volume tại `/var/lib/postgresql/data` (cần mount tại `/var/lib/postgresql` để hỗ trợ `pg_upgrade --link`). | Đã xử lý: `run-drill-env.ps1` mount volume drill tại `/var/lib/postgresql`. Nếu tự dựng container 18+ bằng tay, mount tại `/var/lib/postgresql` thay vì `/var/lib/postgresql/data`. |

---

## 8. Kiến trúc Sao lưu Ngoại vi (Offsite Storage) & Mã hóa Client-Side (Phase 3)

Nhằm bảo vệ dữ liệu trước nguy cơ thảm họa máy chủ (Server Disaster / Host Failure / Ransomware), Phase 3 triển khai cơ chế đẩy dữ liệu ngoại vi tới dịch vụ **Cloudflare R2** (giao diện S3-compatible) kết hợp **mã hóa bất đối xứng phía client bằng age** trước khi dữ liệu rời khỏi máy chủ ứng dụng.

### 8.1. Nguyên tắc An toàn & Quản lý Khóa (Key Management)
- **Mã hóa Bất đối xứng (Asymmetric Encryption với `age`):**
  - **Public Key (`age1...`):** Được cấu hình trên máy chủ backup (qua biến môi trường `$env:AGE_RECIPIENT_PUBKEY` hoặc cờ `-AgeRecipientKey`). Khóa công khai này chỉ có quyền MÃ HÓA, không thể giải mã dữ liệu.
  - **Private Key (`AGE-SECRET-KEY-1...`):** **TUYỆT ĐỐI KHÔNG LƯU TRÊN MÁY CHỦ BACKUP**, không commit vào repository Git, không ghi vào file log hoặc artifacts. Khóa bí mật chỉ được lưu trữ an toàn trong két sắt mật khẩu ngoại tuyến (Offline Secret Vault) của Quản trị viên phục vụ Disaster Recovery.
- **Cơ chế Staging khi Mã hóa:**
  - File mã hóa được xuất tạm ra file `${backupFile}.age.in_progress`.
  - Chỉ khi mã hóa thành công (exit code 0, kích thước > 64 bytes, SHA-256 hợp lệ) thì mới đổi tên thành file chính thức `${backupFile}.age`.
  - Nếu mã hóa lỗi: Tự động dọn dẹp file staging dở dang; **bảo lưu 100% file dump gốc và không xóa bất kỳ backup nào**.

### 8.2. Định dạng Manifest Version 2.0 (Dual-Layer Hash)
Manifest được nâng cấp lên Version 2.0 để liên kết chặt chẽ giữa file dump gốc và file mã hóa ngoại vi:
```json
{
  "backupId": "ep-backup-20261018_090000-a1b2c3d4",
  "manifestVersion": "2.0",
  "createdAtUtc": "2026-10-18T02:00:00.0000000Z",
  "backupFile": "eventpulse_backup_20261018_090000.dump",
  "encryptedFile": "eventpulse_backup_20261018_090000.dump.age",
  "sizeBytes": 204800,
  "encryptedSizeBytes": 218500,
  "plaintextSha256": "853389CB1224E0A6C2D60417F02BA9AE8BA6A9ED45308386573AE2667A188C4F",
  "sha256": "853389CB1224E0A6C2D60417F02BA9AE8BA6A9ED45308386573AE2667A188C4F",
  "encryptedSha256": "C0BA202E85B133F12CA9DB67C51A2FAAF3EA131C81F99CCAABCA1FBE4D7AA484",
  "encryption": {
    "algorithm": "age-x25519",
    "recipient": "age1ql3z7hjy54pw3hyww5ayyfg7zqgvc7w3j2elw8zmrj2kg5sfn9aqmcac8p"
  },
  "sourceHost": "localhost",
  "sourceDatabase": "event_ticket_db",
  "pgDumpVersion": "PostgreSQL 16.x",
  "format": "PostgreSql_CustomFormat_Fc_Age",
  "status": "VerifiedValid",
  "offsiteStatus": "VerifiedUploaded",
  "offsiteVerifiedAtUtc": "2026-10-18T02:01:15.0000000Z"
}
```
*Ghi chú:* Thuộc tính `sha256` được duy trì song song với `plaintextSha256` để đảm bảo tính tương thích ngược 100% với các công cụ kiểm thử Version 1.0.

### 8.2b. Lưu ý Định dạng Tên Object của `rclone lsjson` (trước lần upload đầu)

Retention offsite đã xử lý **cả hai** dạng tên object mà `rclone lsjson` có thể trả về (có hoặc không kèm prefix `s55/`), chỉ coi object nằm **trực tiếp** trong prefix là thuộc namespace backup; object trong thư mục con của prefix được bảo vệ tuyệt đối. Tuy nhiên, trước lần upload thật đầu tiên, hãy chạy một lệnh **chỉ đọc** để xác nhận định dạng thực tế (trong phiên đã cấu hình credentials):

```powershell
& 'C:\Users\ADMIN\tools\rclone\rclone.exe' lsjson "R2:s55-postgres-backup/s55" --s3-provider Cloudflare --s3-access-key-id $env:R2_ACCESS_KEY_ID --s3-secret-access-key $env:R2_SECRET_ACCESS_KEY --s3-endpoint "https://$env:R2_ACCOUNT_ID.r2.cloudflarestorage.com"
```

Kết quả phải hiển thị `Name` có hoặc không có tiền tố `s55/` — cả hai đều được script hỗ trợ; nếu có dạng khác, dừng và báo lại trước khi bật retention.

### 8.3. Quy trình Upload và Xác minh Ngoại vi R2 (Download & Hash Verification)
1. Upload file mã hóa `.dump.age` và manifest `.json` lên Cloudflare R2 qua công cụ `rclone` (S3 API).
2. **Không tin cậy mù quáng vào mã thoát (exit code 0):** Script tải ngược file mã hóa từ R2 về một thư mục tạm biệt lập.
3. Tính toán lại mã băm SHA-256 của file vừa tải về và so sánh trực tiếp với `encryptedSha256` trong manifest.
4. Chỉ khi mã băm khớp 100% từng bit: Hệ thống mới cập nhật `offsiteStatus = "VerifiedUploaded"` và đồng bộ lại manifest lên remote.
5. Nếu lệch hash hoặc tải thất bại: Ném ngoại lệ, **tuyệt đối không đánh dấu VerifiedUploaded**, và dừng ngay trước khi thực thi retention.

### 8.4. Chính sách Retention Offsite 14 Bản Độc lập (Fail-Safe & Namespace Isolation)
- **Giữ tối đa 14 bản backup hợp lệ gần nhất** trên Cloudflare R2.
- **Quy tắc an toàn sống còn (Fail-Safe):** Nếu việc upload hoặc xác minh bản mới gặp lỗi (`IsUploadFailed = $true`), hệ thống lập tức hủy tác vụ retention, **tuyệt đối không xóa bản backup thứ 15 hoặc bất kỳ object cũ nào**.
- **Retention lỗi sau khi upload đã VerifiedUploaded (F5):** lỗi retention transient (mạng/quyền) chỉ cảnh báo `[OFFSITE RETENTION FAILED]` và trả về trạng thái `RetentionFailedNonBlocking` — bản backup vẫn được tính là thành công (đã upload + xác minh); người vận hành cần chạy lại retention thủ công. Ngược lại, retention **không bao giờ** chạy khi upload/verification chưa thành công.
- **Cách ly Namespace (Namespace Guard):** Hệ thống chỉ nhận diện và xử lý các object khớp đúng quy ước `eventpulse_backup_\d{8}_\d{6}\.(dump\.age|json)`. Mọi object ngoài namespace (như file sao lưu của hệ thống khác, file log, thư mục khác trong bucket) được bảo vệ tuyệt đối 100%, không bao giờ bị xóa nhầm.

### 8.5. Ma trận Phân định Cấu hình & Hiện trạng Kiểm thử

> [!IMPORTANT]
> **Bảng phân định trạng thái rõ ràng giữa Mã nguồn, Cấu hình và Môi trường:**

| Hạng mục | Trạng thái hiện tại | Chi tiết |
|---|---|---|
| **Module `scripts/backup-upload-r2.ps1`** | **ĐÃ TRIỂN KHAI** | Hỗ trợ đầy đủ logic validation, mã hóa `age`, upload R2 qua `rclone`, download SHA-256 verify và retention 14 bản. |
| **Tích hợp `scripts/backup-db.ps1`** | **ĐÃ TRIỂN KHAI** | Bổ sung các tham số `-EnableOffsite`, `-AgeRecipientKey`, `-R2BucketName`, cập nhật manifest Version 2.0 dual-hash. |
| **Tương thích `scripts/restore-drill.ps1`** | **ĐÃ TRIỂN KHAI** | Hỗ trợ xác thực file `.dump.age` qua `encryptedSha256` trong manifest và cơ chế giải mã an toàn qua `-AgeIdentityFile`. |
| **Cặp khóa age (`age1...` / `AGE-SECRET-KEY-1...`)** | **CẦN NGƯỜI DÙNG THIẾT LẬP** | Người vận hành cần tự sinh cặp khóa thật qua lệnh `age-keygen -o key.txt`, chỉ đưa public key lên server. |
| **Bucket Cloudflare R2 & API Token** | **CẦN NGƯỜI DÙNG THIẾT LẬP** | Cần tạo Bucket riêng trên Cloudflare Dashboard và cấp API Token có quyền Object Read/Write trên bucket đó. |
| **Các giá trị trong tài liệu/test** | **CHỈ LÀ VÍ DỤ** | Mọi chuỗi `age1ql3z...`, `test_key_id`, `eventpulse-backup-prod` trong tài liệu/test chỉ là dữ liệu giả lập. |
| **Bộ kiểm thử Phase 3 (`test-s55-phase3-offline.ps1`)** | **ĐÃ CHẠY & PASS (25/25)** | Đã kiểm thử 100% offline: validation config, age key format, error cleanup, dual hash, upload failure, checksum mismatch, retention 14 bản, namespace guard, masking secrets. |
| **Kết nối Cloudflare R2 & Database thật** | **DATABASE THẬT: ĐÃ CHẠY (18/10/2026)** | Backup thật từ Neon cloud `neondb` (PostgreSQL 18.6) thành công: `artifacts/database-backups/eventpulse_backup_20261018_072456.dump` (671.084 bytes, SHA-256 + baseline 32 bảng). **R2: CHƯA CHẠY** — chưa có credentials (cần người dùng tạo bucket + API token). |
| **Smoke Test API trên DB đã restore (Phase 4)** | **ĐÃ CHẠY & PASS (18/10/2026)** | Runbook `scripts/run-drill-env.ps1` hoàn tất end-to-end trên bản backup thật `eventpulse_backup_20261018_072456.dump` (Neon 18.6): restore PASS (42 migrations, 31 bảng, 34 FK, 100 index, baseline 0 lệch, **RTO 2.07s ≤ 900s**), API khởi động môi trường `Drill` (Mock payment + SMTP tắt + AutoMigrate/AutoSeed tắt), smoke test 3 endpoint `/api/health`, `/api/public/events`, `/api/public/showtimes` đều HTTP 200, dọn dẹp có xác minh ownership marker + snapshot container trước/sau trùng khớp (isolation confirmed). Bằng chứng lưu tại `artifacts/drill-evidence/` (drill-result.json, smoke_test_report.txt, docker_snapshot_before/after.txt). Các lỗi phát hiện trong lần chạy đầu đã sửa: mount volume PG 18+ tại `/var/lib/postgresql`, đối chiếu baseline bảng tiền tố `__` (hàm `Compare-S55BaselineRowCounts` + 4 regression test), môi trường API drill `ASPNETCORE_ENVIRONMENT=Drill`. |

### 8.6. Hướng dẫn Chuẩn bị Cấu hình cho Vận hành Thật

Khi triển khai trên máy chủ Production, Quản trị viên thực hiện các bước sau:

1. **Sinh cặp khóa age an toàn:**
   ```powershell
   # Chạy trên máy quản trị an toàn (KHÔNG chạy trên máy chủ public)
   age-keygen -o eventpulse_dr_key.txt
   # File sẽ chứa:
   # Public key:  age1... (Dùng để mã hóa - nạp vào máy chủ backup)
   # Private key: AGE-SECRET-KEY-1... (Cất vào Vault an toàn)
   ```

2. **Cấu hình biến môi trường trên máy chủ Backup:**
   ```powershell
   [System.Environment]::SetEnvironmentVariable('AGE_RECIPIENT_PUBKEY', 'age1your_real_public_key_here...', 'Machine')
   [System.Environment]::SetEnvironmentVariable('R2_ACCOUNT_ID', 'your_cloudflare_account_id', 'Machine')
   [System.Environment]::SetEnvironmentVariable('R2_BUCKET_NAME', 'your-private-backup-bucket', 'Machine')
   [System.Environment]::SetEnvironmentVariable('R2_ACCESS_KEY_ID', 'your_r2_token_access_key', 'Machine')
   [System.Environment]::SetEnvironmentVariable('R2_SECRET_ACCESS_KEY', 'your_r2_token_secret_key', 'Machine')
   [System.Environment]::SetEnvironmentVariable('R2_PREFIX', 'your-prefix', 'Machine') # tuy chon; mac dinh 'database-backups'
   ```
   *Thứ tự ưu tiên prefix:* tham số `-R2Prefix` → biến `R2_PREFIX` → mặc định `database-backups`. Dấu `/` thừa ở đầu/cuối prefix được tự động chuẩn hóa.

3. **Cài đặt công cụ khách trên máy chủ:**
   - Cài đặt `age` CLI: [https://github.com/FiloSottile/age/releases](https://github.com/FiloSottile/age/releases)
   - Cài đặt `rclone` CLI: [https://rclone.org/downloads/](https://rclone.org/downloads/)
   - Đảm bảo cả hai công cụ có trong `PATH`.

