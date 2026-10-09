using EventTicketBooking.Api.Data;
using EventTicketBooking.Api.DTOs;
using EventTicketBooking.Api.Models;
using Microsoft.EntityFrameworkCore;
using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;

namespace EventTicketBooking.Api.Services;

/// <summary>
/// Service tạo và xuất sơ đồ ghế sự kiện ra PDF (1 trang duy nhất) - TTKN-127 / S-60
/// </summary>
public class SeatMapPdfService : ISeatMapPdfService
{
    private readonly AppDbContext _dbContext;

    // Định nghĩa bảng màu trực quan cho trạng thái ghế
    private static readonly string ColorSold = "#EF4444";       // Đỏ tươi - Đã bán
    private static readonly string ColorReserved = "#F59E0B";   // Vàng cam - Giữ chỗ
    private static readonly string ColorAvailable = "#10B981";  // Xanh lục ngọc - Còn trống
    private static readonly string ColorStage = "#1E293B";      // Xanh đen huyền bí cho sân khấu
    private static readonly string ColorDarkText = "#0F172A";   // Slate dark text
    private static readonly string ColorMuted = "#64748B";      // Text ghi chú

    static SeatMapPdfService()
    {
        // Kích hoạt giấy phép cộng đồng QuestPDF (bắt buộc từ v2022+)
        QuestPDF.Settings.License = LicenseType.Community;
        QuestPDF.Settings.UseSystemFonts = true;
        QuestPDF.Settings.ThrowOnMissingFontFamilies = false;
    }

    public SeatMapPdfService(AppDbContext dbContext)
    {
        _dbContext = dbContext;
    }

    /// <summary>
    /// Xuất PDF sơ đồ ghế theo request tùy biến hoặc mặc định
    /// </summary>
    public async Task<byte[]> ExportSeatMapPdfAsync(SeatMapExportRequestDto? request)
    {
        var reportData = await GetSeatMapReportDataAsync(request);
        return GeneratePdfDocument(reportData);
    }

    /// <summary>
    /// Xuất PDF sơ đồ ghế theo EventId
    /// </summary>
    public async Task<byte[]> ExportSeatMapPdfByEventIdAsync(Guid eventId)
    {
        var request = new SeatMapExportRequestDto { EventId = eventId };
        var reportData = await GetSeatMapReportDataAsync(request);
        return GeneratePdfDocument(reportData);
    }

    /// <summary>
    /// Tổng hợp và chuẩn hóa dữ liệu sơ đồ ghế
    /// </summary>
    public async Task<SeatMapReportDto> GetSeatMapReportDataAsync(SeatMapExportRequestDto? request)
    {
        Guid eventId = request?.EventId ?? Guid.NewGuid();
        string title = request?.EventTitle ?? string.Empty;
        string location = request?.Location ?? string.Empty;
        DateTime eventDate = request?.EventDate ?? DateTime.UtcNow.AddDays(7);
        string hallName = request?.HallName ?? "Khán phòng Grand Concert Hall";

        // 1. Nếu có EventId, thử lấy thông tin từ database
        if (request?.EventId.HasValue == true)
        {
            var dbEvent = await _dbContext.Events.FirstOrDefaultAsync(e => e.Id == request.EventId.Value);
            if (dbEvent != null)
            {
                if (string.IsNullOrWhiteSpace(title)) title = dbEvent.Title;
                if (string.IsNullOrWhiteSpace(location)) location = dbEvent.Location;
                eventDate = dbEvent.StartTime;
            }
        }

        // 2. Sử dụng thông tin mặc định nếu chưa có
        if (string.IsNullOrWhiteSpace(title))
        {
            title = "ĐẠI NHẠC HỘI MÙA HÈ 2026 - SUMMER MUSIC FESTIVAL";
        }
        if (string.IsNullOrWhiteSpace(location))
        {
            location = "Trung tâm Hội nghị Quốc gia, Hà Nội";
        }

        // 3. Chuẩn bị danh sách ghế (từ request hoặc sinh dữ liệu mô phỏng chân thực)
        List<SeatDto> rawSeats = request?.Seats != null && request.Seats.Any()
            ? request.Seats
            : GenerateDefaultSeatMatrix();

        // 4. Gom nhóm ghế theo hàng
        var rowGroups = rawSeats
            .GroupBy(s => s.Row.ToUpperInvariant())
            .OrderBy(g => g.Key)
            .Select(g => new SeatRowGroupDto
            {
                RowName = g.Key,
                Seats = g.OrderBy(s => s.Number).ToList()
            })
            .ToList();

        // 5. Tính toán các chỉ số thống kê
        int totalSeats = rawSeats.Count;
        int soldCount = rawSeats.Count(s => s.Status == SeatStatus.Sold);
        int reservedCount = rawSeats.Count(s => s.Status == SeatStatus.Reserved);
        int availableCount = rawSeats.Count(s => s.Status == SeatStatus.Available);
        decimal revenue = rawSeats.Where(s => s.Status == SeatStatus.Sold).Sum(s => s.Price);

        return new SeatMapReportDto
        {
            EventId = eventId,
            EventTitle = title,
            Location = location,
            EventDate = eventDate,
            HallName = hallName,
            ExportedAt = DateTime.UtcNow,
            TotalSeats = totalSeats,
            SoldCount = soldCount,
            ReservedCount = reservedCount,
            AvailableCount = availableCount,
            TotalRevenue = revenue,
            Rows = rowGroups
        };
    }

    /// <summary>
    /// Sinh file PDF 1 trang hoàn chỉnh bằng QuestPDF
    /// </summary>
    private byte[] GeneratePdfDocument(SeatMapReportDto report)
    {
        int rowCount = report.Rows.Count;
        // Tính toán chiều cao ô ghế và font động để vừa khít 1 trang A4 Landscape
        float seatHeight = rowCount > 10 ? 19f : (rowCount > 7 ? 24f : 28f);
        float seatFontSize = rowCount > 10 ? 6.5f : 7.5f;

        var document = Document.Create(container =>
        {
            container.Page(page =>
            {
                // Sử dụng khổ ngang A4 (Landscape: 842 x 595 pt) chuẩn cho sơ đồ ghế
                page.Size(PageSizes.A4.Landscape());
                page.Margin(18, Unit.Point);
                page.PageColor(Colors.White);
                page.DefaultTextStyle(x => x.FontFamily("Arial").FontSize(8.5f).FontColor(Color.FromHex(ColorDarkText)));

                // HEADER
                page.Header().Column(col =>
                {
                    col.Item().Row(r =>
                    {
                        r.RelativeItem().Column(c =>
                        {
                            c.Item().Row(subRow =>
                            {
                                subRow.AutoItem().Text("HỆ THỐNG QUẢN LÝ VÉ SỰ KIỆN")
                                    .FontSize(8)
                                    .Bold()
                                    .FontColor(Color.FromHex("#2563EB"));

                                subRow.AutoItem().PaddingHorizontal(6).Text("|").FontSize(8).FontColor(Colors.Grey.Medium);

                                subRow.AutoItem().Container()
                                    .Background(Color.FromHex("#F1F5F9"))
                                    .PaddingHorizontal(5)
                                    .PaddingVertical(1)
                                    .Text("Task TTKN-127 / S-60")
                                    .FontSize(7.5f)
                                    .Bold()
                                    .FontColor(Color.FromHex("#475569"));
                            });

                            c.Item().PaddingTop(2).Text("BÁO CÁO SƠ ĐỒ TRẠNG THÁI GHẾ NGỒI")
                                .FontSize(15)
                                .Bold()
                                .FontColor(Color.FromHex("#0F172A"));
                        });

                        r.ConstantItem(240).AlignRight().Column(c =>
                        {
                            c.Item().Text($"Thời gian xuất: {report.ExportedAt:dd/MM/yyyy HH:mm:ss} UTC")
                                .FontSize(7.5f)
                                .FontColor(Color.FromHex(ColorMuted));
                            c.Item().Text($"Khán phòng: {report.HallName}")
                                .FontSize(8)
                                .Bold()
                                .FontColor(Color.FromHex("#334155"));
                        });
                    });

                    // Khung thông tin tóm tắt sự kiện
                    col.Item().PaddingTop(4).Container()
                        .Background(Color.FromHex("#F8FAFC"))
                        .Border(1)
                        .BorderColor(Color.FromHex("#E2E8F0"))
                        .PaddingHorizontal(10)
                        .PaddingVertical(5)
                        .Row(r =>
                        {
                            r.RelativeItem(3).Column(c =>
                            {
                                c.Item().Text(t =>
                                {
                                    t.Span("Sự kiện: ").Bold().FontColor(Color.FromHex("#475569"));
                                    t.Span(report.EventTitle).Bold().FontColor(Color.FromHex("#1E293B"));
                                });
                            });

                            r.RelativeItem(3).Column(c =>
                            {
                                c.Item().Text(t =>
                                {
                                    t.Span("Địa điểm: ").Bold().FontColor(Color.FromHex("#475569"));
                                    t.Span(report.Location).FontColor(Color.FromHex("#334155"));
                                });
                            });

                            r.RelativeItem(2).AlignRight().Column(c =>
                            {
                                c.Item().Text(t =>
                                {
                                    t.Span("Ngày diễn ra: ").Bold().FontColor(Color.FromHex("#475569"));
                                    t.Span($"{report.EventDate:dd/MM/yyyy HH:mm}").FontColor(Color.FromHex("#334155"));
                                });
                            });
                        });

                    // Khối thẻ thống kê (Summary KPI Cards)
                    col.Item().PaddingTop(6).Row(r =>
                    {
                        r.Spacing(6);

                        // Card 1: Tổng số ghế
                        r.RelativeItem().Container()
                            .Background(Color.FromHex("#F1F5F9"))
                            .Border(1).BorderColor(Color.FromHex("#CBD5E1"))
                            .Padding(4)
                            .Column(c =>
                            {
                                c.Item().Text("TỔNG SỐ GHẾ").FontSize(7).Bold().FontColor(Color.FromHex("#475569"));
                                c.Item().Text($"{report.TotalSeats}").FontSize(11).Bold().FontColor(Color.FromHex("#0F172A"));
                            });

                        // Card 2: Đã bán (Sold)
                        r.RelativeItem().Container()
                            .Background(Color.FromHex("#FEF2F2"))
                            .Border(1).BorderColor(Color.FromHex("#FCA5A5"))
                            .Padding(4)
                            .Column(c =>
                            {
                                c.Item().Text("ĐÃ BÁN (SOLD)").FontSize(7).Bold().FontColor(Color.FromHex("#B91C1C"));
                                c.Item().Text($"{report.SoldCount} ({report.SoldPercentage}%)").FontSize(11).Bold().FontColor(Color.FromHex("#DC2626"));
                            });

                        // Card 3: Giữ chỗ (Reserved)
                        r.RelativeItem().Container()
                            .Background(Color.FromHex("#FFFBEB"))
                            .Border(1).BorderColor(Color.FromHex("#FCD34D"))
                            .Padding(4)
                            .Column(c =>
                            {
                                c.Item().Text("GIỮ CHỖ (RESERVED)").FontSize(7).Bold().FontColor(Color.FromHex("#B45309"));
                                c.Item().Text($"{report.ReservedCount} ({report.ReservedPercentage}%)").FontSize(11).Bold().FontColor(Color.FromHex("#D97706"));
                            });

                        // Card 4: Còn trống (Available)
                        r.RelativeItem().Container()
                            .Background(Color.FromHex("#ECFDF5"))
                            .Border(1).BorderColor(Color.FromHex("#6EE7B7"))
                            .Padding(4)
                            .Column(c =>
                            {
                                c.Item().Text("CÒN TRỐNG (AVAILABLE)").FontSize(7).Bold().FontColor(Color.FromHex("#047857"));
                                c.Item().Text($"{report.AvailableCount} ({report.AvailablePercentage}%)").FontSize(11).Bold().FontColor(Color.FromHex("#059669"));
                            });

                        // Card 5: Tỷ lệ lấp đầy & Doanh thu
                        r.RelativeItem().Container()
                            .Background(Color.FromHex("#EFF6FF"))
                            .Border(1).BorderColor(Color.FromHex("#93C5FD"))
                            .Padding(4)
                            .Column(c =>
                            {
                                c.Item().Text("TỶ LỆ LẤP ĐẦY").FontSize(7).Bold().FontColor(Color.FromHex("#1D4ED8"));
                                c.Item().Text($"{report.SoldPercentage}%").FontSize(11).Bold().FontColor(Color.FromHex("#2563EB"));
                            });
                    });
                });

                // CONTENT: KHU VỰC SƠ ĐỒ GHẾ
                page.Content().PaddingTop(8).Column(col =>
                {
                    // 1. Khu vực Sân khấu / Stage
                    col.Item().AlignCenter().Width(380).Container()
                        .Background(Color.FromHex(ColorStage))
                        .Border(1).BorderColor(Color.FromHex("#0F172A"))
                        .PaddingVertical(4)
                        .AlignCenter()
                        .Text("★ KHU VỰC SÂN KHẤU / MÀN HÌNH CHÍNH (STAGE) ★")
                        .FontSize(8.5f)
                        .Bold()
                        .FontColor(Colors.White);

                    col.Item().PaddingTop(2).AlignCenter()
                        .Text("▼  HƯỚNG NHÌN VỀ SÂN KHẤU  ▼")
                        .FontSize(6.5f)
                        .Bold()
                        .FontColor(Color.FromHex(ColorMuted));

                    // 2. Lưới sơ đồ ghế (Seat Map Grid)
                    col.Item().PaddingTop(4).Column(gridCol =>
                    {
                        gridCol.Spacing(rowCount > 10 ? 2 : 3);

                        foreach (var rowGroup in report.Rows)
                        {
                            gridCol.Item().Row(seatRow =>
                            {
                                // Nhãn hàng ghế bên trái (Row Letter)
                                seatRow.ConstantItem(20)
                                    .Height(seatHeight)
                                    .Container()
                                    .Background(Color.FromHex("#334155"))
                                    .AlignCenter()
                                    .AlignMiddle()
                                    .Text(rowGroup.RowName)
                                    .FontSize(8)
                                    .Bold()
                                    .FontColor(Colors.White);

                                seatRow.ConstantItem(6); // Khoảng cách đệm

                                // Danh sách các ghế trong hàng
                                seatRow.RelativeItem().Row(r =>
                                {
                                    r.Spacing(3);

                                    foreach (var seat in rowGroup.Seats)
                                    {
                                        string bgHex = seat.Status switch
                                        {
                                            SeatStatus.Sold => ColorSold,
                                            SeatStatus.Reserved => ColorReserved,
                                            _ => ColorAvailable
                                        };

                                        string statusText = seat.Status switch
                                        {
                                            SeatStatus.Sold => "ĐÃ BÁN",
                                            SeatStatus.Reserved => "GIỮ",
                                            _ => "TRỐNG"
                                        };

                                        r.RelativeItem()
                                            .Height(seatHeight)
                                            .Container()
                                            .Background(Color.FromHex(bgHex))
                                            .Border(0.5f)
                                            .BorderColor(Colors.White)
                                            .AlignCenter()
                                            .AlignMiddle()
                                            .Column(c =>
                                            {
                                                c.Item().AlignCenter().Text(seat.DisplayCode)
                                                    .FontSize(seatFontSize)
                                                    .Bold()
                                                    .FontColor(Colors.White);

                                                if (seatHeight >= 22f)
                                                {
                                                    c.Item().AlignCenter().Text(statusText)
                                                        .FontSize(5.5f)
                                                        .FontColor(Colors.White);
                                                }
                                            });
                                    }
                                });

                                seatRow.ConstantItem(6); // Khoảng cách đệm

                                // Nhãn hàng ghế bên phải (Row Letter đối xứng)
                                seatRow.ConstantItem(20)
                                    .Height(seatHeight)
                                    .Container()
                                    .Background(Color.FromHex("#334155"))
                                    .AlignCenter()
                                    .AlignMiddle()
                                    .Text(rowGroup.RowName)
                                    .FontSize(8)
                                    .Bold()
                                    .FontColor(Colors.White);
                            });
                        }
                    });

                    // 3. Phần Chú Giải Màu Sắc (Legend)
                    col.Item().PaddingTop(8).Container()
                        .Background(Color.FromHex("#F8FAFC"))
                        .Border(1)
                        .BorderColor(Color.FromHex("#E2E8F0"))
                        .PaddingVertical(4)
                        .PaddingHorizontal(12)
                        .Row(r =>
                        {
                            r.AutoItem().AlignMiddle().Text("CHÚ GIẢI TRẠNG THÁI:")
                                .FontSize(7.5f)
                                .Bold()
                                .FontColor(Color.FromHex("#334155"));

                            r.ConstantItem(12);

                            // Legend: Đã bán
                            r.AutoItem().AlignMiddle().Row(leg =>
                            {
                                leg.ConstantItem(12).Height(12).Container()
                                    .Background(Color.FromHex(ColorSold))
                                    .Border(0.5f).BorderColor(Colors.Grey.Lighten1);
                                leg.ConstantItem(4);
                                leg.AutoItem().Text($"Ghế đã bán (Sold): {report.SoldCount} ghế ({report.SoldPercentage}%)")
                                    .FontSize(7.5f).Bold().FontColor(Color.FromHex("#B91C1C"));
                            });

                            r.ConstantItem(16);

                            // Legend: Giữ chỗ
                            r.AutoItem().AlignMiddle().Row(leg =>
                            {
                                leg.ConstantItem(12).Height(12).Container()
                                    .Background(Color.FromHex(ColorReserved))
                                    .Border(0.5f).BorderColor(Colors.Grey.Lighten1);
                                leg.ConstantItem(4);
                                leg.AutoItem().Text($"Ghế giữ chỗ (Reserved): {report.ReservedCount} ghế ({report.ReservedPercentage}%)")
                                    .FontSize(7.5f).Bold().FontColor(Color.FromHex("#B45309"));
                            });

                            r.ConstantItem(16);

                            // Legend: Còn trống
                            r.AutoItem().AlignMiddle().Row(leg =>
                            {
                                leg.ConstantItem(12).Height(12).Container()
                                    .Background(Color.FromHex(ColorAvailable))
                                    .Border(0.5f).BorderColor(Colors.Grey.Lighten1);
                                leg.ConstantItem(4);
                                leg.AutoItem().Text($"Ghế còn trống (Available): {report.AvailableCount} ghế ({report.AvailablePercentage}%)")
                                    .FontSize(7.5f).Bold().FontColor(Color.FromHex("#047857"));
                            });
                        });
                });

                // FOOTER: Cố định thông tin & Đảm bảo 1 trang duy nhất
                page.Footer().PaddingTop(4).Row(r =>
                {
                    r.RelativeItem().Text(t =>
                    {
                        t.Span("Hệ thống Bán Vé Sự Kiện Trực Tuyến © 2026 | Báo cáo nội bộ Ban Tổ Chức")
                            .FontSize(7).FontColor(Color.FromHex(ColorMuted));
                    });

                    r.RelativeItem().AlignRight().Text(t =>
                    {
                        t.Span("Định dạng: 1 Trang Hoàn Chỉnh | ")
                            .FontSize(7).FontColor(Color.FromHex(ColorMuted));
                        t.Span("Trang 1/1").FontSize(7).Bold().FontColor(Color.FromHex("#1E293B"));
                    });
                });
            });
        });

        return document.GeneratePdf();
    }

    /// <summary>
    /// Sinh ma trận ghế mẫu tiêu chuẩn thực tế (8 hàng A-H, mỗi hàng 12 ghế = 96 ghế)
    /// </summary>
    private static List<SeatDto> GenerateDefaultSeatMatrix()
    {
        var seats = new List<SeatDto>();
        string[] rows = { "A", "B", "C", "D", "E", "F", "G", "H" };
        int seatsPerRow = 12;

        // Mô phỏng trạng thái thực tế:
        // Hàng A, B là VIP: Giá 1.200.000đ, đa số đã bán hoặc giữ chỗ
        // Hàng C, D, E là Standard: Giá 750.000đ
        // Hàng F, G, H là Economy: Giá 450.000đ
        for (int r = 0; r < rows.Length; r++)
        {
            string rowLetter = rows[r];
            string seatType = r < 2 ? "VIP" : (r < 5 ? "Standard" : "Economy");
            decimal price = r < 2 ? 1200000m : (r < 5 ? 750000m : 450000m);

            for (int num = 1; num <= seatsPerRow; num++)
            {
                SeatStatus status;

                // Tỉ lệ phân bổ thực tế đẹp mắt
                if (r < 2) // VIP
                {
                    status = (num is 3 or 9) ? SeatStatus.Reserved
                        : (num is 6 or 7) ? SeatStatus.Available
                        : SeatStatus.Sold;
                }
                else if (r < 5) // Standard
                {
                    status = (num is 1 or 2 or 11 or 12) ? SeatStatus.Available
                        : (num is 4 or 8) ? SeatStatus.Reserved
                        : SeatStatus.Sold;
                }
                else // Economy
                {
                    status = (num % 3 == 0) ? SeatStatus.Sold
                        : (num % 4 == 0) ? SeatStatus.Reserved
                        : SeatStatus.Available;
                }

                seats.Add(new SeatDto
                {
                    Id = Guid.NewGuid().ToString(),
                    Row = rowLetter,
                    Number = num,
                    SeatCode = $"{rowLetter}{num}",
                    SeatType = seatType,
                    Price = price,
                    Status = status
                });
            }
        }

        return seats;
    }
}
