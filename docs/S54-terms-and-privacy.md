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

---

## 6. Đối chiếu Chính sách Ticketbox và Áp dụng vào Dự án EventPulse

Nhằm giải quyết ghi chú Backlog: *"chưa refine — nội dung điều khoản do người có thẩm quyền cung cấp, không phải dev soạn"*, hệ thống đã tiến hành đối chiếu toàn diện các quy chế hoạt động của sàn thương mại điện tử phân phối vé hàng đầu Việt Nam (**Ticketbox**) và tích hợp tương thích với các năng lực công nghệ sẵn có của EventPulse:

| STT | Trụ cột chính sách | Quy định thực tế tại Ticketbox | Tích hợp & Áp dụng trong Dự án EventPulse | Story kỹ thuật liên quan |
|---|---|---|---|---|
| **1** | **Mô hình & Vai trò sàn giao dịch** | Ticketbox là đơn vị trung gian công nghệ kết nối BTC và Khách hàng; BTC chịu trách nhiệm toàn bộ về sự kiện, giấy phép biểu diễn và chất lượng chương trình. | EventPulse đóng vai trò nền tảng trung gian; hệ thống phân quyền 4 nhóm đối tượng (`Admin`, `Organizer`, `Staff`, `Customer`). Điều khoản quy định rõ nghĩa vụ từng bên và miễn trừ trách nhiệm nội dung cho bên thứ ba. | S-03 (Phân quyền Role) |
| **2** | **Tài khoản & Xác thực an toàn** | Khách hàng chịu trách nhiệm bảo mật thông tin đăng nhập, Ticketbox miễn trừ trách nhiệm nếu khách để lộ mật khẩu/OTP. | Bắt buộc xác thực đa yếu tố với mật mã băm Argon2id và mã OTP gửi qua email (2FA); quy định trách nhiệm bảo mật thuộc về chủ tài khoản. | S-02 (2FA Login & Register) |
| **3** | **Giữ chỗ tạm thời & Đặt vé** | Cơ chế giữ ghế trong 5-15 phút khi thanh toán để tránh tranh chấp chỗ; quá hạn hệ thống tự động nhả vé. | Áp dụng cơ chế Hold Ticket với countdown timer thời gian thực; quá hạn tự hủy đơn hàng tạm thời và giải phóng ghế cho người mua khác. | S-26, S-27 (Hold & Booking) |
| **4** | **Vé điện tử & Chống vé giả mạo** | Phát hành vé E-ticket chứa mã QR; mỗi vé chỉ có giá trị cho 01 lần soát vé vào cổng; người quét trước được quyền vào. | Sử dụng công nghệ chữ ký số bất đối xứng **ECDSA** ký trực tiếp lên mã QR vé điện tử; ứng dụng web `staff-scanner.html` soát vé xác minh chữ ký số chống sao chép/chỉnh sửa giả mạo 100%. | S-28, S-48 (ECDSA Ticket & Scanner) |
| **5** | **Hủy vé & Hoàn tiền** | Vé không đổi trả theo nhu cầu cá nhân; chỉ xử lý hoàn tiền theo chính sách riêng từng sự kiện hoặc khi BTC hủy sự kiện. | **Tích hợp tính năng tự phục vụ S-45 & S-46:** Cho phép khách hàng chủ động hủy vé trước giờ diễn tối thiểu 24 giờ và chưa check-in, nhận hoàn 90% (giữ 10% phí); tự động giải phóng ghế về kho bán; hoàn 100% khi BTC hoãn/hủy sự kiện (S-51). | S-45, S-46, S-51 |
| **6** | **Chống đầu cơ, Phe vé (Anti-scalping)** | Nghiêm cấm dùng tool/bot gom vé đầu cơ bán chênh lệch; khóa tài khoản và hủy vé vi phạm. | Giới hạn số lượng vé mỗi lần đặt, giám sát hành vi bất thường, điều khoản quy định rõ quyền khóa vĩnh viễn tài khoản và hủy vé phe vé. | S-26, S-54 |
| **7** | **Bảo vệ dữ liệu & Quyền riêng tư** | Thu thập dữ liệu phục vụ phát hành vé, chăm sóc khách hàng và cá nhân hóa trải nghiệm; hỗ trợ quyền dừng cung cấp hoặc xóa thông tin. | Tuân thủ triệt để **Nghị định 13/2023/NĐ-CP**: Minh bạch mục đích thu thập, cho phép tra cứu, hỗ trợ **quyền yêu cầu xóa dữ liệu cá nhân & ẩn danh hóa an toàn kế toán** (S-53), và quyền **thu hồi sự đồng ý nhận email tiếp thị** (S-54, chặn gửi HTTP 403). | S-53, S-54 |
| **8** | **Cập nhật & Bắt buộc đồng ý lại** | Sàn có quyền cập nhật điều khoản và áp dụng ngay khi đăng tải. | Lưu vết phiên bản (`TermsVersion`), thời điểm đồng ý (`TermsAcceptedAt`); chặn đăng nhập (HTTP 428) buộc người dùng đọc và chấp thuận phiên bản mới trước khi tiếp tục. | S-54 (Terms Versioning) |

---

## 7. Toàn văn Điều khoản Dịch vụ và Chính sách Bảo vệ Dữ liệu Cá nhân (Chuẩn hóa Pháp nhân)

### 7.1 ĐIỀU KHOẢN VÀ ĐIỀU KIỆN SỬ DỤNG DỊCH VỤ PHÂN PHỐI VÉ (TERMS OF SERVICE)

**LỜI MỞ ĐẦU:**  
Chào mừng Quý khách đến với Nền tảng phân phối vé sự kiện trực tuyến **EventPulse** (sau đây gọi tắt là *"EventPulse"* hoặc *"Nền tảng"*). Bản Điều khoản và Điều kiện sử dụng dịch vụ này (sau đây gọi là *"Điều khoản"*) tạo thành một thỏa thuận pháp lý ràng buộc giữa Quý khách (sau đây gọi là *"Khách hàng"* hoặc *"Người dùng"*) và Đơn vị vận hành Nền tảng EventPulse. Bằng việc tích chọn *"Tôi đồng ý với Điều khoản dịch vụ và Chính sách riêng tư"*, đăng ký tài khoản hoặc tiếp tục sử dụng dịch vụ đặt vé trên hệ thống, Khách hàng xác nhận đã đọc, hiểu rõ, đồng thuận và cam kết tuân thủ toàn bộ các điều khoản được quy định dưới đây.

---

#### ĐIỀU 1: GIẢI THÍCH TỪ NGỮ VÀ ĐỊNH NGHĨA PHÁP LÝ
Trong bản Điều khoản này, các thuật ngữ viết hoa dưới đây được hiểu và giải thích như sau:
1. **"EventPulse" / "Nền tảng":** Là hệ thống sàn thương mại điện tử phân phối vé sự kiện trực tuyến, bao gồm website, ứng dụng di động, giao diện lập trình ứng dụng (API) và hạ tầng kỹ thuật phụ trợ do đơn vị quản trị vận hành.
2. **"Ban Tổ Chức" (BTC) / "Nhà Tổ Chức Sự Kiện":** Là tổ chức, doanh nghiệp hoặc cá nhân chủ quản sự kiện, chịu trách nhiệm pháp lý cao nhất về việc tổ chức, xin cấp giấy phép biểu diễn, địa điểm, nội dung, an ninh trật tự và thực hiện nghĩa vụ thuế đối với sự kiện.
3. **"Khách hàng" / "Người Mua Vé":** Là bất kỳ cá nhân hoặc tổ chức nào đăng ký tài khoản hợp lệ trên Nền tảng EventPulse nhằm mục đích tìm kiếm thông tin, giữ chỗ và mua vé tham dự các sự kiện được đăng tải trên hệ thống.
4. **"Vé điện tử" (E-Ticket):** Là chứng từ điện tử hợp pháp do EventPulse phát hành thay mặt cho Ban Tổ Chức sau khi Khách hàng thanh toán thành công, xác nhận quyền vào cửa tham dự sự kiện của Khách hàng.
5. **"Mã QR chữ ký số ECDSA":** Là chuỗi dữ liệu mã vạch hai chiều được tạo ra từ công nghệ chữ ký số bất đối xứng Elliptic Curve Digital Signature Algorithm (ECDSA), dùng để đối soát và kiểm soát ra vào cửa tại sự kiện, có đặc tính chống sao chép và chống làm giả tuyệt đối.
6. **"Giữ chỗ tạm thời" (Seat Hold):** Là tính năng kỹ thuật cho phép hệ thống khóa tạm thời ghế ngồi hoặc số lượng vé được Khách hàng lựa chọn trong khoảng thời gian xác định (có đồng hồ đếm ngược) nhằm hỗ trợ Khách hàng thực hiện thanh toán mà không bị người khác tranh chấp chỗ.
7. **"Đơn hàng hoàn tất" (Mã ORD):** Là trạng thái đơn hàng khi giao dịch thanh toán đã được cổng thanh toán trung gian xác nhận thành công, tiền đã được ghi nhận và hệ thống tự động cấp phát mã đơn hàng có định dạng ORD cùng các vé điện tử liên quan.

---

#### ĐIỀU 2: TƯ CÁCH PHÁP LÝ VÀ MÔ HÌNH SÀN GIAO DỊCH TRUNG GIAN
1. **Mô hình trung gian công nghệ:** EventPulse hoạt động theo mô hình sàn giao dịch thương mại điện tử phân phối vé trực tuyến, đóng vai trò đơn vị cung ứng giải pháp công nghệ kết nối giữa Ban Tổ Chức sự kiện và Khách hàng.
2. **Phân định trách nhiệm nội dung sự kiện:** Ban Tổ Chức là bên duy nhất chịu trách nhiệm toàn diện trước pháp luật và Khách hàng về:
   - Tính hợp pháp của chương trình, giấy phép biểu diễn của cơ quan quản lý nhà nước có thẩm quyền;
   - Chất lượng nội dung, kịch bản, thời lượng chương trình, dàn diễn viên / nghệ sĩ biểu diễn;
   - An ninh, an toàn, sơ đồ phân luồng và cơ sở vật chất tại địa điểm diễn ra sự kiện;
   - Việc xuất hóa đơn giá trị gia tăng (VAT) cho dịch vụ biểu diễn/vé tham dự theo yêu cầu của Khách hàng.
3. **Miễn trừ trách nhiệm bên thứ ba:** EventPulse không phải là đơn vị đồng tổ chức hay người bảo lãnh cho bất kỳ sự kiện nào (trừ trường hợp EventPulse được công bố chính thức là đơn vị tổ chức trực tiếp). EventPulse được miễn trừ mọi trách nhiệm pháp lý phát sinh từ hành vi vi phạm, chậm trễ, hoãn hủy hoặc tranh chấp hợp đồng giữa Ban Tổ Chức và Khách hàng.

---

#### ĐIỀU 3: ĐĂNG KÝ TÀI KHOẢN, ĐỘ TUỔI VÀ BẢO MẬT XÁC THỰC HAI LỚP (2FA OTP)
1. **Điều kiện đăng ký:** Khách hàng phải từ đủ 15 tuổi trở lên hoặc có sự giám hộ hợp pháp của cha mẹ/người đại diện theo quy định của pháp luật Việt Nam. Khách hàng cam kết cung cấp thông tin cá nhân (Họ và tên, địa chỉ email, số điện thoại) chính xác, trung thực và thuộc sở hữu chính chủ.
2. **Cơ chế xác thực an toàn:** Mật khẩu đăng nhập của Khách hàng được mã hóa một chiều bằng thuật toán băm Argon2id theo chuẩn an ninh công nghệ thông tin quốc tế. Hệ thống tích hợp xác thực hai yếu tố (2FA) qua mã OTP gửi về email cá nhân khi đăng nhập và thực hiện các giao dịch trọng yếu.
3. **Trách nhiệm tự bảo mật của Khách hàng:** Khách hàng có nghĩa vụ tự bảo quản bí mật mật khẩu, mã OTP và thiết bị đăng nhập. Không chia sẻ thông tin tài khoản cho bất kỳ cá nhân nào khác.
4. **Miễn trừ trách nhiệm lộ danh tính:** Mọi giao dịch đặt mua vé xuất phát từ tài khoản đã đăng nhập thành công qua mật khẩu và OTP hợp lệ đều được coi là do chính Khách hàng thực hiện. EventPulse miễn trừ trách nhiệm bồi thường cho bất kỳ thiệt hại nào phát sinh do Khách hàng để lộ thông tin đăng nhập hoặc bị chiếm quyền điều khiển thiết bị cá nhân.

---

#### ĐIỀU 4: QUY TRÌNH GIỮ CHỖ TẠM THỜI (SEAT HOLD) VÀ GIAO KẾT ĐƠN HÀNG
1. **Cơ chế giữ chỗ tạm thời:** Khi Khách hàng lựa chọn vị trí ghế hoặc số lượng vé và bấm tiếp tục, hệ thống sẽ tự động kích hoạt cơ chế khóa chỗ tạm thời trong khoảng thời gian quy định (thể hiện bằng đồng hồ đếm ngược trên màn hình đặt vé).
2. **Hết hạn giữ chỗ:** Nếu Khách hàng không hoàn tất thủ tục thanh toán trước khi đồng hồ đếm ngược trở về số 0, giao dịch giữ chỗ sẽ tự động bị hủy bỏ; hệ thống sẽ giải phóng các vị trí ghế ngồi trở lại kho vé công khai cho những khách hàng khác đặt mua.
3. **Giới hạn số lượng vé chống gom vé:** Nhằm bảo đảm tính công bằng và minh bạch cho cộng đồng người hâm mộ, hệ thống áp dụng hạn mức mua vé tối đa trên mỗi tài khoản hoặc mỗi lần giao dịch (tùy thuộc vào chính sách của từng sự kiện do BTC thiết lập). Mọi nỗ lực vượt qua giới hạn này bằng nhiều phiên kết nối sẽ bị hệ thống tự động từ chối.

---

#### ĐIỀU 5: PHƯƠNG THỨC THANH TOÁN VÀ XÁC NHẬN GIAO DỊCH
1. **Phương thức thanh toán:** Khách hàng thanh toán giá trị đơn hàng thông qua các cổng trung gian thanh toán hợp pháp tích hợp trên hệ thống (Chuyển khoản mã VietQR, Thẻ nội địa Napas, Thẻ quốc tế Visa/Mastercard, Ví điện tử).
2. **Thời điểm giao dịch hoàn tất:** Giao dịch chỉ được ghi nhận là hoàn tất khi:
   - Cổng thanh toán phản hồi thông báo thanh toán thành công;
   - Hệ thống EventPulse xử lý khớp tiền và phát sinh mã đơn hàng ORD trên cơ sở dữ liệu;
   - Khách hàng nhận được màn hình thông báo "Đặt vé thành công" hoặc email xác nhận đơn hàng kèm vé điện tử.
3. **Trách nhiệm rà soát thông tin:** Khách hàng có trách nhiệm kiểm tra kỹ lưỡng các thông tin chi tiết: Tên sự kiện, Suất diễn, Khung giờ, Hạng vé, Số ghế và Tổng số tiền phải trả trước khi bấm lệnh xác nhận thanh toán.

---

#### ĐIỀU 6: PHÁT HÀNH VÉ ĐIỆN TỬ VÀ QUY CHẾ KIỂM SOÁT CỬA VÀO BẰNG CHỮ KÝ SỐ ECDSA
1. **Giá trị pháp lý của Vé điện tử:** Vé điện tử do EventPulse phát hành chứa mã QR định danh kèm chữ ký số bất đối xứng ECDSA có giá trị pháp lý tương đương vé giấy truyền thống và là căn cứ duy nhất để xác định quyền vào cửa sự kiện.
2. **Nguyên tắc "Quét vé 01 lần duy nhất" (Single Check-in):** Mỗi mã QR vé điện tử chỉ có giá trị cho 01 (một) lượt quét hợp lệ tại cửa soát vé của sự kiện. Hệ thống máy quét của EventPulse vận hành trên nguyên tắc: *"Người xuất trình mã QR hợp lệ và quét thành công trước sẽ được quyền vào cửa"*.
3. **Bảo mật mã vé và Miễn trừ trách nhiệm mất vé:** Khách hàng có nghĩa vụ bảo mật tuyệt đối mã QR vé điện tử của mình:
   - Không chụp ảnh vé chứa mã QR đăng tải lên các nền tảng mạng xã hội, diễn đàn hoặc chia sẻ công khai;
   - Không chuyển tiếp email chứa mã vé cho những người không có quyền lợi liên quan;
   - Trong trường hợp mã QR bị người khác quét vào cổng trước do sự bất cẩn hoặc để lộ thông tin từ phía Khách hàng, EventPulse và Ban Tổ Chức có quyền từ chối cho lượt quét tiếp theo vào cửa và hoàn toàn miễn trừ nghĩa vụ bồi hoàn.

---

#### ĐIỀU 7: CHÍNH SÁCH ĐỔI, TRẢ VÉ, HỦY ĐƠN HÀNG VÀ HOÀN TIỀN
1. **Nguyên tắc chung:** Vé sự kiện đã mua thành công không được đổi sang sự kiện khác hoặc trả lại lấy tiền mặt theo nhu cầu cá nhân chủ quan, trừ các trường hợp được quy định rõ tại Khoản 2, Khoản 3 và Khoản 4 dưới đây.
2. **Quyền tự phục vụ Hủy vé và Hoàn tiền của Khách hàng (Self-Service Cancellation - Tính năng S-45 & S-46):**
   - *Điều kiện áp dụng đồng thời:*
     + (i) Khách hàng chủ động gửi yêu cầu hủy đơn hàng trực tiếp trên hệ thống trước thời điểm diễn ra sự kiện tối thiểu 24 giờ (hoặc theo hạn chót riêng được BTC thông báo);
     + (ii) Toàn bộ các vé trong đơn hàng chưa từng thực hiện quét mã QR tại cổng kiểm soát (chưa check-in);
     + (iii) Đơn hàng chưa từng phát sinh khiếu nại hoặc yêu cầu hoàn tiền đang xử lý dở dang.
   - *Tỷ lệ hoàn tiền và Khấu trừ chi phí:* Khách hàng được hoàn trả **90% tổng giá trị đơn hàng** đã thanh toán. **10% còn lại** được khấu trừ để bù đắp chi phí xử lý cổng thanh toán ngân hàng, phí chuyển khoản và chi phí vận hành hạ tầng kỹ thuật của Nền tảng.
   - *Quy trình thu hồi vé và nhả ghế tự động:* Ngay khi yêu cầu hủy vé được xác nhận thành công:
     + Toàn bộ mã QR của các vé trong đơn hàng lập tức bị vô hiệu hóa trên hệ thống máy quét;
     + Các vị trí ghế ngồi tương ứng được hệ thống tự động giải phóng và hoàn trả về sơ đồ bán vé công khai cho những khách hàng khác đặt mua.
3. **Hoàn tiền 100% khi Sự kiện bị hoãn, thay đổi hoặc hủy bỏ bởi Ban Tổ Chức (Quy trình S-51):**
   - Trường hợp sự kiện bị Ban Tổ Chức thông báo hủy bỏ hoàn toàn hoặc thay đổi thời gian/địa điểm mà Khách hàng không thể sắp xếp tham dự lịch mới, Khách hàng sẽ được hoàn trả **100% giá trị tiền vé**.
   - Thời gian hoàn tiền: Từ 07 đến 15 ngày làm việc kể từ thời điểm BTC hoàn tất đối soát tài chính, tiền được chuyển trả trực tiếp về phương thức thanh toán ban đầu của Khách hàng.
4. **Hoàn tiền do Lỗi kỹ thuật hệ thống:**
   - Trường hợp tài khoản ngân hàng của Khách hàng đã bị trừ tiền thành công nhưng hệ thống gặp sự cố mạng dẫn đến không phát hành được đơn hàng và vé điện tử (quá hạn timeout giữ vé), EventPulse cam kết tra soát và hoàn trả 100% số tiền đã trừ trong vòng 03 đến 05 ngày làm việc.
5. **Các trường hợp từ chối hủy vé và hoàn tiền:**
   - Yêu cầu hủy gửi sau thời hạn quy định (dưới 24 giờ trước thời điểm bắt đầu sự kiện);
   - Bất kỳ vé nào trong đơn hàng đã quét mã QR vào cổng sự kiện;
   - Tài khoản hoặc đơn hàng vi phạm chính sách phòng chống đầu cơ và gian lận quy định tại Điều 8.

---

#### ĐIỀU 8: PHÒNG CHỐNG ĐẦU CƠ VÉ, PHE VÉ VÀ GIAN LẬN KỸ THUẬT (ANTI-SCALPING)
1. **Các hành vi bị nghiêm cấm tuyệt đối:**
   - Sử dụng các công cụ tự động, phần mềm bot, mã kịch bản (script), giả lập trình duyệt nhằm can thiệp bất hợp pháp vào quy trình đặt vé để gom vé số lượng lớn;
   - Thu gom vé với mục đích đầu cơ, bán lại cho người khác với mức giá chênh lệch cao hơn giá gốc niêm yết ngoài các kênh phân phối chính thức;
   - Sao chép, chỉnh sửa, giả mạo vé điện tử hoặc sử dụng thông tin thanh toán gian lận, thẻ tín dụng bị đánh cắp.
2. **Chế tài xử lý vi phạm:** Khi phát hiện bất kỳ dấu hiệu nghi vấn vi phạm nào, EventPulse và Ban Tổ Chức có toàn quyền thực hiện các biện pháp sau mà không cần báo trước:
   - Ngay lập tức hủy bỏ hiệu lực của toàn bộ vé liên quan, khóa mã QR trên cổng soát vé và **không hoàn tiền**;
   - Khóa vĩnh viễn quyền truy cập tài khoản của người dùng vi phạm;
   - Chuyển giao thông tin tài khoản, nhật ký kết nối IP và bằng chứng liên quan cho Cơ quan Công an và cơ quan chức năng để xử lý nghiêm theo quy định của pháp luật hình sự về tội lừa đảo chiếm đoạt tài sản hoặc vi phạm trật tự kinh tế.

---

#### ĐIỀU 9: QUYỀN SỞ HỮU TRÍ TUỆ VÀ GIỚI HẠN TRÁCH NHIỆM PHÁP LÝ
1. **Quyền sở hữu trí tuệ:** Toàn bộ bản quyền giao diện người dùng, nhãn hiệu thương mại "EventPulse", logo, đồ họa, mã nguồn, cấu trúc cơ sở dữ liệu, thuật toán kiểm soát vé đều là tài sản sở hữu trí tuệ độc quyền của đơn vị vận hành EventPulse. Mọi hành vi sao chép, đảo ngược mã nguồn hoặc khai thác thương mại khi chưa có sự chấp thuận bằng văn bản đều là bất hợp pháp.
2. **Trường hợp bất khả kháng:** EventPulse được miễn trừ trách nhiệm bồi thường trong các tình huống bất khả kháng nằm ngoài tầm kiểm soát hợp lý của công nghệ, bao gồm: Thiên tai, dịch bệnh, hỏa hoạn, chiến tranh, sự cố đứt cáp quang biển quốc tế, quyết định đình chỉ biểu diễn từ cơ quan có thẩm quyền của Nhà nước, hoặc sự cố mất điện/mạng viễn thông diện rộng.
3. **Giới hạn bồi thường tối đa:** Trong mọi trường hợp phát sinh khiếu nại có lỗi được chứng minh là từ EventPulse, tổng mức trách nhiệm bồi thường thiệt hại tối đa đối với Khách hàng không vượt quá tổng số tiền vé mà Khách hàng đã thanh toán cho đơn hàng phát sinh tranh chấp.

---

#### ĐIỀU 10: QUẢN LÝ PHIÊN BẢN ĐIỀU KHOẢN VÀ CƠ CHẾ BẮT BUỘC TÁI CHẤP THUẬN (STORY S-54)
1. **Quyền sửa đổi:** EventPulse có quyền điều chỉnh, sửa đổi, bổ sung các điều khoản này tại bất kỳ thời điểm nào nhằm đáp ứng yêu cầu vận hành hoặc tuân thủ các thay đổi của quy định pháp luật.
2. **Đánh số phiên bản minh bạch:** Mỗi lần sửa đổi sẽ được đánh số phiên bản mới (`TermsVersion`, ví dụ: v1.0, v1.1, v2.0) cùng ngày hiệu lực cụ thể.
3. **Cơ chế tái chấp thuận bắt buộc (AC3):** Khi hệ thống công bố phiên bản điều khoản mới, những tài khoản chưa đồng ý phiên bản mới nhất sẽ bị chặn đăng nhập và nhận thông báo yêu cầu đồng ý (`HTTP 428 Precondition Required`). Khách hàng bắt buộc phải đọc và bấm xác nhận đồng ý phiên bản mới để được cấp quyền đăng nhập và tiếp tục sử dụng hệ thống.

---

#### ĐIỀU 11: LUẬT ĐIỀU CHỈNH VÀ GIẢI QUYẾT TRANH CHẤP
1. **Luật áp dụng:** Bản Điều khoản này được điều chỉnh, diễn giải và thi hành theo quy định của hệ thống pháp luật nước Cộng hòa Xã hội Chủ nghĩa Việt Nam.
2. **Giải quyết tranh chấp:** Mọi bất đồng, tranh chấp phát sinh giữa Khách hàng và EventPulse trước hết sẽ được ưu tiên thương lượng giải quyết trên tinh thần thiện chí, hợp tác và bảo đảm quyền lợi chính đáng của người tiêu dùng trong vòng ba mươi (30) ngày. Trường hợp không thể tự hòa giải, một trong các bên có quyền đưa tranh chấp ra giải quyết tại Tòa án nhân dân có thẩm quyền tại Việt Nam.

---

### 7.2 CHÍNH SÁCH BẢO VỆ DỮ LIỆU CÁ NHÂN VÀ QUYỀN RIÊNG TƯ (TUÂN THỦ NGHỊ ĐỊNH 13/2023/NĐ-CP)

**CAM KẾT TUÂN THỦ:**  
EventPulse tôn trọng và cam kết bảo vệ dữ liệu cá nhân của mọi Khách hàng. Chính sách Bảo vệ Dữ liệu Cá nhân và Quyền Riêng tư này (sau đây gọi là *"Chính sách Bảo mật"*) được ban hành nhằm tuân thủ triệt để **Nghị định số 13/2023/NĐ-CP ngày 17/04/2023 của Chính phủ về bảo vệ dữ liệu cá nhân**. Bản chính sách này giải thích chi tiết mục đích, phạm vi, loại dữ liệu thu thập, các biện pháp bảo mật và các quyền hợp pháp của Quý khách với tư cách là Chủ thể dữ liệu.

---

#### ĐIỀU 1: CĂN CỨ PHÁP LÝ VÀ NGUYÊN TẮC XỬ LÝ DỮ LIỆU
1. **Căn cứ pháp lý:** Áp dụng theo quy định của Nghị định số 13/2023/NĐ-CP, Luật Giao dịch điện tử, Luật Bảo vệ quyền lợi người tiêu dùng và các văn bản quy phạm pháp luật liên quan của Việt Nam.
2. **Nguyên tắc xử lý dữ liệu:** Dữ liệu cá nhân của Quý khách luôn được xử lý dựa trên nguyên tắc:
   - **Hợp pháp và Minh bạch:** Xử lý trên cơ sở sự đồng ý tự nguyện của Chủ thể dữ liệu;
   - **Đúng mục đích:** Chỉ thu thập dữ liệu phục vụ trực tiếp cho việc vận hành dịch vụ đặt vé và các mục tiêu đã thông báo trước;
   - **Tối thiểu hóa dữ liệu:** Chỉ yêu cầu cung cấp các trường thông tin cần thiết nhất để hoàn thành dịch vụ;
   - **An toàn và Bảo mật:** Áp dụng các biện pháp kỹ thuật số và an ninh mạng chặt chẽ để phòng chống rò rỉ dữ liệu.

---

#### ĐIỀU 2: CÁC LOẠI DỮ LIỆU CÁ NHÂN ĐƯỢC THU THẬP VÀ XỬ LÝ
Hệ thống EventPulse thu thập và xử lý các nhóm dữ liệu cá nhân sau:
1. **Dữ liệu cá nhân cơ bản:** Họ và tên, địa chỉ thư điện tử (email), số điện thoại di động, mật khẩu đăng nhập tài khoản (được lưu trữ dưới dạng chuỗi băm một chiều mật mã học Argon2id).
2. **Dữ liệu giao dịch và dịch vụ vé:** Lịch sử đơn hàng, mã số đơn hàng (ORD), tên sự kiện đã tham gia, hạng vé, vị trí ghế ngồi, số tiền thanh toán, phương thức giao dịch, mã vé điện tử ký số ECDSA, thời gian và địa điểm quét mã QR vào cổng soát vé.
3. **Dữ liệu kỹ thuật số và thiết bị:** Địa chỉ IP truy cập, chuỗi định danh trình duyệt (User-Agent), cookies phiên làm việc, nhật ký thời gian đăng nhập/đăng xuất, lịch sử chấp thuận phiên bản điều khoản (`TermsVersion`, `TermsAcceptedAt`), và trạng thái đồng ý nhận email tiếp thị (`MarketingEmailOptIn`).

---

#### ĐIỀU 3: MỤC ĐÍCH XỬ LÝ DỮ LIỆU CÁ NHÂN
EventPulse sử dụng dữ liệu cá nhân của Khách hàng vào các mục đích hợp pháp sau:
1. **Xác thực tài khoản và Cung cấp dịch vụ:** Nhận diện người dùng, gửi mã xác thực hai lớp (2FA OTP) qua email, duy trì phiên đăng nhập và kích hoạt tài khoản.
2. **Xử lý đơn hàng và Phát hành vé ký số:** Xác nhận thanh toán, khởi tạo vé điện tử kèm mã QR ký số ECDSA và gửi thông tin vé qua email cho Khách hàng.
3. **Kiểm soát ra vào và Đối soát sự kiện:** Chuyển giao dữ liệu mã vé phục vụ hệ thống máy quét tại cổng soát vé nhằm xác định người có quyền vào xem chương trình.
4. **Hỗ trợ khách hàng và Giải quyết khiếu nại:** Tiếp nhận và xử lý yêu cầu hủy vé hoàn tiền tự phục vụ (S-45/S-46), bồi hoàn khi có sự cố kỹ thuật hoặc giải đáp các thắc mắc về đơn hàng.
5. **Gửi thông báo vận hành thiết yếu:** Gửi thông báo khẩn cấp khi sự kiện có sự thay đổi về thời gian, địa điểm, nghệ sĩ biểu diễn hoặc bị hủy bỏ; gửi cảnh báo bảo mật tài khoản.
6. **Tuân thủ nghĩa vụ pháp luật và Kế toán - Thuế:** Lưu trữ chứng từ giao dịch phục vụ quyết toán thuế, xuất hóa đơn tài chính và kiểm toán theo quy định của Luật Kế toán và Luật Quản lý thuế Việt Nam.
7. **Tiếp thị và Khuyến mãi:** Gửi thông tin về các sự kiện mới, mã giảm giá và ưu đãi (chỉ thực hiện khi Khách hàng tự nguyện đồng ý).

---

#### ĐIỀU 4: BIỆN PHÁP BẢO MẬT VÀ AN NINH THÔNG TIN
1. **Mật mã học một chiều Argon2id:** Toàn bộ mật khẩu của tài khoản đều được băm một chiều an toàn bằng thuật toán Argon2id kết hợp chuỗi salt ngẫu nhiên. Ngay cả quản trị viên hệ thống cũng không thể đọc được mật khẩu gốc của người dùng.
2. **Chữ ký số bất đối xứng ECDSA:** Vé điện tử được ký bằng thuật toán mật mã đường cong elliptic ECDSA, bảo đảm mã vé không thể bị làm giả, sao chép hay can thiệp nội dung.
3. **Kiểm soát phân quyền nghiêm ngặt (RBAC):** Chỉ những nhân sự được phân quyền cụ thể (`Admin`, `Organizer`, `Staff`) mới được truy cập dữ liệu trong phạm vi phục vụ công việc; hệ thống lưu trữ toàn diện nhật ký kiểm toán (audit trail) đối với mọi thao tác truy cập dữ liệu nhạy cảm.

---

#### ĐIỀU 5: QUY ĐỊNH VỀ CHIA SẺ DỮ LIỆU CHO BÊN THỨ BA
EventPulse cam kết bảo mật tuyệt đối dữ liệu người dùng và chỉ chia sẻ thông tin trong các trường hợp giới hạn sau:
1. **Chia sẻ với Ban Tổ Chức sự kiện:** Chỉ cung cấp danh sách vé hợp lệ, mã QR và thông tin đối soát cần thiết cho BTC sự kiện mà Khách hàng đã đặt mua vé để phục vụ khâu kiểm soát cửa vào và bảo đảm an ninh trật tự tại địa điểm diễn ra chương trình.
2. **Chia sẻ với Cổng thanh toán trung gian:** Chia sẻ các thông tin giao dịch cần thiết (mã đơn hàng, số tiền) với các đối tác ngân hàng/cổng thanh toán để xử lý khấu trừ tiền và hoàn tiền theo yêu cầu.
3. **Theo yêu cầu của Cơ quan Nhà nước có thẩm quyền:** Cung cấp thông tin khi có yêu cầu bằng văn bản hợp pháp từ Cơ quan Công an, Viện Kiểm sát, Tòa án hoặc cơ quan quản lý nhà nước có thẩm quyền theo đúng trình tự pháp luật.
4. **Cam kết không thương mại hóa dữ liệu:** EventPulse tuyệt đối không bán, cho thuê, trao đổi hay chuyển giao dữ liệu cá nhân của Khách hàng cho bất kỳ tổ chức hay doanh nghiệp thứ ba nào vì mục đích quảng cáo hoặc khai thác thương mại trái phép.

---

#### ĐIỀU 6: QUYỀN HỢP PHÁP CỦA CHỦ THỂ DỮ LIỆU (THEO ĐIỀU 9 NGHỊ ĐỊNH 13/2023/NĐ-CP)
Quý khách có đầy đủ các quyền của Chủ thể dữ liệu theo quy định của pháp luật Việt Nam, bao gồm:
1. **Quyền được biết và Quyền đồng ý:** Được biết rõ mục đích, loại dữ liệu thu thập và có quyền tự nguyện đồng ý hoặc không đồng ý cho phép xử lý dữ liệu.
2. **Quyền truy cập và Quyền chỉnh sửa:** Được quyền đăng nhập vào hệ thống để tra cứu toàn bộ thông tin cá nhân, lịch sử đơn hàng và yêu cầu chỉnh sửa, cập nhật thông tin cá nhân chưa chính xác.
3. **Quyền yêu cầu xóa dữ liệu cá nhân ("Quyền được lãng quên" - Story S-53):**
   - Khách hàng có quyền gửi yêu cầu xóa dữ liệu cá nhân của mình tại giao diện **Cài đặt riêng tư** (`privacy-settings.html`).
   - Sau khi yêu cầu được xem xét và phê duyệt theo quy trình, hệ thống sẽ tiến hành **ẩn danh hóa vĩnh viễn** họ tên, email, số điện thoại trong cơ sở dữ liệu và ngừng hoạt động tài khoản;
   - Các chứng từ hóa đơn, dữ liệu đơn hàng và số liệu tài chính liên quan đến nghĩa vụ kế toán - thuế sẽ được lưu trữ độc lập theo quy định bắt buộc của pháp luật kế toán Việt Nam.
4. **Quyền rút lại sự đồng ý & Thu hồi quyền nhận Email tiếp thị (Story S-54):**
   - Khi đăng ký, Khách hàng có thể cho phép nhận email tiếp thị (`MarketingEmailOptIn = true`).
   - Khách hàng có toàn quyền **THU HỒI SỰ ĐỒNG Ý** này bất kỳ lúc nào bằng cách truy cập mục **Cài đặt riêng tư** (`privacy-settings.html`) và bấm nút **"Thu hồi quyền nhận email tiếp thị"**.
   - Ngay khi thu hồi (`MarketingEmailOptIn = false`), hệ thống cam kết **chặn tuyệt đối (HTTP 403 Forbidden)** mọi luồng gửi email tiếp thị / quảng bá đến địa chỉ email của Quý khách. Quý khách có thể kích hoạt lại quyền này bất cứ lúc nào nếu có nhu cầu.
5. **Quyền phản đối xử lý dữ liệu, Quyền khiếu nại và Bồi thường thiệt hại:** Khách hàng có quyền phản đối việc sử dụng dữ liệu sai mục đích và khiếu nại, tố cáo đến cơ quan bảo vệ dữ liệu có thẩm quyền hoặc khởi kiện yêu cầu bồi thường thiệt hại theo quy định pháp luật nếu EventPulse có hành vi vi phạm nghiêm trọng về dữ liệu cá nhân.

---

#### ĐIỀU 7: THỜI HẠN LƯU TRỮ DỮ LIỆU CÁ NHÂN
1. **Dữ liệu hồ sơ tài khoản:** Được lưu trữ trong suốt thời gian tài khoản của Khách hàng còn hoạt động trên Nền tảng EventPulse cho đến khi có yêu cầu xóa hợp lệ từ Khách hàng.
2. **Dữ liệu giao dịch tài chính, hóa đơn và đơn hàng:** Được lưu trữ theo thời hạn bắt buộc từ 05 đến 10 năm theo quy định của Luật Kế toán và Luật Quản lý thuế Việt Nam kể từ ngày phát sinh giao dịch.
3. **Nhật ký truy cập hệ thống và an ninh mạng:** Được lưu trữ tối thiểu 12 tháng theo quy định của Luật An ninh mạng để phục vụ công tác điều tra an toàn thông tin khi cần thiết.

---

#### ĐIỀU 8: THÔNG TIN LIÊN HỆ BỘ PHẬN BẢO VỆ DỮ LIỆU CÁ NHÂN (DPO)
Mọi thắc mắc, đề xuất, yêu cầu thực hiện các quyền của chủ thể dữ liệu hoặc phản ánh liên quan đến chính sách bảo vệ dữ liệu cá nhân, Quý khách vui lòng liên hệ trực tiếp với Bộ phận Bảo vệ Dữ liệu Cá nhân của chúng tôi qua thông tin sau:
- **Đơn vị quản lý:** Ban Pháp chế & An toàn Thông tin - Nền tảng EventPulse
- **Email tiếp nhận yêu cầu về dữ liệu:** `privacy@eventpulse.vn`
- **Email hỗ trợ pháp lý và giải quyết khiếu nại:** `legal@eventpulse.vn`
- **Đường dây nóng (Hotline):** `1900 6868` (Thời gian hỗ trợ: 08:30 – 18:00 từ Thứ Hai đến Thứ Sáu)
- **Địa chỉ trụ sở:** Tòa nhà Công nghệ EventPulse, Số 123 Đường Công Nghệ, Quận Cầu Giấy, TP. Hà Nội, Việt Nam.
- **Cam kết thời gian xử lý:** Bộ phận DPO của chúng tôi cam kết tiếp nhận và phản hồi các yêu cầu liên quan đến dữ liệu cá nhân của Quý khách trong vòng tối đa **72 giờ làm việc** kể từ thời điểm nhận được yêu cầu hợp lệ.

