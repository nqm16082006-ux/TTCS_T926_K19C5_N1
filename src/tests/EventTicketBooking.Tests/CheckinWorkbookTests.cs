using System.IO.Compression;
using System.Xml.Linq;
using EventTicketBooking.Api.DTOs;
using EventTicketBooking.Api.Services;
using Xunit;

namespace EventTicketBooking.Tests;

public class CheckinWorkbookTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(2)]
    public void Workbook_HasTypedColumnsStylesAndFilters(int count)
    {
        var items = Enumerable.Range(0, count).Select(i => new CheckinExportItemDto
        {
            TicketCode = "=1+1",
            SeatNumber = "A01",
            CheckinTime = new DateTimeOffset(2026, 10, 17, 12, 30, 0, TimeSpan.FromHours(7)),
            GateName = "Cửa A",
            ConflictNote = "Dòng 1\nDòng 2",
            IsConflict = true
        }).ToList();
        var bytes = CheckinWorkbookWriter.Create(items, "Sự kiện & Âm nhạc", "Suất tối", "Tất cả cửa");
        using var zip = new ZipArchive(new MemoryStream(bytes));
        Assert.NotNull(zip.GetEntry("[Content_Types].xml"));
        foreach (var entry in zip.Entries) { using var stream = entry.Open(); XDocument.Load(stream); }
        using var sheetStream = zip.GetEntry("xl/worksheets/sheet1.xml")!.Open();
        var sheet = XDocument.Load(sheetStream);
        XNamespace ns = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
        Assert.Equal(count + 5, sheet.Descendants(ns + "row").Count());
        Assert.Equal(8, sheet.Descendants(ns + "col").Count());
        Assert.Equal("A6", sheet.Descendants(ns + "pane").Single().Attribute("topLeftCell")!.Value);
        Assert.Equal($"A5:H{Math.Max(5, count + 5)}", sheet.Descendants(ns + "autoFilter").Single().Attribute("ref")!.Value);
        Assert.Empty(sheet.Descendants(ns + "f"));
        if (count > 0)
        {
            var code = sheet.Descendants(ns + "c").Single(c => (string?)c.Attribute("r") == "A6");
            Assert.Equal("inlineStr", (string?)code.Attribute("t"));
            Assert.Equal("=1+1", code.Descendants(ns + "t").Single().Value);
            var time = sheet.Descendants(ns + "c").Single(c => (string?)c.Attribute("r") == "C6");
            Assert.Equal("5", (string?)time.Attribute("s"));
            Assert.NotNull(time.Element(ns + "v"));
        }
    }
}
