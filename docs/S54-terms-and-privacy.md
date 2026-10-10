# S-54: Người dùng đồng ý điều khoản và chính sách riêng tư, thu hồi được

**Mã Story:** TTKN-121 / TTKN-10  
**Ước lượng:** 2 Story Points  
**Tuân thủ:** Nghị định 13/2023/NĐ-CP về Bảo vệ dữ liệu cá nhân  

---

## 1. Yêu cầu & Tiêu chí chấp nhận (Acceptance Criteria)

| STT | Tiêu chí | Mô tả chi tiết |
|---|---|---|
| **AC1** | **Bắt buộc tích đồng ý khi đăng ký** | Khi đăng ký (qua Email & Mật khẩu hoặc Google), người dùng bắt buộc phải tích chọn ô đồng ý Điều khoản dịch vụ và Chính sách riêng tư. Nếu không tích, form từ chối gửi và máy chủ trả về `HTTP 400 Bad Request`. Người dùng có thể bấm vào liên kết để đọc toàn văn điều khoản. |
| **AC2** | **Lưu phiên bản và thời điểm** | Khi người dùng đồng ý, hệ thống lưu trữ phiên bản chính sách (`TermsVersion`, mặc định `"v1.0"`), thời điểm chấp thuận (`TermsAcceptedAt`), và trạng thái nhận email tiếp thị ban đầu (`MarketingEmailOptIn = true`). |
| **AC3** | **Đổi phiên bản thì lần đăng nhập sau phải đồng ý lại** | Khi quản trị viên hoặc hệ thống cập nhật phiên bản chính sách (ví dụ lên `"v1.1"`), nếu tài khoản chưa đồng ý phiên bản mới nhất, lần đăng nhập tiếp theo sẽ bị chặn với mã `HTTP 428 Precondition Required`. Giao diện hiển thị modal thông báo cập nhật; sau khi người dùng xác nhận (`acceptCurrentTerms = true`), hệ thống cập nhật phiên bản và cấp JWT token đăng nhập. |
| **AC4** | **Thu hồi được & không nhận email tiếp thị nữa** | Người dùng có thể vào trang **Cài đặt riêng tư** (`privacy-settings.html`) để bấm nút **Thu hồi quyền nhận email tiếp thị**. Hệ thống chuyển `MarketingEmailOptIn = false`. Mọi luồng gửi email tiếp thị / quảng bá sau đó đều bị chặn (HTTP 403 Forbidden). Người dùng có quyền kích hoạt lại bất kỳ lúc nào. |

---

## 2. Kiến trúc & Thiết kế kỹ thuật

### 2.1 Cơ sở dữ liệu (Bảng `Users`)
- `TermsVersion` (`text`, NOT NULL, default `""`): Phiên bản điều khoản người dùng đã chấp thuận.
- `TermsAcceptedAt` (`timestamp with time zone`, NULL): Thời điểm chính xác người dùng đồng ý.
- `MarketingEmailOptIn` (`boolean`, NOT NULL, default `false`): Trạng thái cho phép gửi email tiếp thị.

### 2.2 Chính sách điều khoản (`TermsPolicy`)
- Quản lý phiên bản chính sách hiện hành (`CurrentVersion`, mặc định `"v1.0"`).
- Hỗ trợ cấu hình động qua `appsettings.json` (`TermsPolicy:CurrentVersion`) và runtime updates.
- Sử dụng cơ chế `AsyncLocal` bảo đảm an toàn luồng (thread-safety), ngăn chặn xung đột dữ liệu giữa các luồng kiểm thử chạy song song.
- Phương thức `IsCurrent(User user)` kiểm tra xem người dùng đã đồng ý đúng phiên bản mới nhất chưa.
- Phương thức `AcceptCurrent(User user)` cập nhật phiên bản và thời điểm UTC hiện tại.

### 2.3 Danh sách API Endpoints

| Phương thức | Đường dẫn API | Xác thực | Mô tả |
|---|---|---|---|
| `GET` | `/api/v1/user/terms/policy` | Public | Lấy toàn văn điều khoản, chính sách và phiên bản hiện hành. |
| `GET` | `/api/v1/user/terms/status` | `[RequireRole]` | Lấy thông tin phiên bản đã đồng ý, thời điểm và trạng thái tiếp thị. |
| `POST` | `/api/v1/user/terms/revoke-marketing` | `[RequireRole]` | Thu hồi quyền nhận email tiếp thị (`MarketingEmailOptIn = false`). |
| `POST` | `/api/v1/user/terms/opt-in-marketing` | `[RequireRole]` | Kích hoạt lại quyền nhận email tiếp thị (`MarketingEmailOptIn = true`). |
| `POST` | `/api/v1/user/terms/accept` | `[RequireRole]` | Người dùng xác nhận đồng ý phiên bản hiện hành. |
| `POST` | `/api/v1/user/terms/send-marketing-test` | `[RequireRole]` | Kiểm thử gửi email tiếp thị: nếu đã thu hồi sẽ bị chặn (403 Forbidden). |
| `POST` | `/api/v1/user/terms/admin/change-policy-version` | `[RequireRole("Admin")]` | Đổi phiên bản chính sách toàn hệ thống (phục vụ nâng cấp/kiểm thử). |
| `POST` | `/api/auth/register` | Public | Đăng ký tài khoản (bắt buộc `acceptTerms: true`). |
| `POST` | `/api/auth/login` | Public | Đăng nhập tài khoản (kiểm tra điều khoản hiện hành; hỗ trợ `acceptCurrentTerms: true`). |

---

## 3. Giao diện người dùng (Frontend)

1. **Trang Đăng ký (`register.html`):**
   - Checkbox bắt buộc: "Tôi đồng ý với Điều khoản dịch vụ và Chính sách riêng tư của EventPulse."
   - Modal xem toàn văn Điều khoản & Chính sách kèm ngày hiệu lực và số hiệu phiên bản.
2. **Trang Đăng nhập (`login.html`):**
   - Khi nhận mã lỗi HTTP 428: tự động hiển thị Modal thông báo nâng cấp phiên bản điều khoản kèm tóm tắt nội dung thay đổi và nút "Tôi đồng ý & Đăng nhập".
3. **Trang Cài đặt riêng tư (`privacy-settings.html`):**
   - Tra cứu phiên bản đã đồng ý, thời điểm chấp thuận, phiên bản hiện hành của hệ thống.
   - Thẻ điều khiển trạng thái Email tiếp thị: Nút bấm chuyển đổi **Thu hồi** / **Kích hoạt lại**.
   - Công cụ kiểm tra thực tế: Thử gửi email tiếp thị để trực tiếp chứng minh hệ thống chặn gửi khi người dùng đã thu hồi quyền.
   - Công cụ Demo dành cho Tester / Admin: Thử nghiệm đổi phiên bản hệ thống.
4. **Trang Đơn hàng của tôi (`my-orders.html`):**
   - Bổ sung liên kết điều hướng trực tiếp đến "Cài đặt riêng tư".

---

## 4. Hướng dẫn kiểm thử

### 4.1 Kiểm thử tự động (Unit Tests)
Chạy bộ kiểm thử chuyên biệt 11 test cases cho Story S-54:
```powershell
$env:PATH = "C:\Users\ADMIN\AppData\Local\Microsoft\dotnet;" + $env:PATH
dotnet test src/tests/EventTicketBooking.Tests --filter FullyQualifiedName~UserTermsS54Tests
```

Chạy toàn bộ test suite dự án (249 tests):
```powershell
dotnet test
```

### 4.2 Kiểm thử End-to-End (E2E Script)
Khởi động backend API:
```powershell
dotnet run --project src/EventTicketBooking.Api
```

Tại một cửa sổ PowerShell khác, chạy script E2E:
```powershell
.\test_e2e_s54.ps1
```

---

## 5. Kịch bản Demo thực tế

1. **Kiểm tra đăng ký:**
   - Mở `http://localhost:5012/register.html`.
   - Điền thông tin nhưng **bỏ tích checkbox điều khoản** -> Bấm Tiếp tục -> Báo lỗi: *"Bạn cần đồng ý với Điều khoản dịch vụ và Chính sách riêng tư để đăng ký."*
   - Bấm vào chữ "Điều khoản dịch vụ" -> Mở modal xem toàn văn chính sách -> Bấm "Đồng ý điều khoản" -> Checkbox tự động được tích -> Tiếp tục đăng ký thành công.
2. **Kiểm tra lưu phiên bản & thời điểm:**
   - Đăng nhập vào hệ thống -> Mở `http://localhost:5012/privacy-settings.html`.
   - Xem thông tin: Phiên bản đã đồng ý `v1.0`, thời điểm chính xác theo giờ Việt Nam.
3. **Kiểm tra thu hồi quyền nhận email tiếp thị:**
   - Trong trang `privacy-settings.html`, bấm **Thu hồi quyền nhận email tiếp thị**.
   - Trạng thái chuyển sang *"Đã thu hồi quyền"*.
   - Bấm nút **"Thử gửi email tiếp thị ngay"** -> Hộp thoại thông báo màu đỏ: *"⛔ Hệ thống đã chặn gửi email tiếp thị (HTTP 403 Forbidden)"*.
   - Bấm **"Đăng ký nhận lại email tiếp thị"** -> Thử gửi lại -> Thông báo màu xanh: *"✅ Gửi email tiếp thị thành công (HTTP 200 OK)"*.
4. **Kiểm tra đổi phiên bản chính sách & đăng nhập lại:**
   - Ở phần Demo bên phải trang `privacy-settings.html`, nhập `v1.1` rồi bấm **"Đổi phiên bản hệ thống"**.
   - Bấm nút **Đăng xuất**.
   - Thực hiện đăng nhập lại tại `login.html` -> Modal thông báo *"Cập nhật Điều khoản dịch vụ phiên bản v1.1"* xuất hiện.
   - Bấm *"Tôi đồng ý & Đăng nhập"* -> Đăng nhập thành công vào hệ thống.
   - Quay lại trang `privacy-settings.html` kiểm tra: phiên bản của tài khoản đã được nâng cấp lên `v1.1` cùng thời điểm mới!
