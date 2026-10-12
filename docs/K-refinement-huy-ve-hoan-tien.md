# K: Refinement cơ chế huỷ vé và hoàn tiền (phục vụ S-45)

| Thông tin | Giá trị |
|---|---|
| Story phục vụ | **S-45** — Khách huỷ vé trước hạn và được hoàn tiền theo chính sách (E-08, Later/Should, ước lượng thô 5 SP) |
| Trạng thái | Tài liệu phân tích & đề xuất — **chưa phải quyết định đã duyệt** |
| Phụ thuộc story | S-25 (vé điện tử), S-27 (email vé), S-32/S-38 (đơn của tôi / vé của tôi), S-49 (nhật ký đơn hàng — đã có) |
| Story liên quan (ngoài phạm vi tài liệu này) | S-46 (nhả ghế sau huỷ), S-47 (huỷ suất & hoàn hàng loạt), S-48 (retry hoàn tiền), S-49 (audit tổng quát) |
| Nhánh khảo sát | `main` tại commit `2dd3ac4` (đã fetch/fast-forward ngày 10/11/2026) |

> **Lưu ý:** Các giá trị `24 giờ`, `90%` và các phương án đánh dấu **[CHỜ PRODUCT]** là đề xuất của nhóm phân tích, chưa được Product phê duyệt. Không coi là yêu cầu chính thức.

---

## 1. Mục tiêu

1. Trả lời 3 điểm "chưa refine" của S-45: **mốc thời gian huỷ (cutoff)**, **tỉ lệ hoàn**, **cổng sandbox có hỗ trợ hoàn tiền không**.
2. Ghi nhận các quyết định nghiệp vụ/kỹ thuật phát sinh thêm để S-45 có đủ điều kiện triển khai trong ~5 SP.
3. Chốt ranh giới với S-46 → S-49 để không phá phạm vi các story kế bên.

---

## 2. Hiện trạng codebase (đã xác minh, có dẫn chứng)

### 2.1 Vòng đời đơn hàng và vé

- `Models/Order.cs`: `OrderStatus = Pending, Paid, Cancelled, Expired, NeedsAttention`. `Order.TotalAmount = Sum(OrderItems.Price)` (VND nguyên, không phí).
- `Models/Ticket.cs`: vé 1:1 với `OrderItem`, **không có trường trạng thái riêng** — hiệu lực vé suy ra từ trạng thái đơn hàng.
- `Models/OrderItem.cs`: có `IsCheckedIn`, `CheckInGate`, `CheckInTime` — dùng để chặn huỷ vé đã vào cửa.
- `Services/Implementations/ExpiredOrderCleanupService.cs` (Task T-52/S-23): job nền quét đơn `Pending` quá hạn → `Expired`, chuyển hold `ACTIVE → EXPIRED`, ghế `HELD → AVAILABLE`, broadcast SignalR `SeatReleased`, có khóa dòng `FOR UPDATE` chống đua webhook.
- `Services/Implementations/PaymentService.cs` (dòng 356–443): khi cổng trả `PaymentStatus.Cancelled` → đơn chuyển `OrderStatus.Cancelled`, `PaymentTransaction.Status = CANCELLED`, huỷ hold, ghế `HELD → AVAILABLE`, ghi audit `ORDER_CANCELLED` / `SEAT_HOLD_CANCELLED`, broadcast `SeatReleased`.

> **Phát hiện quan trọng:** `OrderStatus.Cancelled` **đang được dùng cho "huỷ trong lúc thanh toán"**, *chưa* phản ánh "khách chủ động huỷ đơn đã thanh toán". S-45 cần phân biệt hai lý do huỷ (xem quyết định D5).

### 2.2 Kiểm soát vé/QR (S-25, S-31, S-38)

- `Controllers/TicketCheckInController.cs`:
  - Dòng 253: `if (ticket.Order.Status != OrderStatus.Paid)` → từ chối check-in; dòng 255–259: đơn `Cancelled` → trả lỗi **`TICKET_CANCELLED`**.
  - Dòng 123 (tải manifest): lọc `OrderStatus == OrderStatus.Paid` — vé đơn đã huỷ không có trong manifest soát vé.
- `wwwroot/my-tickets.html` (S-38) + `src/tests/frontend-audit.test.cjs`: đơn `Cancelled`/`Expired` → thẻ vé mờ, nhãn "Đã huỷ", **không hiển thị QR**.
- ⇒ **Khi S-45 chuyển đơn `Paid → Cancelled`, vé tự động mất hiệu lực ở check-in, manifest và QR mà không cần sửa logic check-in.**

### 2.3 Cổng thanh toán

- `Services/Interfaces/IPaymentGateway.cs`: chỉ có `CreatePaymentAsync`, `QueryPaymentStatusAsync`, `VerifyWebhookSignature`, `ParseWebhookData`. **Không có method hoàn tiền.**
- `Services/Implementations/Payment/PayOSPaymentGateway.cs`: adapter PayOS (BaseUrl `https://api-merchant.payos.vn`), chỉ tạo link thanh toán, query trạng thái, xác thực webhook.
- `Services/Implementations/Payment/DevelopmentMockPaymentGateway.cs`: trả về trang `mock-payment.html`, webhook mock bị tắt.
- `Program.cs` dòng 156–158 + `appsettings.json` dòng 29–30: `PaymentSettings:Provider = "Mock"` là cấu hình mặc định; chọn `"PayOS"` thì đăng ký adapter PayOS.

**Kết quả kiểm tra tài liệu PayOS chính thức (payos.vn, ngày 10/11/2026):**

1. **Không có môi trường sandbox/test riêng** — trang `payos.vn/docs/moi-truong-test/` xác nhận payOS chỉ chạy môi trường production, thử bằng giao dịch thật số tiền nhỏ.
2. **Tài liệu API mới nhất (`payos.vn/docs/api/`) không liệt kê endpoint hoàn tiền**: chỉ có tạo/lấy thông tin/**huỷ link thanh toán**, hóa đơn, webhook và **lệnh chi (payouts)**.
3. SDK .NET chính thức `payOSHQ/payos-lib-dotnet` không có hàm refund.

> ⇒ Trả lời điểm refine *"cổng sandbox có hỗ trợ hoàn tiền không"*: **không có sandbox, và theo tài liệu công bố thì payOS không có API hoàn tiền tự động.** S-45 phải mô phỏng luồng hoàn tiền (Mock) và xử lý thủ công cho môi trường thật (xem D7).

### 2.4 Hạ tầng sẵn có để tận dụng

| Hạ tầng | Dẫn chứng | Dùng cho S-45 |
|---|---|---|
| Nhật ký đơn hàng (S-49) | `Models/OrderAuditLog.cs`, `Services/Implementations/OrderAuditService.cs`, action mẫu `ORDER_CANCELLED` | Ghi `ORDER_CANCELLED` (lý do) + action refund mới; `order-details.html` đã render sẵn (`ORDER_CANCELLED → "Hủy đơn hàng"`, dòng 487) |
| Vai trò Kế toán | `admin-users.html` (role `Auditor`), `auditor.html` | Xác nhận hoàn tiền thủ công / đối soát |
| Gửi email | `Interfaces/IEmailService.cs` (`SendTicketEmailAsync` S-27, ...), `Models/EmailFailureLog.cs` | Thêm email thông báo huỷ & hoàn tiền (chưa có) |
| SignalR ghế | `SeatReleased` (xử lý ở `seat-map.html` dòng 589) | Thuộc S-46 |
| Giao diện đơn | `my-orders.html` có tab lọc `Cancelled` (dòng ~133); `order-details.html` `getStatusConfig` (`Cancelled → "Đã huỷ"`, dòng 169) | Hiển thị "Đơn đã huỷ" + tiền hoàn + trạng thái hoàn |
| Rate limit | `Middlewares/TicketRateLimit*` (S-41) | Cân nhắc bảo vệ endpoint huỷ (chống bấm lặp) |

### 2.5 Chính sách công khai đang mâu thuẫn

- `wwwroot/event-detail.html` dòng 644–645: *"Quy định vé & Hoàn tiền — Vé đã mua **không được hoàn hoặc hủy** trừ khi có thông báo chính thức từ Ban tổ chức trước 7 ngày."*
- `wwwroot/seat-map.html` dòng ~502: link chết *"Quy trình đổi / hoàn vé"* (`href="#"`).
- **S-45 công bố sẽ cho huỷ/hoàn theo tỉ lệ → bắt buộc cập nhật nội dung này**, nếu không hệ thống tự mâu thuẫn với trang công bố (xem D9).

### 2.6 API đơn hàng hiện có (`Controllers/OrdersController.cs`)

| Method | Route | Ghi chú |
|---|---|---|
| POST | `/api/v1/orders/showtimes/{showtimeId:guid}` | Tạo đơn từ giữ chỗ (T-37) |
| GET | `/api/v1/orders/{orderId:guid}` | Chi tiết đơn |
| GET | `/api/v1/orders/{orderId:guid}/status` | Trạng thái (polling) |
| GET | `/api/v1/orders/{orderId:guid}/tickets` | Vé của đơn (S-25) |
| POST | `/api/v1/orders/{orderId:guid}/resend-tickets` | Gửi lại email vé (S-27) |
| GET | `/api/v1/orders/{orderId:guid}/audit-logs` | Nhật ký (S-49) |
| GET | `/api/v1/orders/my-orders` | Đơn của tôi (S-32) |

**Chưa có endpoint huỷ đơn/hoàn tiền.**

---

## 3. Bảng quyết định refinement

| # | Điểm cần chốt | Phương án đề xuất | Lý do / căn cứ | Trạng thái |
|---|---|---|---|---|
| **D1** | Mốc thời gian huỷ (cutoff) | Cho huỷ khi `now ≤ Showtime.StartTime − RefundCutoffHours`; mặc định **24 giờ**. Cấu hình theo ưu tiên: Showtime → Event → appsettings (toàn hệ thống) | Dựa trên giờ chiếu có sẵn (`Models/Showtime.cs: StartTime`); đơn giản, test được, không phụ thuộc thời điểm mua; hiện **chưa có trường nào** lưu cutoff | **[CHỜ PRODUCT]** giá trị 24h và việc cấu hình theo từng Showtime/Event |
| **D2** | Tỉ lệ hoàn | Tỉ lệ phẳng cấu hình được `RefundRatePercent`, mặc định **90%**, làm tròn **xuống** VND nguyên; tiền hoàn = tỉ lệ × `Order.TotalAmount` | Đơn hàng dùng VND nguyên; 90% = giữ 10% phí xử lý/giữ chỗ; phẳng → dễ mô tả, dễ hiển thị "số tiền sẽ nhận" trước khi xác nhận | **[CHỜ PRODUCT]** giá trị 90%; tỉ lệ theo bậc thời gian (tiered) để Later |
| **D3** | Phạm vi huỷ | **Huỷ toàn bộ đơn**, không huỷ từng vé | Đơn gắn ghế qua `OrderItem` (1:1 `Seat`); huỷ lẻ kéo theo nhả ghế lẻ, chia tiền hoàn lẻ, QR lẻ — vượt 5 SP | Đề xuất — cần Product xác nhận |
| **D4** | Điều kiện huỷ | Chỉ đơn `Paid`; người yêu cầu là chủ đơn (`ClaimTypes.NameIdentifier`); **chưa vé nào `IsCheckedIn`**; trước cutoff D1; chưa từng huỷ | Check-in đang chặn đơn khác `Paid` (`TicketCheckInController` dòng 253) — đây là lớp bảo vệ bổ sung; `OrderItem.IsCheckedIn` đã có sẵn | Đề xuất |
| **D5** | Trạng thái đơn & lý do huỷ | Giữ `OrderStatus.Cancelled` (frontend đã render sẵn) và **bắt buộc kèm lý do**: bản ghi `Refund` mới + audit phân biệt với `ORDER_CANCELLED` của `system:PaymentWebhook`; phương án gọn: thêm cột `Order.CancelReason` (`PAYMENT_CANCELLED`, `CUSTOMER_REFUND`) | Tránh thêm enum trạng thái (sửa nhiều nơi, rủi ro migration); `OrderAuditLog` đã có `ActorType/Actor/Note`; tiền hoàn nằm ở thực thể riêng | Đề xuất — cần chốt (câu hỏi Q7) |
| **D6** | Chính sách áp dụng | **Phiên bản 1: chính sách thống nhất toàn hệ thống** (cutoff + tỉ lệ đọc từ cấu hình); **chưa** xây giao diện Organizer cấu hình riêng | Giữ đúng 5 SP; cấu hình per-Event chỉ là field dữ liệu, giao diện quản lý để story riêng | Đề xuất |
| **D7** | Cổng thanh toán & hoàn tiền | Mở rộng `IPaymentGateway` bằng `CreateRefundAsync` + cờ `SupportsRefund`: (a) **Mock** mô phỏng thành công/thất bại → demo & test E2E; (b) **PayOS** trả `NotSupported` → Refund ở trạng thái **`ManualPending`**, vai trò **Auditor** xác nhận "đã chuyển khoản thủ công"; (c) spike riêng hỏi payOS support về API hoàn tiền (không cam kết trong S-45) | Căn cứ mục 2.3: payOS **không có sandbox và không có API refund công bố**; mock gateway đã có pattern sẵn; role Auditor đã có | Đề xuất — cần Product chấp nhận "hoàn tiền thủ công cho môi trường thật" |
| **D8** | Hiển thị cho khách | Đơn đã huỷ nằm trong tab "Đã huỷ" của `my-orders.html`/`order-details.html`; hiển thị **số tiền hoàn dự kiến/đã nhận** + trạng thái hoàn; vé đã huỷ không có QR (đã có sẵn từ S-38) | Giao diện đã có sẵn, chỉ cần bổ sung trường hiển thị | Đề xuất |
| **D9** | Chính sách công khai | Cập nhật `event-detail.html` (dòng 644–645) sang văn bản động: *"Có thể huỷ trước {cutoff} giờ so với giờ chiếu, hoàn {rate}% số tiền đã thanh toán"*; thay/bỏ link chết "Quy trình đổi / hoàn vé" ở `seat-map.html` | Loại bỏ mâu thuẫn pháp lý/hình ảnh (mục 2.5) | Bắt buộc — cần Product duyệt văn bản |
| **D10** | Nhả ghế sau huỷ | **Không thực hiện trong S-45** (thuộc **S-46**) | Ranh giới phạm vi đã định (mục 6) | Đã chốt ranh giới — xem rủi ro R2 |

---

## 4. Luồng nghiệp vụ đề xuất

### 4.1 Luồng chính (Mock — môi trường development/demo)

```text
Khách mở Đơn hàng → bấm "Huỷ & hoàn tiền"
  → GET xem trước số tiền hoàn (cutoff, tỉ lệ, tiền nhận)
  → Xác nhận
  → POST /api/v1/orders/{id}/cancel  [RequireRole]
       Kiểm tra (trong transaction, khóa dòng đơn):
         1. Người gọi là chủ đơn?
         2. order.Status == Paid?
         3. now ≤ StartTime − CutoffHours?
         4. Mọi OrderItem.IsCheckedIn == false?
         5. Đơn chưa có refund đang hoạt động (idempotent)?
       → order.Status = Cancelled; UpdatedAt = now
       → tạo bản ghi Refund: amount = floor(TotalAmount × rate / 100)
       → gọi gateway.CreateRefundAsync (Mock → SUCCEEDED; mô phỏng lỗi → FAILED)
       → ghi OrderAuditLog: ORDER_CANCELLED (USER, lý do khách huỷ) + REFUND_REQUESTED/REFUND_SUCCEEDED
  → Response: trạng thái đơn + số tiền hoàn + trạng thái hoàn
  → Frontend: thẻ sang "Đã huỷ", hiển thị tiền hoàn; email thông báo (method mới của IEmailService)
  → Vé: tự mất hiệu lực vì đơn ≠ Paid (check-in, manifest, QR — không cần sửa code)
```

### 4.2 Luồng môi trường thật (PayOS — chưa có API refund)

```text
Sau bước tạo Refund → gateway.SupportsRefund == false
  → Refund.status = ManualPending ("Chờ đối soát thủ công")
  → Auditor mở danh sách hoàn tiền ở cổng kế toán → xác nhận đã chuyển khoản
  → Refund.status = Succeeded (kèm actor Auditor, thời điểm, ghi chú)
  → Khách thấy "Đang xử lý hoàn tiền" → "Đã hoàn tiền"
```

### 4.3 Trạng thái và quy tắc chuyển

**Đơn hàng (không thêm giá trị trạng thái mới):**

```text
Paid ──(khách huỷ trước cutoff, hợp lệ)──▶ Cancelled
Paid ──(sau cutoff / đã check-in)───────▶ giữ nguyên, trả lỗi kèm lý do
Pending ──(job T-52)────────────────────▶ Expired              [đã có]
Pending ──(cổng huỷ thanh toán)────────▶ Cancelled            [đã có, PaymentService]
```

**Yêu cầu hoàn tiền (bảng `Refunds` — mới):**

```text
Requested ──▶ Succeeded                      (Mock gateway mô phỏng thành công)
Requested ──▶ ManualPending ──▶ Succeeded    (PayOS, Auditor xác nhận)
Requested ──▶ Failed                         (mô phỏng lỗi; retry thuộc S-48)
Requested/ManualPending ──▶ Rejected         (tranh chấp: đã check-in, trễ cutoff khi kiểm lại)
```

Ràng buộc: **tối đa 1 bản ghi Refund mỗi đơn** (unique theo `OrderId`) → chống double-refund.

---

## 5. Trường hợp lỗi, tranh chấp và xử lý lặp

| Tình huống | Xử lý đề xuất |
|---|---|
| Huỷ lần 2 / bấm nút nhiều lần | Idempotent: nếu đơn đã `Cancelled` và có Refund → trả lại kết quả hiện tại (200) hoặc 409 kèm thông báo "Đơn đã được huỷ"; **không tạo Refund lần 2** (unique `OrderId`) |
| Huỷ trùng với webhook thanh toán thành công | Khóa dòng đơn (`FOR UPDATE` như `ExpiredOrderCleanupService`) trong transaction; nếu webhook ghi `Paid` trước → lần huỷ thấy `Paid` và tiến hành; nếu sau → webhook phải từ chối đơn `Cancelled` (cần kiểm tra điểm này khi implement) |
| Huỷ trùng thời điểm check-in | Kiểm tra `IsCheckedIn` trong cùng transaction; check-in cập nhật có điều kiện `!IsCheckedIn` (S-31) là lớp bảo vệ cuối |
| Trễ cutoff giữa lúc xem trước và lúc xác nhận | Tính lại cutoff tại thời điểm xử lý server; nếu trễ → từ chối kèm lý do `AFTER_CUTOFF` |
| Vé đã check-in (một phần) | Từ chối toàn bộ huỷ, lý do `TICKET_ALREADY_CHECKED_IN`, nêu rõ ghế/vé |
| Mock gateway trả refund thất bại | Refund `Failed` → đơn vẫn `Cancelled`? **Cần chốt:** đề xuất đơn giữ `Cancelled` nhưng Refund `Failed` để hiển thị "Hoàn tiền thất bại — liên hệ hỗ trợ"; retry thuộc S-48 |
| Gửi email thông báo lỗi | Ghi `EmailFailureLog` (đã có), không làm thất bại lệnh huỷ; retry email không chặn S-45 |
| Số tiền làm tròn | `floor(TotalAmount × rate / 100)`; luôn `0 < amount ≤ TotalAmount` (nếu rate = 100 thì amount = TotalAmount) |
| Rate limit / spam endpoint huỷ | Cân nhắc dùng `TicketRateLimit` (S-41) hoặc rely trên idempotency |

---

## 6. Ranh giới phạm vi


**K (tài liệu này) chỉ phân tích, đề xuất và ghi nhận quyết định.** Không sửa code backend/frontend, không migration, không API, không test.

| Thuộc S-45 (sau khi refine) | KHÔNG thuộc S-45 |
|---|---|
| Endpoint huỷ đơn + xem trước tiền hoàn | **S-46**: nhả ghế `SOLD → AVAILABLE` sau huỷ |
| Bảng `Refunds`, tính tiền hoàn, rule cutoff | **S-47**: huỷ suất chiếu & hoàn tiền hàng loạt |
| Mở rộng `IPaymentGateway` (Mock refund, PayOS NotSupported) | **S-48**: retry hoàn tiền thất bại |
| UI huỷ & tiền hoàn ở my-orders/order-details | **S-49**: audit tổng quát (đã có — chỉ **tái sử dụng** `IOrderAuditService`) |
| Cập nhật văn bản chính sách `event-detail.html` | Giao diện Organizer cấu hình chính sách riêng |
| Email thông báo huỷ/hoàn tiền (method mới `IEmailService`) | Cổng Auditor hoàn chỉnh (chỉ cần danh sách tối thiểu) |

---

## 7. Các câu hỏi Product cần trả lời

| # | Câu hỏi | Đề xuất mặc định |
|---|---|---|
| Q1 | Mốc cutoff là **24 giờ** trước giờ chiếu? Áp dụng chung hay cấu hình từng suất/sự kiện? | 24h, cấu hình được, mặc định toàn hệ thống 24h |
| Q2 | Tỉ lệ hoàn **90%**? Làm tròn xuống? Có phân bậc theo thời gian không? | 90%, làm tròn xuống, phẳng |
| Q3 | Chỉ huỷ **toàn bộ đơn** — có chấp nhận không huỷ từng vé không? | Toàn bộ đơn |
| Q4 | Sau khi huỷ, ghế có **bán lại được không**? (Hiện trạng: giữ nguyên `SOLD` tới S-46 → mất tồn kho trong lúc này) | Xác nhận tách S-46 và chấp nhận tồn kho tạm |
| Q5 | Môi trường thật hoàn tiền **thủ công qua Auditor** (vì payOS không có API refund) — có phù hợp demo/khóa học không? | Chấp nhận: Mock cho demo, ManualPending cho production |
| Q6 | Văn bản chính sách mới trên `event-detail.html` (thay "không được hoàn hoặc hủy") do ai duyệt? | Product soạn, đối chiếu D1/D2 |
| Q7 | Cách phân biệt "khách huỷ sau thanh toán" với "huỷ do thanh toán thất bại" — dùng `Refund` + audit note hay thêm cột `Order.CancelReason`? | Thêm cột `Order.CancelReason` tối giản (`PAYMENT_CANCELLED`, `CUSTOMER_REFUND`) |
| Q8 | Organizer/Admin có được huỷ giúp khách không? | Ngoài phạm vi S-45 |
| Q9 | Đơn `Pending` (chưa thanh toán) có cần "huỷ" không? | Không — đã có job T-52; S-45 chỉ áp dụng đơn `Paid` |

---

## 8. Điều kiện để S-45 sẵn sàng triển khai (Definition of Ready)

1. Product trả lời Q1–Q7; các quyết định D1–D9 đổi trạng thái từ **[CHỜ PRODUCT]** → **Đã duyệt**.
2. Văn bản chính sách công khai (D9) được duyệt kèm AC hiển thị "số tiền sẽ nhận".
3. AC viết cụ thể cho: điều kiện từ chối (sau cutoff, đã check-in, không phải chủ đơn) kèm mã lỗi/anh văn trả về; idempotency; trạng thái hiển thị của Refund.
4. Chốt tách S-46 (nhả ghế) và mức tồn kho chấp nhận được (R2).
5. Xác nhận Mock gateway là kênh demo hoàn tiền chính (S-45 không đợi API thật của payOS).

---

## 9. Ước lượng sơ bộ & rủi ro ảnh hưởng mức 5 SP

**Ước lượng (sau refine, tham chiếu các story đã làm):**

| Hạng mục | Ước lượng |
|---|---|
| Backend: entity `Refunds` + migration + tính tiền/cutoff + endpoint cancel & preview + idempotency + audit | ~2 SP |
| Mở rộng `IPaymentGateway` + Mock refund + PayOS NotSupported + email method | ~1 SP |
| Frontend: my-orders/order-details (nút huỷ, dialog xác nhận tiền hoàn, badge trạng thái hoàn) + cập nhật chính sách | ~1 SP |
| Unit/E2E test (rule cutoff, tiền hoàn, tranh chấp, idempotency) | ~0.5–1 SP |
| **Tổng** | **≈ 4.5–5 SP** |

**Rủi ro có thể khiến S-45 vượt 5 SP:**

| # | Rủi ro | Ảnh hưởng | Giảm thiểu |
|---|---|---|---|
| R1 | Nghiên cứu/payOS yêu cầu hoàn tiền API thật | Vượt phạm vi nếu cố tự động hoàn | D7: Mock + thủ công; spike tách riêng |
| R2 | S-46 (nhả ghế) chưa làm → ghế đã hoàn vẫn `SOLD`, không bán lại được | Mất tồn kho; khách thắc mắc | Chốt Q4; tách rõ trong AC S-45 |
| R3 | Thêm nhiều trạng thái đơn mới (ngoài `Cancelled`) | Sửa nhiều điểm frontend/check-in/test hiện có | D5: giữ `Cancelled` + `CancelReason`/audit |
| R4 | Email hoàn tiền + retry gửi (`EmailFailureLog`) | Phình scope | Chỉ 1 method đơn giản; retry để S-48 |
| R5 | Cấu hình per-Event/Showtime + giao diện quản lý | Vượt 5 SP | D6: cấu hình toàn hệ thống ở phiên bản 1 |
| R6 | Chồng lấn với S-46/S-47/S-48 nếu gộp | Vô kỷ luật phạm vi | Bảng mục 6 đã chốt |

---

*Tài liệu này chỉ là đầu vào refinement. Việc sửa code/migration/API/test sẽ thực hiện ở bước triển khai S-45 sau khi Product xác nhận các câu hỏi mục 7.*








