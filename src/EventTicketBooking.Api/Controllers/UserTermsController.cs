using System;
using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Threading.Tasks;
using EventTicketBooking.Api.Data;
using EventTicketBooking.Api.DTOs;
using EventTicketBooking.Api.Middlewares;
using EventTicketBooking.Api.Models;
using EventTicketBooking.Api.Services;
using EventTicketBooking.Api.Services.Interfaces;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace EventTicketBooking.Api.Controllers
{
    [ApiController]
    [Route("api/v1/user/terms")]
    public class UserTermsController : ControllerBase
    {
        private readonly AppDbContext _context;
        private readonly IEmailService _emailService;
        private readonly ILogger<UserTermsController> _logger;

        public UserTermsController(
            AppDbContext context,
            IEmailService emailService,
            ILogger<UserTermsController> logger)
        {
            _context = context;
            _emailService = emailService;
            _logger = logger;
        }

        /// <summary>
        /// Lấy thông tin Điều khoản dịch vụ và Chính sách riêng tư hiện hành (Công khai).
        /// </summary>
        [HttpGet("policy")]
        [AllowAnonymous]
        [ProducesResponseType(typeof(TermsPolicyInfoDto), StatusCodes.Status200OK)]
        public IActionResult GetCurrentPolicy()
        {
            var policyInfo = new TermsPolicyInfoDto
            {
                Version = TermsPolicy.CurrentVersion,
                EffectiveDate = "2026-10-18",
                Title = "Điều khoản dịch vụ và Chính sách bảo vệ dữ liệu cá nhân EventPulse",
                Summary = "Quy định toàn diện về dịch vụ phân phối vé trực tuyến, vai trò trung gian giữa Ban Tổ Chức và Khách hàng, bảo vệ quyền riêng tư, quyền yêu cầu xóa dữ liệu cá nhân và quyền thu hồi email tiếp thị theo Nghị định 13/2023/NĐ-CP.",
                TermsContent = """
ĐIỀU 1: GIẢI THÍCH TỪ NGỮ VÀ ĐỊNH NGHĨA PHÁP LÝ
1. "EventPulse" / "Nền tảng": Hệ thống sàn thương mại điện tử giao dịch vé sự kiện trực tuyến.
2. "Ban Tổ Chức" (BTC): Pháp nhân hoặc cá nhân chủ quản sự kiện, chịu trách nhiệm pháp lý cao nhất về việc tổ chức, giấy phép biểu diễn, chất lượng chương trình và địa điểm.
3. "Khách hàng": Cá nhân hoặc tổ chức đăng ký tài khoản hợp lệ để tìm kiếm, giữ chỗ và mua vé tham dự sự kiện.
4. "Vé điện tử" (E-Ticket): Chứng từ điện tử hợp pháp do EventPulse phát hành xác nhận quyền vào cửa sự kiện.
5. "Mã QR chữ ký số ECDSA": Chuỗi dữ liệu mã hóa mật mã học bất đối xứng dùng để đối soát ra vào cổng, chống sao chép và làm giả tuyệt đối.
6. "Giữ chỗ tạm thời" (Seat Hold): Tính năng khóa tạm thời vị trí ghế/vé trong thời gian đếm ngược (countdown) để khách hàng thực hiện thanh toán.
7. "Đơn hàng hoàn tất" (Mã ORD): Giao dịch thanh toán thành công được cổng thanh toán xác nhận và hệ thống cấp vé điện tử.

ĐIỀU 2: TƯ CÁCH PHÁP LÝ VÀ MÔ HÌNH SÀN GIAO DỊCH TRUNG GIAN
1. EventPulse hoạt động theo mô hình sàn giao dịch thương mại điện tử phân phối vé trực tuyến, đóng vai trò đơn vị công nghệ trung gian kết nối giữa BTC và Khách hàng.
2. Ban Tổ Chức là bên bán hàng và chịu trách nhiệm toàn diện trước pháp luật về tính hợp pháp của sự kiện, an ninh trật tự, địa điểm tổ chức và xuất hóa đơn GTGT theo quy định.
3. EventPulse được miễn trừ mọi trách nhiệm pháp lý phát sinh từ hành vi vi phạm, chậm trễ, hoãn hủy hoặc tranh chấp hợp đồng giữa Ban Tổ Chức và Khách hàng.

ĐIỀU 3: ĐĂNG KÝ TÀI KHOẢN VÀ BẢO MẬT XÁC THỰC HAI LỚP (2FA OTP)
1. Khách hàng từ đủ 15 tuổi trở lên hoặc có sự đồng ý của người giám hộ hợp pháp; cam kết cung cấp thông tin chính chủ và trung thực.
2. Mật khẩu đăng nhập được mã hóa băm an toàn bằng thuật toán Argon2id; hệ thống kích hoạt xác thực hai yếu tố (2FA) qua mã OTP gửi về email cá nhân khi đăng nhập.
3. Khách hàng có nghĩa vụ tự bảo quản bí mật mật khẩu, mã OTP và thiết bị cá nhân; EventPulse miễn trừ trách nhiệm bồi thường nếu khách hàng tự ý chia sẻ hoặc để lộ thông tin đăng nhập.

ĐIỀU 4: QUY TRÌNH GIỮ CHỖ TẠM THỜI (SEAT HOLD) VÀ GIAO KẾT ĐƠN HÀNG
1. Khi chọn vé, hệ thống áp dụng cơ chế giữ chỗ tạm thời (Seat Hold) với đồng hồ đếm ngược thời gian thực.
2. Quá hạn đếm ngược mà chưa hoàn tất thanh toán, hệ thống tự động hủy giữ chỗ và trả vé về kho bán công khai cho người khác đặt mua.
3. Áp dụng giới hạn số lượng vé tối đa trên mỗi tài khoản hoặc mỗi giao dịch theo chính sách của từng sự kiện nhằm bảo đảm sự công bằng.

ĐIỀU 5: PHƯƠNG THỨC THANH TOÁN VÀ XÁC NHẬN GIAO DỊCH
1. Thanh toán qua các cổng trung gian thanh toán hợp pháp (Mã QR VietQR, thẻ nội địa Napas, thẻ quốc tế Visa/Mastercard, ví điện tử).
2. Giao dịch mua vé hoàn tất khi cổng thanh toán xác nhận thành công và hệ thống EventPulse cấp mã đơn hàng ORD.
3. Khách hàng có trách nhiệm kiểm tra kỹ tên sự kiện, suất diễn, hạng vé, vị trí ghế và tổng số tiền trước khi xác nhận thanh toán.

ĐIỀU 6: PHÁT HÀNH VÉ ĐIỆN TỬ VÀ QUY CHẾ KIỂM SOÁT CỬA VÀO BẰNG CHỮ KÝ SỐ ECDSA
1. Vé điện tử chứa mã QR ký số ECDSA có giá trị pháp lý tương đương vé giấy truyền thống.
2. Mỗi vé điện tử chỉ có giá trị cho 01 (một) lượt quét hợp lệ tại cổng kiểm soát. Áp dụng nguyên tắc "Ai quét mã hợp lệ trước sẽ được vào cửa trước".
3. Khách hàng có nghĩa vụ bảo mật mã vé, không chụp ảnh mã QR đăng lên mạng xã hội. Nếu để lộ dẫn đến việc người khác quét vào cửa trước, EventPulse và BTC từ chối giải quyết.

ĐIỀU 7: CHÍNH SÁCH ĐỔI, TRẢ VÉ, HỦY ĐƠN HÀNG VÀ HOÀN TIỀN
1. Nguyên tắc chung: Vé đã mua không hoàn trả hoặc chuyển đổi sang sự kiện khác theo nhu cầu cá nhân chủ quan.
2. Quyền tự phục vụ Hủy vé của Khách hàng (S-45 & S-46): Khách hàng có quyền chủ động hủy vé trên hệ thống nếu thỏa mãn: (i) Trước giờ diễn tối thiểu 24 giờ; (ii) Toàn bộ vé chưa check-in tại cổng; (iii) Chưa có khiếu nại hoàn tiền dở dang. Mức hoàn tiền là 90% (khấu trừ 10% phí xử lý hệ thống và ngân hàng); vé sẽ lập tức bị hủy mã QR và ghế được trả về kho bán.
3. Hoàn tiền khi BTC hoãn/hủy sự kiện (S-51): Khách hàng được hoàn trả 100% tiền vé trong thời hạn 07 - 15 ngày làm việc.
4. Lỗi hệ thống: Khách hàng được hoàn trả 100% khi tài khoản bị trừ tiền nhưng lỗi mạng không phát hành được đơn hàng.
5. Từ chối hoàn tiền: Khi đã quá hạn chót 24 giờ, vé đã quét vào cửa, hoặc đơn hàng vi phạm quy định chống phe vé.

ĐIỀU 8: PHÒNG CHỐNG ĐẦU CƠ VÉ, PHE VÉ VÀ GIAN LẬN KỸ THUẬT (ANTI-SCALPING)
1. Nghiêm cấm dùng robot, mã script tự động để gom vé số lượng lớn hoặc mua bán vé chênh lệch giá trái phép nhằm trục lợi.
2. Khi phát hiện vi phạm: Hủy toàn bộ vé liên quan không hoàn tiền, khóa vĩnh viễn tài khoản và chuyển hồ sơ cho cơ quan Công an xử lý theo quy định pháp luật.

ĐIỀU 9: QUYỀN SỞ HỮU TRÍ TUỆ VÀ GIỚI HẠN TRÁCH NHIỆM
1. Bản quyền giao diện, nhãn hiệu EventPulse, mã nguồn, thuật toán kiểm soát vé thuộc sở hữu độc quyền của đơn vị vận hành.
2. Miễn trừ trách nhiệm trong các tình huống bất khả kháng (thiên tai, dịch bệnh, đứt cáp quang biển diện rộng, lệnh cấm biểu diễn của cơ quan quản lý).
3. Trách nhiệm bồi thường thiệt hại tối đa không vượt quá tổng số tiền vé mà khách hàng đã thanh toán cho đơn hàng liên quan.

ĐIỀU 10: QUẢN LÝ PHIÊN BẢN ĐIỀU KHOẢN VÀ CƠ CHẾ BẮT BUỘC TÁI CHẤP THUẬN (S-54)
1. Đánh số phiên bản TermsVersion và cập nhật ngày hiệu lực khi có điều chỉnh.
2. Khi cập nhật phiên bản mới, người dùng chưa đồng ý sẽ bị chặn đăng nhập (HTTP 428 Precondition Required) và bắt buộc phải xác nhận đồng ý phiên bản mới để tiếp tục sử dụng.

ĐIỀU 11: LUẬT ĐIỀU CHỈNH VÀ GIẢI QUYẾT TRANH CHẤP
1. Áp dụng theo pháp luật nước CHXHCN Việt Nam.
2. Tranh chấp được giải quyết trước hết bằng thương lượng hòa giải trong 30 ngày; nếu không thành sẽ đưa ra Tòa án nhân dân có thẩm quyền tại Việt Nam.
""",
                PrivacyContent = """
ĐIỀU 1: CĂN CỨ PHÁP LÝ VÀ NGUYÊN TẮC XỬ LÝ DỮ LIỆU
1. Tuân thủ triệt để Nghị định 13/2023/NĐ-CP của Chính phủ về bảo vệ dữ liệu cá nhân, Luật An ninh mạng và các luật chuyên ngành liên quan.
2. Dữ liệu cá nhân luôn được xử lý hợp pháp, minh bạch, đúng mục đích đã công bố, tối thiểu hóa dữ liệu và bảo mật an toàn.

ĐIỀU 2: CÁC LOẠI DỮ LIỆU CÁ NHÂN ĐƯỢC THU THẬP
1. Dữ liệu cá nhân cơ bản: Họ tên, email, số điện thoại, mật khẩu tài khoản (mã hóa băm an toàn bằng Argon2id).
2. Dữ liệu giao dịch & vé: Lịch sử đặt vé, mã ORD, vị trí ghế ngồi, số tiền, mã QR ký số ECDSA, thời gian quét vé vào cổng.
3. Dữ liệu kỹ thuật số: Địa chỉ IP, chuỗi User-Agent, cookies phiên, lịch sử chấp thuận phiên bản điều khoản (TermsVersion, TermsAcceptedAt), trạng thái nhận email tiếp thị (MarketingEmailOptIn).

ĐIỀU 3: MỤC ĐÍCH XỬ LÝ DỮ LIỆU CÁ NHÂN
1. Nhận diện người dùng, gửi mã xác thực 2 lớp (2FA OTP) qua email và duy trì phiên làm việc an toàn.
2. Khởi tạo đơn hàng, phát hành vé điện tử ký số ECDSA và gửi thông tin vé qua email.
3. Phục vụ công tác quét vé, đối soát vào cửa và phòng chống vé giả mạo tại sự kiện.
4. Hỗ trợ khách hàng xử lý hủy vé tự phục vụ, giải quyết khiếu nại và tra soát thanh toán.
5. Gửi thông báo vận hành thiết yếu khi sự kiện hoãn/hủy hoặc có cảnh báo khẩn cấp.
6. Lưu trữ hồ sơ kế toán, hóa đơn và thực hiện nghĩa vụ thuế theo quy định pháp luật Việt Nam.
7. Gửi email tiếp thị và thông tin ưu đãi (chỉ thực hiện khi có sự đồng ý của khách hàng).

ĐIỀU 4: BIỆN PHÁP BẢO MẬT VÀ AN NINH DỮ LIỆU
1. Áp dụng thuật toán băm Argon2id đối với toàn bộ mật khẩu người dùng.
2. Mã hóa chữ ký số bất đối xứng ECDSA đối với toàn bộ mã QR vé điện tử.
3. Áp dụng phân quyền truy cập nghiêm ngặt (RBAC) và lưu trữ nhật ký truy vết bảo mật (audit log).

ĐIỀU 5: CHIA SẺ DỮ LIỆU CHO BÊN THỨ BA
1. Ban Tổ Chức sự kiện: Chia sẻ danh sách vé hợp lệ để đối soát vào cổng tại sự kiện tương ứng.
2. Cổng thanh toán & Ngân hàng: Phục vụ đối soát trừ tiền và xử lý hoàn tiền theo quy định.
3. Cơ quan Nhà nước có thẩm quyền: Cung cấp khi có yêu cầu bằng văn bản hợp pháp.
4. Cam kết tuyệt đối không bán, cho thuê hay trao đổi dữ liệu cá nhân cho bên thứ ba vì mục đích thương mại ngoài hệ thống.

ĐIỀU 6: QUYỀN CỦA CHỦ THỂ DỮ LIỆU (THEO NGHỊ ĐỊNH 13/2023/NĐ-CP)
1. Quyền được biết và Quyền đồng ý: Được thông tin minh bạch và chủ động quyết định việc cung cấp thông tin.
2. Quyền truy cập và Chỉnh sửa: Tra cứu và cập nhật dữ liệu cá nhân trong trang tài khoản.
3. Quyền yêu cầu xóa dữ liệu cá nhân ("Quyền được lãng quên" - Story S-53): Khách hàng có quyền gửi yêu cầu xóa dữ liệu cá nhân tại mục 'Cài đặt riêng tư'. Sau khi được phê duyệt, thông tin cá nhân sẽ được ẩn danh hóa vĩnh viễn trong CSDL; chứng từ kế toán được lưu trữ theo Luật Kế toán.
4. Quyền rút lại sự đồng ý & Thu hồi nhận Email tiếp thị (Story S-54): Khách hàng có toàn quyền bấm THU HỒI QUYỀN NHẬN EMAIL TIẾP THỊ trong mục 'Cài đặt riêng tư'. Hệ thống cam kết lập tức chặn tuyệt đối (HTTP 403 Forbidden) việc gửi email tiếp thị. Khách hàng có thể kích hoạt lại bất kỳ lúc nào.
5. Quyền phản đối xử lý dữ liệu, Khiếu nại và Bồi thường thiệt hại: Theo quy định của pháp luật nếu phát hiện vi phạm.

ĐIỀU 7: THỜI HẠN LƯU TRỮ DỮ LIỆU
1. Dữ liệu tài khoản: Lưu trữ suốt thời gian tài khoản hoạt động cho đến khi có yêu cầu xóa hợp lệ.
2. Dữ liệu hóa đơn, đơn hàng: Lưu trữ từ 05 đến 10 năm theo quy định của Luật Kế toán và Thuế.
3. Nhật ký truy cập hệ thống: Lưu trữ tối thiểu 12 tháng theo Luật An ninh mạng.

ĐIỀU 8: THÔNG TIN LIÊN HỆ BỘ PHẬN BẢO VỆ DỮ LIỆU CÁ NHÂN (DPO)
- Ban Pháp chế & An toàn Thông tin - EventPulse
- Email tiếp nhận yêu cầu về dữ liệu: privacy@eventpulse.vn
- Email giải quyết khiếu nại: legal@eventpulse.vn
- Hotline hỗ trợ: 1900 6868 (08:30 - 18:00 Thứ 2 - Thứ 6)
- Cam kết thời gian phản hồi yêu cầu dữ liệu trong vòng tối đa 72 giờ làm việc.
"""
            };

            return Ok(policyInfo);
        }

        /// <summary>
        /// Lấy trạng thái chấp thuận điều khoản và sự đồng ý tiếp thị của người dùng hiện tại (AC1, AC2, AC4).
        /// </summary>
        [HttpGet("status")]
        [RequireRole]
        [ProducesResponseType(typeof(UserTermsStatusDto), StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status401Unauthorized)]
        public async Task<IActionResult> GetTermsStatus()
        {
            var user = await GetCurrentUserAsync();
            if (user == null)
            {
                return Unauthorized(new { message = "Vui lòng đăng nhập để tiếp tục." });
            }

            var statusDto = new UserTermsStatusDto
            {
                UserId = user.Id,
                Email = user.Email,
                FullName = user.FullName ?? user.Username,
                TermsVersion = user.TermsVersion,
                TermsAcceptedAt = user.TermsAcceptedAt,
                CurrentPolicyVersion = TermsPolicy.CurrentVersion,
                IsTermsCurrent = TermsPolicy.IsCurrent(user),
                MarketingEmailOptIn = user.MarketingEmailOptIn,
                CanReceiveMarketingEmails = user.MarketingEmailOptIn
            };

            return Ok(statusDto);
        }

        /// <summary>
        /// Người dùng thu hồi quyền nhận email tiếp thị (AC4).
        /// Sau khi thu hồi, hệ thống không gửi email tiếp thị nữa.
        /// </summary>
        [HttpPost("revoke-marketing")]
        [RequireRole]
        [ProducesResponseType(StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status401Unauthorized)]
        public async Task<IActionResult> RevokeMarketingConsent()
        {
            var user = await GetCurrentUserAsync();
            if (user == null)
            {
                return Unauthorized(new { message = "Vui lòng đăng nhập để tiếp tục." });
            }

            user.MarketingEmailOptIn = false;
            user.UpdatedAt = DateTime.UtcNow;
            await _context.SaveChangesAsync();

            _logger.LogInformation("Người dùng {Email} (Id: {UserId}) đã thu hồi quyền nhận email tiếp thị.", user.Email, user.Id);

            return Ok(new
            {
                success = true,
                marketingEmailOptIn = false,
                message = "Đã thu hồi quyền nhận email tiếp thị. Hệ thống sẽ không gửi email tiếp thị nữa."
            });
        }

        /// <summary>
        /// Người dùng đăng ký nhận lại email tiếp thị (Tùy chọn tái kích hoạt).
        /// </summary>
        [HttpPost("opt-in-marketing")]
        [RequireRole]
        [ProducesResponseType(StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status401Unauthorized)]
        public async Task<IActionResult> OptInMarketingConsent()
        {
            var user = await GetCurrentUserAsync();
            if (user == null)
            {
                return Unauthorized(new { message = "Vui lòng đăng nhập để tiếp tục." });
            }

            user.MarketingEmailOptIn = true;
            user.UpdatedAt = DateTime.UtcNow;
            await _context.SaveChangesAsync();

            _logger.LogInformation("Người dùng {Email} (Id: {UserId}) đã đồng ý nhận lại email tiếp thị.", user.Email, user.Id);

            return Ok(new
            {
                success = true,
                marketingEmailOptIn = true,
                message = "Đã đăng ký nhận email tiếp thị và thông tin ưu đãi thành công."
            });
        }

        /// <summary>
        /// Người dùng đồng ý với phiên bản Điều khoản và Chính sách riêng tư hiện hành (AC3).
        /// </summary>
        [HttpPost("accept")]
        [RequireRole]
        [ProducesResponseType(StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status401Unauthorized)]
        public async Task<IActionResult> AcceptCurrentTerms()
        {
            var user = await GetCurrentUserAsync();
            if (user == null)
            {
                return Unauthorized(new { message = "Vui lòng đăng nhập để tiếp tục." });
            }

            TermsPolicy.AcceptCurrent(user);
            await _context.SaveChangesAsync();

            _logger.LogInformation("Người dùng {Email} (Id: {UserId}) đã đồng ý điều khoản phiên bản {Version}.",
                user.Email, user.Id, user.TermsVersion);

            return Ok(new
            {
                success = true,
                termsVersion = user.TermsVersion,
                termsAcceptedAt = user.TermsAcceptedAt,
                message = $"Đã chấp thuận Điều khoản và Chính sách riêng tư phiên bản {user.TermsVersion} thành công."
            });
        }

        /// <summary>
        /// Gửi thử nghiệm email tiếp thị đến người dùng để kiểm chứng tiêu chí:
        /// 'Người dùng thu hồi được và khi đó không nhận email tiếp thị nữa' (AC4).
        /// </summary>
        [HttpPost("send-marketing-test")]
        [RequireRole]
        [ProducesResponseType(StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status403Forbidden)]
        [ProducesResponseType(StatusCodes.Status401Unauthorized)]
        public async Task<IActionResult> SendMarketingTest([FromBody] SendMarketingTestDto? request)
        {
            var user = await GetCurrentUserAsync();
            if (user == null)
            {
                return Unauthorized(new { message = "Vui lòng đăng nhập để tiếp tục." });
            }

            // KIỂM TRA QUYỀN: Nếu người dùng đã thu hồi quyền nhận email tiếp thị -> Chặn hoàn toàn!
            if (!user.MarketingEmailOptIn)
            {
                _logger.LogWarning("Chặn gửi email tiếp thị tới {Email} vì người dùng đã thu hồi quyền nhận tiếp thị.", user.Email);
                return StatusCode(StatusCodes.Status403Forbidden, new
                {
                    success = false,
                    sent = false,
                    canReceiveMarketingEmails = false,
                    message = "Người dùng đã thu hồi quyền nhận email tiếp thị. Hệ thống từ chối gửi email tiếp thị."
                });
            }

            // Người dùng đang đồng ý nhận tiếp thị -> Gửi email
            var subject = request?.Subject ?? "🎁 Ưu đãi độc quyền vé Concert cuối tuần từ EventPulse";
            var content = request?.Content ?? "Giảm giá 20% cho đơn hàng tiếp theo khi đặt vé qua ứng dụng EventPulse trong tuần này.";

            var sent = await _emailService.SendMarketingEmailAsync(
                user.Email,
                user.FullName ?? user.Username,
                subject,
                content);

            return Ok(new
            {
                success = true,
                sent = sent,
                canReceiveMarketingEmails = true,
                message = $"Email tiếp thị đã được gửi thành công đến {user.Email}."
            });
        }

        /// <summary>
        /// Quản trị viên cập nhật phiên bản Điều khoản / Chính sách riêng tư của toàn hệ thống (AC3).
        /// </summary>
        [HttpPost("admin/change-policy-version")]
        [RequireRole("Admin")]
        [ProducesResponseType(StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status400BadRequest)]
        [ProducesResponseType(StatusCodes.Status401Unauthorized)]
        public IActionResult ChangePolicyVersion([FromBody] UpdatePolicyVersionDto request)
        {
            if (string.IsNullOrWhiteSpace(request?.NewVersion))
            {
                return BadRequest(new { message = "Phiên bản mới không được để trống." });
            }

            TermsPolicy.SetGlobalVersion(request.NewVersion.Trim());
            _logger.LogInformation("Quản trị viên đã đổi phiên bản chính sách hệ thống sang {Version}", TermsPolicy.CurrentVersion);

            return Ok(new
            {
                success = true,
                currentVersion = TermsPolicy.CurrentVersion,
                message = $"Đã cập nhật phiên bản chính sách hệ thống thành công sang {TermsPolicy.CurrentVersion}."
            });
        }

        private async Task<User?> GetCurrentUserAsync()
        {
            var userIdValue = User.FindFirstValue(ClaimTypes.NameIdentifier)
                              ?? User.FindFirst("id")?.Value
                              ?? User.FindFirst(JwtRegisteredClaimNames.Sub)?.Value;

            if (!Guid.TryParse(userIdValue, out var userId))
            {
                return null;
            }

            return await _context.Users.FirstOrDefaultAsync(u => u.Id == userId);
        }
    }
}
