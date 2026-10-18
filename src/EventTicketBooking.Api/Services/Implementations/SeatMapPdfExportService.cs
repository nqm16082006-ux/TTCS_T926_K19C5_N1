using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using EventTicketBooking.Api.Data;
using EventTicketBooking.Api.DTOs;
using EventTicketBooking.Api.Models;
using EventTicketBooking.Api.Services.Interfaces;
using Microsoft.EntityFrameworkCore;
using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;

namespace EventTicketBooking.Api.Services.Implementations
{
    public class SeatMapPdfExportService : ISeatMapPdfExportService
    {
        private readonly AppDbContext _context;

        static SeatMapPdfExportService()
        {
            // Bắt buộc cấu hình giấy phép Community cho QuestPDF
            QuestPDF.Settings.License = LicenseType.Community;
            QuestPDF.Settings.UseSystemFonts = true;
            QuestPDF.Settings.ThrowOnMissingFontFamilies = false;
        }

        public SeatMapPdfExportService(AppDbContext context)
        {
            _context = context ?? throw new ArgumentNullException(nameof(context));
        }

        public async Task<SeatMapExportDataDto> GetSeatMapDataAsync(Guid showtimeId, DateTime? nowOverride = null, CancellationToken cancellationToken = default)
        {
            var showtime = await _context.Showtimes
                .AsNoTracking()
                .Include(s => s.Event)
                .FirstOrDefaultAsync(s => s.Id == showtimeId, cancellationToken);

            if (showtime == null)
            {
                throw new KeyNotFoundException($"Không tìm thấy suất diễn với ID {showtimeId}.");
            }

            var now = nowOverride ?? DateTime.UtcNow;

            // 1. Lấy danh sách ID ghế đang được giữ chỗ còn hiệu lực
            var activeHeldSeatIds = await _context.SeatHold
                .AsNoTracking()
                .Where(sh => sh.Seat.ShowtimeId == showtimeId && sh.Status == "ACTIVE" && sh.ExpiresAt > now)
                .Select(sh => sh.SeatId)
                .ToListAsync(cancellationToken);

            // 2. Lấy danh sách ID ghế thuộc các đơn hàng đã thanh toán (Order Paid)
            var paidSeatIds = await _context.OrderItems
                .AsNoTracking()
                .Where(oi => oi.Order.ShowtimeId == showtimeId && oi.Order.Status == OrderStatus.Paid)
                .Select(oi => oi.SeatId)
                .ToListAsync(cancellationToken);

            // 3. Lấy toàn bộ ghế của suất diễn
            var rawSeats = await (from s in _context.Seats.AsNoTracking().Where(s => s.ShowtimeId == showtimeId)
                                  join c in _context.SeatCategories.AsNoTracking() on s.SeatCategoryId equals c.Id
                                  select new
                                  {
                                      s.Id,
                                      s.Row,
                                      s.SeatNumber,
                                      CategoryName = c.Name,
                                      Price = c.Price,
                                      s.Status
                                  }).ToListAsync(cancellationToken);

            var items = rawSeats.Select(s =>
            {
                string status;
                if (s.Status == "SOLD" || paidSeatIds.Contains(s.Id))
                {
                    status = "SOLD";
                }
                else if (activeHeldSeatIds.Contains(s.Id) || (s.Status == "HELD" && activeHeldSeatIds.Contains(s.Id)))
                {
                    status = "HELD";
                }
                else
                {
                    status = "AVAILABLE";
                }

                return new SeatMapExportItemDto
                {
                    Id = s.Id,
                    Row = (s.Row ?? string.Empty).Trim().ToUpperInvariant(),
                    SeatNumber = s.SeatNumber,
                    CategoryName = s.CategoryName,
                    Price = s.Price,
                    Status = status
                };
            })
            .OrderBy(s => s.Row)
            .ThenBy(s => s.SeatNumber)
            .ToList();

            var data = new SeatMapExportDataDto
            {
                ShowtimeId = showtime.Id,
                ShowtimeName = !string.IsNullOrWhiteSpace(showtime.Name) ? showtime.Name : $"Suất diễn {showtime.StartTime:dd/MM/yyyy HH:mm}",
                StartTime = showtime.StartTime,
                EndTime = showtime.EndTime,
                EventId = showtime.EventId,
                EventTitle = showtime.Event?.Title ?? "Sự kiện",
                Location = showtime.Event?.Location ?? "Chưa xác định",
                ExportedAt = now,
                Seats = items
            };

            return data;
        }

        public byte[] GenerateSeatMapPdf(SeatMapExportDataDto data)
        {
            var document = Document.Create(container =>
            {
                container.Page(page =>
                {
                    // Dùng khổ A4 ngang (Landscape) để hiển thị sơ đồ ghế rộng rãi nhất
                    page.Size(PageSizes.A4.Landscape());
                    page.Margin(18, Unit.Point);
                    page.DefaultTextStyle(x => x.FontSize(9).FontColor(Colors.Grey.Darken4));

                    page.Header().Element(c => ComposeHeader(c, data));

                    // Toàn bộ phần thân (Stage + Lưới ghế) được gói trong ScaleToFit để tuyệt đối vừa khít 1 trang
                    page.Content().Element(c => ComposeContent(c, data));

                    page.Footer().Element(c => ComposeFooter(c, data));
                });
            });

            return document.GeneratePdf();
        }

        public async Task<byte[]> ExportSeatMapPdfAsync(Guid showtimeId, DateTime? nowOverride = null, CancellationToken cancellationToken = default)
        {
            var data = await GetSeatMapDataAsync(showtimeId, nowOverride, cancellationToken);
            return GenerateSeatMapPdf(data);
        }

        #region QuestPDF Composition Components

        private void ComposeHeader(IContainer container, SeatMapExportDataDto data)
        {
            container.PaddingBottom(8).Row(row =>
            {
                row.RelativeItem().Column(col =>
                {
                    col.Item().Row(r =>
                    {
                        r.AutoItem().Background(Colors.Blue.Darken2).PaddingHorizontal(6).PaddingVertical(2).Text("S-60 REPORT")
                            .FontColor(Colors.White).FontSize(8).Bold();
                        r.AutoItem().PaddingLeft(6).Text("SƠ ĐỒ GHẾ VÀ TRẠNG THÁI BÁN VÉ")
                            .FontSize(14).Bold().FontColor(Colors.Blue.Darken4);
                    });

                    col.Item().PaddingTop(2).Text(text =>
                    {
                        text.Span("Sự kiện: ").Bold();
                        text.Span(data.EventTitle).Bold().FontColor(Colors.Grey.Darken3);
                        text.Span("   |   Địa điểm: ").Bold();
                        text.Span(data.Location);
                    });

                    col.Item().PaddingTop(1).Text(text =>
                    {
                        text.Span("Suất diễn: ").Bold();
                        text.Span(data.ShowtimeName);
                        text.Span($" ({data.StartTime:dd/MM/yyyy HH:mm} - {data.EndTime:HH:mm})");
                    });
                });

                row.AutoItem().AlignRight().Column(col =>
                {
                    col.Item().Border(1).BorderColor(Colors.Grey.Lighten2).Background(Colors.Grey.Lighten4)
                        .PaddingHorizontal(8).PaddingVertical(4).Column(subCol =>
                        {
                            subCol.Item().Text("THỜI ĐIỂM XUẤT REAL-TIME").FontSize(7).Bold().FontColor(Colors.Grey.Darken2);
                            subCol.Item().Text($"{data.ExportedAt:dd/MM/yyyy HH:mm:ss} UTC").FontSize(9).Bold().FontColor(Colors.Blue.Darken3);
                            subCol.Item().Text("Chính xác từng giây").FontSize(7).Italic().FontColor(Colors.Grey.Darken1);
                        });
                });
            });
        }

        private void ComposeContent(IContainer container, SeatMapExportDataDto data)
        {
            // Bọc toàn bộ Content trong ScaleToFit để tự động co giãn vừa khít không nhảy trang
            container.ScaleToFit().Column(col =>
            {
                // 1. Khối màn hình / Sân khấu (Screen / Stage)
                col.Item().PaddingBottom(10).AlignCenter().Width(380).Height(24)
                    .Background(Colors.Grey.Lighten3)
                    .Border(1).BorderColor(Colors.Grey.Lighten1)
                    .AlignCenter().AlignMiddle()
                    .Text("SÂN KHẤU / MÀN HÌNH (STAGE)").FontSize(10).Bold().FontColor(Colors.Grey.Darken3);

                // 2. Lưới ghế (Seat Map Grid)
                col.Item().Element(c => ComposeSeatGrid(c, data));
            });
        }

        private void ComposeSeatGrid(IContainer container, SeatMapExportDataDto data)
        {
            if (data.Seats == null || data.Seats.Count == 0)
            {
                container.Padding(20).AlignCenter().Text("Chưa có dữ liệu ghế cho suất diễn này.")
                    .FontSize(11).Italic().FontColor(Colors.Grey.Medium);
                return;
            }

            // Nhóm theo hàng
            var rows = data.Seats
                .GroupBy(s => s.Row)
                .OrderBy(g => g.Key)
                .ToList();

            // Tìm số ghế tối đa trong 1 hàng
            int maxSeatsInRow = rows.Max(g => g.Count());

            container.AlignCenter().Column(gridCol =>
            {
                foreach (var rowGroup in rows)
                {
                    gridCol.Item().PaddingVertical(2).Row(seatRow =>
                    {
                        // Nhãn hàng bên trái (ví dụ "A")
                        seatRow.AutoItem().Width(24).Height(22)
                            .AlignCenter().AlignMiddle()
                            .Text(rowGroup.Key).FontSize(9).Bold().FontColor(Colors.Grey.Darken3);

                        // Ghế trong hàng
                        seatRow.RelativeItem().Row(r =>
                        {
                            var orderedSeats = rowGroup.OrderBy(s => s.SeatNumber).ToList();
                            foreach (var seat in orderedSeats)
                            {
                                r.AutoItem().Padding(1.5f).Element(cell => ComposeSeatCell(cell, seat));
                            }
                        });

                        // Nhãn hàng bên phải
                        seatRow.AutoItem().Width(24).Height(22)
                            .AlignCenter().AlignMiddle()
                            .Text(rowGroup.Key).FontSize(9).Bold().FontColor(Colors.Grey.Darken3);
                    });
                }
            });
        }

        private void ComposeSeatCell(IContainer container, SeatMapExportItemDto seat)
        {
            // Xác định màu sắc theo trạng thái
            string bgColor;
            string borderColor;
            string textColor;

            switch (seat.Status)
            {
                case "SOLD":
                    bgColor = "#EF4444";     // Đỏ đậm nổi bật
                    borderColor = "#DC2626";
                    textColor = "#FFFFFF";
                    break;
                case "HELD":
                    bgColor = "#F59E0B";     // Vàng hổ phách
                    borderColor = "#D97706";
                    textColor = "#FFFFFF";
                    break;
                case "AVAILABLE":
                default:
                    bgColor = "#10B981";     // Xanh lục tươi
                    borderColor = "#059669";
                    textColor = "#FFFFFF";
                    break;
            }

            container
                .Width(28)
                .Height(20)
                .Background(bgColor)
                .Border(1)
                .BorderColor(borderColor)
                .AlignCenter()
                .AlignMiddle()
                .Text(seat.SeatCode)
                .FontSize(7.5f)
                .Bold()
                .FontColor(textColor);
        }

        private void ComposeFooter(IContainer container, SeatMapExportDataDto data)
        {
            container.PaddingTop(6).BorderTop(1).BorderColor(Colors.Grey.Lighten2).Column(col =>
            {
                col.Item().Row(row =>
                {
                    // Chú giải (Legend)
                    row.RelativeItem(3).Row(legendRow =>
                    {
                        legendRow.AutoItem().AlignMiddle().Text("CHÚ GIẢI: ").FontSize(8.5f).Bold().FontColor(Colors.Grey.Darken3);

                        // Sold
                        legendRow.AutoItem().PaddingLeft(8).Row(r =>
                        {
                            r.AutoItem().Width(14).Height(12).Background("#EF4444").Border(0.5f).BorderColor("#DC2626");
                            r.AutoItem().PaddingLeft(4).AlignMiddle()
                                .Text($"Đã bán ({data.SoldCount} - {data.SoldPercentage}%)").FontSize(8).Bold().FontColor(Colors.Grey.Darken3);
                        });

                        // Held
                        legendRow.AutoItem().PaddingLeft(12).Row(r =>
                        {
                            r.AutoItem().Width(14).Height(12).Background("#F59E0B").Border(0.5f).BorderColor("#D97706");
                            r.AutoItem().PaddingLeft(4).AlignMiddle()
                                .Text($"Đang giữ chỗ ({data.HeldCount} - {data.HeldPercentage}%)").FontSize(8).Bold().FontColor(Colors.Grey.Darken3);
                        });

                        // Available
                        legendRow.AutoItem().PaddingLeft(12).Row(r =>
                        {
                            r.AutoItem().Width(14).Height(12).Background("#10B981").Border(0.5f).BorderColor("#059669");
                            r.AutoItem().PaddingLeft(4).AlignMiddle()
                                .Text($"Còn trống ({data.AvailableCount} - {data.AvailablePercentage}%)").FontSize(8).Bold().FontColor(Colors.Grey.Darken3);
                        });
                    });

                    // Thống kê tổng số
                    row.RelativeItem(2).AlignRight().Text(text =>
                    {
                        text.Span("Tổng số ghế: ").FontSize(8.5f).Bold();
                        text.Span($"{data.TotalSeats} ghế").FontSize(9).Bold().FontColor(Colors.Blue.Darken3);
                        text.Span(" | Trang 1/1 (Single-page A4)").FontSize(7.5f).Italic().FontColor(Colors.Grey.Darken1);
                    });
                });
            });
        }

        #endregion
    }
}
