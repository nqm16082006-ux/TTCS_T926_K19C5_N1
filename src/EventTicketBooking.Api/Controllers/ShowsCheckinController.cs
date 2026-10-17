using System.Security.Claims;
using EventTicketBooking.Api.Data;
using EventTicketBooking.Api.DTOs;
using EventTicketBooking.Api.Middlewares;
using EventTicketBooking.Api.Services;
using EventTicketBooking.Api.Services.Interfaces;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace EventTicketBooking.Api.Controllers;

[ApiController]
[Route("api/shows/{showId:guid}/checkins")]
[RequireRole("Admin", "Organizer")]
public class ShowsCheckinController(
    ICheckinExportService exportService,
    ILogger<ShowsCheckinController> logger,
    AppDbContext db) : ControllerBase
{
    private async Task<IActionResult?> CheckAccess(Guid showId, CancellationToken ct)
    {
        if (showId == Guid.Empty) return BadRequest(new { message = "Mã suất diễn không hợp lệ." });
        if (User.Identity?.IsAuthenticated != true) return Unauthorized();
        if (!User.IsInRole("Admin") && !User.IsInRole("Organizer")) return Forbid();
        var show = await db.Showtimes.AsNoTracking().Where(s => s.Id == showId)
            .Select(s => new { s.Event.OwnerId }).SingleOrDefaultAsync(ct);
        if (show == null) return NotFound(new { message = "Không tìm thấy suất diễn." });
        var id = User.FindFirstValue(ClaimTypes.NameIdentifier) ?? User.FindFirstValue("id") ?? User.FindFirstValue("sub");
        if (!User.IsInRole("Admin") && (!Guid.TryParse(id, out var userId) || userId != show.OwnerId))
        {
            SecurityAuditLogger.LogOwnershipDenied(HttpContext, "ShowsCheckinExport", showId.ToString());
            return StatusCode(403, new { message = "Bạn không có quyền xuất dữ liệu của sự kiện này." });
        }
        return null;
    }

    [HttpGet("export-csv")]
    [HttpGet("/api/v1/checkin/showtimes/{showId:guid}/export-csv")]
    public async Task<IActionResult> ExportCheckinListCsv(
        Guid showId, string? gateName = null, string? doorName = null,
        string? gateId = null, string? doorId = null, bool? onlyConflicts = null,
        bool includeNotCheckedIn = false, CancellationToken cancellationToken = default,
        bool includeScanLog = false)
    {
        var denied = await CheckAccess(showId, cancellationToken);
        if (denied != null) return denied;
        var query = Query(showId, gateName ?? doorName ?? gateId ?? doorId, onlyConflicts, includeNotCheckedIn, includeScanLog);
        var rows = await exportService.GetCheckinExportDataAsync(query, cancellationToken);
        logger.LogInformation("Checkin export: show {ShowId}, actor {Actor}, audit {Audit}, rows {Count}",
            showId, User.FindFirstValue(ClaimTypes.NameIdentifier), includeScanLog, rows.Count);
        Response.Headers.CacheControl = "no-store";
        return File(exportService.GenerateCsvBytes(rows), "text/csv; charset=utf-8", $"danh-sach-checkin-suat-{showId}.csv");
    }

    [HttpGet("export-excel")]
    public async Task<IActionResult> ExportExcel(Guid showId, string? gateName = null,
        bool? onlyConflicts = null, bool includeScanLog = false, CancellationToken cancellationToken = default)
    {
        var denied = await CheckAccess(showId, cancellationToken);
        if (denied != null) return denied;
        var rows = await exportService.GetCheckinExportDataAsync(Query(showId, gateName, onlyConflicts, false, includeScanLog), cancellationToken);
        var show = await db.Showtimes.AsNoTracking().Where(s => s.Id == showId)
            .Select(s => new { s.Event.Title, s.Name, s.StartTime }).SingleAsync(cancellationToken);
        var filters = (includeScanLog ? "Nhật ký đối soát" : "Danh sách đã vào cửa")
            + (string.IsNullOrWhiteSpace(gateName) ? " · Tất cả cửa" : $" · Cửa: {gateName.Trim()}")
            + (onlyConflicts == true ? " · Chỉ dòng có xung đột" : "");
        Response.Headers.CacheControl = "no-store";
        logger.LogInformation("Checkin XLSX export: show {ShowId}, actor {Actor}, rows {Count}", showId,
            User.FindFirstValue(ClaimTypes.NameIdentifier), rows.Count);
        return File(CheckinWorkbookWriter.Create(rows, show.Title, show.Name ?? show.StartTime.ToString("dd/MM/yyyy"), filters),
            "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", $"danh-sach-checkin-suat-{showId}.xlsx");
    }

    [HttpGet("export-preview")]
    public async Task<IActionResult> Preview(Guid showId, string? gateName = null,
        bool? onlyConflicts = null, bool includeScanLog = false, CancellationToken cancellationToken = default)
    {
        var denied = await CheckAccess(showId, cancellationToken);
        if (denied != null) return denied;
        var rows = await exportService.GetCheckinExportDataAsync(Query(showId, gateName, onlyConflicts, false, includeScanLog), cancellationToken);
        Response.Headers.CacheControl = "no-store";
        return Ok(new
        {
            items = rows.Take(200),
            totalRows = rows.Count,
            admittedCount = rows.Count(r => r.Result == "Đã vào"),
            uniqueTickets = rows.Where(r => r.Result == "Đã vào").Select(r => r.TicketCode).Distinct(StringComparer.OrdinalIgnoreCase).Count(),
            conflictCount = rows.Count(r => r.IsConflict),
            timeZone = "Asia/Ho_Chi_Minh",
            previewLimit = 200
        });
    }

    private static ExportCheckinListQuery Query(Guid id, string? gate, bool? conflicts, bool notCheckedIn, bool audit) => new()
    {
        ShowId = id,
        GateName = gate,
        OnlyConflicts = conflicts,
        IncludeNotCheckedIn = notCheckedIn,
        IncludeScanLog = audit
    };
}
