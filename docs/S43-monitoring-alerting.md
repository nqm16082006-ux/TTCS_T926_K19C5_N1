# S-43: Giám sát và cảnh báo khi hệ thống quá tải (No-Grafana Architecture)

Tài liệu hướng dẫn vận hành, kiến trúc bảo mật và kiểm thử cho Story S-43: Hệ thống Giám sát & Cảnh báo (Monitoring & Alerting) theo kiến trúc độc lập không phụ thuộc Grafana.

---

## 1. Tổng quan kiến trúc (No-Grafana)

Kiến trúc giám sát S-43 hoạt động theo luồng:
```text
Application API (:5012)
    |
    | /metrics (Bearer Token Scrape)
    v
Prometheus Local (:9090)
    |
    | Đánh giá các Alert Rules định kỳ
    v
Alertmanager Local (:9093)
    |
    | Authenticated Webhook (Bearer Token)
    v
EventTicketBooking API (:5012)
    |
    +--> SystemAlert lưu trữ bền vững trong PostgreSQL / Neon (Lịch sử cảnh báo)
    |
    +--> SystemAlertHub (SignalR realtime broadcast tới Admin)
    |
    v
admin-monitoring.html (Bảng điều khiển Giám sát Quản trị viên)
    - Tóm tắt 5 nhóm chỉ số cốt lõi qua backend API: GET /api/admin/monitoring/summary
    - Lịch sử & lọc cảnh báo từ DB qua backend API: GET /api/admin/monitoring/alerts
```

### Nguyên tắc vận hành cục bộ (Team-Local Monitoring Experience)
- **Không yêu cầu máy chủ giám sát chạy 24/7**: Mỗi lập trình viên có thể tự chạy stack giám sát cục bộ trên máy phát triển.
- **Phạm vi chỉ số Prometheus**: Chỉ số Prometheus cục bộ phản ánh trạng thái phiên bản API chạy trên máy local (`host.docker.internal:5012`).
- **Lịch sử SystemAlert**: Được lưu trữ bền vững trong cơ sở dữ liệu (`SystemAlerts`), đảm bảo tính chính xác và toàn vẹn lịch sử sự cố.
- **SignalR Realtime**: Là tầng tăng cường trải nghiệm thời gian thực cho Admin client kết nối cùng API instance; sau khi reconnect, client tự động refetch từ REST API.

---

## 2. 5 Nhóm Chỉ Số Giám Sát Cốt Lõi (S-43)

Hệ thống giám sát và bảng điều khiển Quản trị viên theo dõi 5 nhóm chỉ số:
1. **HTTP Request Rate (`req/s`)**: Tổng lưu lượng yêu cầu HTTP đến hệ thống (tính trên cửa sổ 2 phút).
2. **Tỉ lệ lỗi máy chủ HTTP 5xx (`%`)**: Tỉ lệ mã phản hồi 5xx trên tổng lưu lượng xử lý.
3. **Thời gian phản hồi p95 (`ms`)**: Độ trễ phân vị 95 (Histogram chuẩn `http_request_duration_seconds`).
4. **Tranh chấp giữ ghế (`conflicts/phút`)**: Ghi nhận xung đột giữ ghế đồng thời (`seat_hold_conflicts_total`).
5. **Webhook thanh toán bị từ chối (`lần/5m`)**: Ghi nhận số webhook bị từ chối do sai chữ ký, dữ liệu trống hoặc bất thường (`payment_webhooks_rejected_total`).

---

## 3. Bảo mật Endpoint `/metrics` & Quản lý Secrets

### Bảo vệ Endpoint `/metrics` (Application-Level Protection)
- Endpoint `/metrics` nằm trên cổng ứng dụng (`5012`).
- Mọi yêu cầu truy cập bắt buộc phải có Header:
  `Authorization: Bearer <METRICS_SCRAPE_TOKEN>`
- Nếu thiếu hoặc sai token: API trả về **HTTP 403 Forbidden**.
- So sánh token bằng thuật toán thời gian hằng số (`CryptographicOperations.FixedTimeEquals`) chống Timing Attack. Tuyệt đối không log token.
- **Endpoint `/api/health`** không yêu cầu token để Docker daemon và CI/CD deploy runner hoạt động ổn định.

### Quản lý Secret Files
- Thư mục `monitoring/secrets/` được cấu hình trong `.gitignore`:
  - `monitoring/secrets/*`
  - `!monitoring/secrets/*.example`
- Git chỉ theo dõi các file mẫu (`.example`) chứa placeholder, không chứa secret thật:
  - `monitoring/secrets/metrics_scrape_token.example`
  - `monitoring/secrets/alertmanager_webhook_token.example`

---

## 4. Hướng dẫn Khởi chạy Monitoring Stack Cục Bộ

### Bước 1: Chuẩn bị Secret files cục bộ
Sao chép các file mẫu thành file thật nếu chưa có:
```bash
cp monitoring/secrets/metrics_scrape_token.example monitoring/secrets/metrics_scrape_token
cp monitoring/secrets/alertmanager_webhook_token.example monitoring/secrets/alertmanager_webhook_token
```

### Bước 2: Khởi động Prometheus & Alertmanager (Không có Grafana)
```bash
docker compose -f docker-compose.monitoring.yml up -d
```

### Bước 3: Truy cập các giao diện và endpoints
- **Admin Monitoring UI**: [http://localhost:5012/admin-monitoring.html](http://localhost:5012/admin-monitoring.html)
- **Prometheus UI**: [http://localhost:9090](http://localhost:9090)
  - Targets: `Status -> Targets` (`event-ticket-api` target tại `host.docker.internal:5012`)
  - Alert Rules: `Alerts`
- **Alertmanager UI**: [http://localhost:9093](http://localhost:9093)
- **API Metrics**: [http://localhost:5012/metrics](http://localhost:5012/metrics)
  *(Kèm header `Authorization: Bearer <token>`)*

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

## 6. Backend Monitoring API (Admin Only)

- **GET `/api/admin/monitoring/summary`**:
  - Yêu cầu quyền: `Admin` (`[RequireRole("Admin")]`).
  - Truy vấn Prometheus local HTTP API (`/api/v1/query`).
  - Nếu Prometheus offline/không phản hồi: trả về `prometheusAvailable = false` và các trường chỉ số là `null` (phía giao diện hiển thị "Chưa có dữ liệu", tuyệt đối không tạo số 0 giả).
- **GET `/api/admin/monitoring/alerts`**:
  - Yêu cầu quyền: `Admin`.
  - Hỗ trợ tham số truy vấn: `limit`, `status` (`firing` / `resolved`), `severity` (`warning` / `critical`), `search`.
  - Trả về danh sách sắp xếp theo thời điểm cập nhật mới nhất.

---

## 7. Kiểm thử Tự động (Automated Testing)

Chạy bộ test S-43:
```powershell
dotnet test src/tests/EventTicketBooking.Tests/EventTicketBooking.Tests.csproj --filter "FullyQualifiedName~AdminMonitoringTests|FullyQualifiedName~SystemAlertsWebhookTests"
```

Chạy toàn bộ test suite để xác minh không hồi quy:
```powershell
dotnet test EventTicketBooking.sln --no-restore
dotnet format EventTicketBooking.sln --verify-no-changes
```
