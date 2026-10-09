using EventTicketBooking.Api.DTOs;
using EventTicketBooking.Api.Services;
using Microsoft.AspNetCore.Mvc;

namespace EventTicketBooking.Api.Controllers;

/// <summary>
/// API Quản lý và xuất sơ đồ ghế sự kiện ra PDF (Task TTKN-127 / S-60)
/// </summary>
[ApiController]
[Route("api/[controller]")]
[Route("api/seats")]
[Route("api/seat-map")]
public class SeatMapController : ControllerBase
{
    private readonly ISeatMapPdfService _seatMapPdfService;
    private readonly ILogger<SeatMapController> _logger;

    public SeatMapController(ISeatMapPdfService seatMapPdfService, ILogger<SeatMapController> logger)
    {
        _seatMapPdfService = seatMapPdfService;
        _logger = logger;
    }

    /// <summary>
    /// Xuất sơ đồ ghế đã bán/giữ chỗ/trống ra file PDF 1 trang theo EventId
    /// </summary>
    /// <param name="eventId">Mã sự kiện (tùy chọn). Nếu không truyền, hệ thống sử dụng sự kiện mẫu.</param>
    /// <response code="200">Trả về file PDF (1 trang) tải xuống hoặc xem trực tiếp trên trình duyệt</response>
    [HttpGet("export-pdf")]
    [Produces("application/pdf")]
    [ProducesResponseType(typeof(FileContentResult), StatusCodes.Status200OK)]
    public async Task<IActionResult> ExportPdfByEventId([FromQuery] Guid? eventId)
    {
        try
        {
            _logger.LogInformation("Nhận yêu cầu xuất PDF sơ đồ ghế cho EventId: {EventId}", eventId);

            var request = eventId.HasValue ? new SeatMapExportRequestDto { EventId = eventId } : null;
            byte[] pdfBytes = await _seatMapPdfService.ExportSeatMapPdfAsync(request);

            string fileName = eventId.HasValue 
                ? $"SeatMap_{eventId:N}_{DateTime.UtcNow:yyyyMMddHHmmss}.pdf"
                : $"SeatMap_Demo_{DateTime.UtcNow:yyyyMMddHHmmss}.pdf";

            return File(pdfBytes, "application/pdf", fileName);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Lỗi xảy ra khi xuất PDF sơ đồ ghế cho EventId: {EventId}", eventId);
            return StatusCode(StatusCodes.Status500InternalServerError, new { Message = "Đã xảy ra lỗi khi tạo file PDF sơ đồ ghế", Detail = ex.Message });
        }
    }

    /// <summary>
    /// Xuất sơ đồ ghế ra file PDF 1 trang từ dữ liệu tùy biến (Body JSON)
    /// </summary>
    /// <param name="request">Thông tin sự kiện và danh sách ghế kèm trạng thái tô màu (Sold, Reserved, Available)</param>
    /// <response code="200">Trả về file PDF 1 trang hoàn chỉnh</response>
    [HttpPost("export-pdf")]
    [Produces("application/pdf")]
    [ProducesResponseType(typeof(FileContentResult), StatusCodes.Status200OK)]
    public async Task<IActionResult> ExportPdfCustom([FromBody] SeatMapExportRequestDto request)
    {
        try
        {
            _logger.LogInformation("Nhận yêu cầu xuất PDF sơ đồ ghế tùy biến: {Title}", request?.EventTitle);

            byte[] pdfBytes = await _seatMapPdfService.ExportSeatMapPdfAsync(request);

            string sanitizedTitle = string.IsNullOrWhiteSpace(request?.EventTitle) 
                ? "CustomEvent" 
                : string.Join("_", request.EventTitle.Split(Path.GetInvalidFileNameChars()));

            string fileName = $"SeatMap_{sanitizedTitle}_{DateTime.UtcNow:yyyyMMddHHmmss}.pdf";

            return File(pdfBytes, "application/pdf", fileName);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Lỗi xảy ra khi xuất PDF sơ đồ ghế tùy biến");
            return StatusCode(StatusCodes.Status500InternalServerError, new { Message = "Đã xảy ra lỗi khi tạo file PDF sơ đồ ghế", Detail = ex.Message });
        }
    }

    /// <summary>
    /// Lấy dữ liệu chi tiết sơ đồ ghế dưới dạng JSON (Xem trước thống kê, tỷ lệ lấp đầy, danh sách ghế theo hàng)
    /// </summary>
    /// <param name="eventId">Mã sự kiện</param>
    [HttpGet("map-data")]
    [Produces("application/json")]
    [ProducesResponseType(typeof(SeatMapReportDto), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetMapData([FromQuery] Guid? eventId)
    {
        try
        {
            var request = eventId.HasValue ? new SeatMapExportRequestDto { EventId = eventId } : null;
            var data = await _seatMapPdfService.GetSeatMapReportDataAsync(request);
            return Ok(data);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Lỗi xảy ra khi lấy dữ liệu sơ đồ ghế");
            return StatusCode(StatusCodes.Status500InternalServerError, new { Message = "Đã xảy ra lỗi khi truy vấn sơ đồ ghế", Detail = ex.Message });
        }
    }
}
