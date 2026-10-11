# S-43: Giám sát và cảnh báo khi hệ thống quá tải

Tài liệu hướng dẫn vận hành, kiến trúc bảo mật và kiểm thử cho Story S-43: Hệ thống Giám sát & Cảnh báo (Monitoring & Alerting).

---

## 1. Tổng quan kiến trúc

Hệ thống giám sát S-43 thu thập và trực quan hóa các chỉ số hiệu năng và nghiệp vụ cốt lõi:
1. **Tổng số HTTP Requests & Phân bố Status Code**: Theo dõi thông lượng và mã phản hồi.
2. **Tỉ lệ lỗi HTTP (Error Rate)**: Cảnh báo chính trên `5xx` (lỗi hệ thống/máy chủ quá tải); quan sát riêng `4xx` và `429` (Rate limiting S-41 / Waiting room S-40).
3. **Thời gian phản hồi p95 (Latency)**: Đo lường độ trễ phân vị 95 bằng Histogram chuẩn.
4. **Số tranh chấp GHẾ (`seat_hold_conflicts_total`)**: Ghi nhận xung đột khi 2 người cùng tranh giữ 1 ghế qua Redis (`redis`), DB Active (`db_active`), hoặc DB Concurrency (`db_constraint`). Không tính xung đột soát vé ngoại tuyến S-36.
5. **Số webhook bị từ chối (`payment_webhooks_rejected_total`)**: Ghi nhận các trường hợp webhook thanh toán bị từ chối (`empty_payload`, `missing_signature`, `invalid_signature`, `invalid_data`). Không tính exception 500.

---

## 2. Bảo mật Endpoint `/metrics` & Quản lý Secrets

### Bảo vệ Endpoint `/metrics` (Application-Level Protection)
- Endpoint `/metrics` nằm trên cùng cổng ứng dụng (`5012`).
- Mọi yêu cầu truy cập bắt buộc phải có Header:
  `Authorization: Bearer <METRICS_SCRAPE_TOKEN>`
- Nếu thiếu hoặc sai token: API trả về **HTTP 403 Forbidden**.
- So sánh token bằng thuật toán thời gian hằng số (`CryptographicOperations.FixedTimeEquals`) để chống tấn công Timing Attack. Không ghi token vào log.
- **Endpoint `/api/health`** được bảo toàn 100%, không yêu cầu token để Docker daemon và CI/CD deploy runner hoạt động ổn định.

### Quản lý Secret Files
- Thư mục `monitoring/secrets/` được cấu hình trong `.gitignore`:
  - `monitoring/secrets/*`
  - `!monitoring/secrets/*.example`
- Git chỉ theo dõi các file mẫu (`.example`) chứa placeholder, không chứa secret thật:
  - `monitoring/secrets/metrics_scrape_token.example`
  - `monitoring/secrets/alertmanager_webhook_token.example`
- File thật trên máy local/server được tạo từ file mẫu và được mount read-only vào container tương ứng.

---

## 3. Cấu hình Cảnh báo Realtime (Alertmanager Webhook -> SignalR -> Admin Web)

- Sử dụng cơ chế native webhook của Alertmanager v0.27.0:
  ```yaml
  receivers:
    - name: 'webhook-admin'
      webhook_configs:
        - url: http://api:5012/api/system-alerts/webhook
          send_resolved: true
          http_config:
            authorization:
              type: Bearer
              credentials_file: /etc/alertmanager/secrets/alertmanager_webhook_token
  ```
- Alertmanager đọc secret token trực tiếp từ file mount `/etc/alertmanager/secrets/alertmanager_webhook_token`.
- API endpoint `POST /api/system-alerts/webhook` xác thực Bearer token an toàn và broadcast cảnh báo qua SignalR (`/hubs/system-alerts`) tới các Admin đang đăng nhập.
- Giao diện Admin (`admin-users.html`) hiển thị toast cảnh báo (Warning / Critical / Resolved) và cập nhật số lượng cảnh báo trên chuông theo `fingerprint`.

---

## 4. Hướng dẫn Khởi chạy Monitoring Stack (Docker Compose)

Monitoring stack được tách riêng vào file overlay `docker-compose.monitoring.yml` để không làm nặng môi trường phát triển tính năng thông thường.

### Bước 1: Chuẩn bị Secret files cục bộ
Sao chép các file mẫu thành file thật nếu chưa có:
```bash
cp monitoring/secrets/metrics_scrape_token.example monitoring/secrets/metrics_scrape_token
cp monitoring/secrets/alertmanager_webhook_token.example monitoring/secrets/alertmanager_webhook_token
```
*(Điền token cào `metrics_scrape_token` khớp với cấu hình `Metrics:ScrapeToken` trong `appsettings.json` hoặc file `.env`)*

### Bước 2: Khởi động hệ thống kèm Monitoring
```bash
docker compose -f docker-compose.yml -f docker-compose.monitoring.yml up -d
```

### Bước 3: Truy cập các giao diện quản trị (Localhost)
- **Grafana Dashboard**: [http://localhost:3000](http://localhost:3000)
  - Tài khoản mặc định: `admin` / `admin`
  - Dashboard có sẵn: **S-43: Giám sát Hệ thống Quá tải (System Overload Overview)**
- **Prometheus UI**: [http://localhost:9090](http://localhost:9090)
  - Kiểm tra Targets: `Status -> Targets` (Target `event-ticket-api` trạng thái UP)
  - Kiểm tra Alert Rules: `Alerts`
- **Alertmanager UI**: [http://localhost:9093](http://localhost:9093)
- **API Metrics**: [http://localhost:5012/metrics](http://localhost:5012/metrics)
  *(Cần header `Authorization: Bearer <token>`)*

---

## 5. Quy tắc Cảnh báo (Alert Rules)

| Quy tắc Alert | Mức độ | Điều kiện PromQL | Cửa sổ | Ý nghĩa |
| :--- | :--- | :--- | :--- | :--- |
| `Http5xxErrorRateWarning` | warning | `5xx / Total > 2%` (Traffic > 5 req/s) | 2 phút | Hệ thống bắt đầu xuất hiện lỗi máy chủ |
| `Http5xxErrorRateCritical` | critical | `5xx / Total > 5%` (Traffic > 5 req/s) | 1 phút | Máy chủ gặp sự cố nghiêm trọng |
| `HttpLatencyP95Warning` | warning | `p95 > 1.0s` | 3 phút | Thời gian phản hồi chậm |
| `HttpLatencyP95Critical` | critical | `p95 > 2.5s` | 1 phút | Thời gian phản hồi rất chậm, nguy cơ nghẽn |
| `SeatHoldConflictsRateWarning` | warning | `rate > 10 conflicts/phút` | 2 phút | Lưu lượng tranh chấp ghế tăng cao |
| `SeatHoldConflictsRateCritical` | critical | `rate > 50 conflicts/phút` | 1 phút | Tranh chấp ghế cực lớn (sự kiện hot/bot spam) |
| `PaymentWebhooksRejectedWarning` | warning | `increase > 3 lần` | 5 phút | Webhook bị từ chối gia tăng |
| `PaymentWebhooksRejectedCritical` | critical | `increase > 10 lần` | 5 phút | Nghi vấn lỗi chữ ký hoặc giả mạo webhook |

---

## 6. Kiểm thử Tự động (Automated Testing)

Chạy bộ test tự động của S-43:
```powershell
dotnet test src/tests/EventTicketBooking.Tests/EventTicketBooking.Tests.csproj --filter "FullyQualifiedName~MonitoringMetricsTests"
```

Chạy toàn bộ test suite để đảm bảo không hồi quy:
```powershell
dotnet test EventTicketBooking.sln --no-restore
dotnet format EventTicketBooking.sln --verify-no-changes
```
