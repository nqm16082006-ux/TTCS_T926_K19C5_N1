using System.Globalization;
using System.IO.Compression;
using System.Xml.Linq;
using EventTicketBooking.Api.DTOs;

namespace EventTicketBooking.Api.Services;

/// <summary>Writes a real XLSX workbook; text is stored as text, never interpreted as a formula.</summary>
public static class CheckinWorkbookWriter
{
    private static readonly XNamespace Ns = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
    public static byte[] Create(IReadOnlyList<CheckinExportItemDto> items, string eventTitle, string showName, string filters)
    {
        using var output = new MemoryStream();
        using (var zip = new ZipArchive(output, ZipArchiveMode.Create, true))
        {
            void Part(string path, string xml)
            {
                using var writer = new StreamWriter(zip.CreateEntry(path).Open(), new System.Text.UTF8Encoding(false));
                writer.Write(xml);
            }
            XElement Cell(string address, string? text, int style) => new(Ns + "c", new XAttribute("r", address),
                new XAttribute("s", style), new XAttribute("t", "inlineStr"),
                new XElement(Ns + "is", new XElement(Ns + "t", new XAttribute(XNamespace.Xml + "space", "preserve"), text ?? "")));
            XElement Row(int n, double height, params XElement[] cells) => new(Ns + "row", new XAttribute("r", n),
                new XAttribute("ht", height), new XAttribute("customHeight", 1), cells);
            var data = new XElement(Ns + "sheetData",
                Row(1, 32, Cell("A1", "DANH SÁCH KHÁCH VÀO CỬA", 1)),
                Row(2, 32, Cell("A2", $"{eventTitle} · {showName}", 3)),
                Row(3, 26, Cell("A3", $"{items.Count} dòng · {items.Count(i => i.Result == "Đã vào")} lượt vào · {items.Where(i => i.Result == "Đã vào").Select(i => i.TicketCode).Distinct(StringComparer.OrdinalIgnoreCase).Count()} vé duy nhất · {items.Count(i => i.IsConflict)} dòng có xung đột", 3)),
                Row(4, 28, Cell("A4", $"{filters} · Giờ Việt Nam (UTC+7)", 3)));
            string[] headings = ["Mã vé", "Ghế", "Giờ vào / quét (UTC+7)", "Cửa vào", "Loại lượt", "Kết quả", "Xung đột", "Ghi chú"];
            data.Add(Row(5, 32, headings.Select((h, c) => Cell($"{(char)('A' + c)}5", h, 2)).ToArray()));
            for (int index = 0; index < items.Count; index++)
            {
                var item = items[index]; int n = index + 6;
                int style = index % 2 == 0 ? 3 : 4;
                var values = new[] { item.TicketCode, item.SeatNumber, "", item.GateName, item.EntryType, item.Result, item.IsConflict ? "Có" : "Không", item.ConflictNote };
                var cells = values.Select((v, c) => Cell($"{(char)('A' + c)}{n}", v, c == 6 && item.IsConflict ? 7 : style)).ToArray();
                if (item.CheckinTime.HasValue)
                {
                    double serial = item.CheckinTime.Value.ToOffset(TimeSpan.FromHours(7)).DateTime.ToOADate();
                    cells[2] = new XElement(Ns + "c", new XAttribute("r", $"C{n}"), new XAttribute("s", index % 2 == 0 ? 5 : 6),
                        new XElement(Ns + "v", serial.ToString(CultureInfo.InvariantCulture)));
                }
                int noteLines = (item.ConflictNote ?? "").Split('\n').Sum(line => Math.Max(1, (line.Length + 54) / 55));
                data.Add(Row(n, Math.Clamp(noteLines * 16 + 12, 36, 240), cells));
            }
            var widths = new double[] { 32, 10, 26, 16, 24, 16, 14, 66 };
            var sheet = new XElement(Ns + "worksheet",
                new XElement(Ns + "sheetViews", new XElement(Ns + "sheetView", new XAttribute("workbookViewId", 0),
                    new XElement(Ns + "pane", new XAttribute("ySplit", 5), new XAttribute("topLeftCell", "A6"), new XAttribute("activePane", "bottomLeft"), new XAttribute("state", "frozen")))),
                new XElement(Ns + "sheetFormatPr", new XAttribute("defaultRowHeight", 20)),
                new XElement(Ns + "cols", widths.Select((w, c) => new XElement(Ns + "col", new XAttribute("min", c + 1), new XAttribute("max", c + 1), new XAttribute("width", w), new XAttribute("customWidth", 1)))),
                data,
                new XElement(Ns + "autoFilter", new XAttribute("ref", $"A5:H{Math.Max(5, items.Count + 5)}")),
                new XElement(Ns + "mergeCells", new XAttribute("count", 4), Enumerable.Range(1, 4).Select(n => new XElement(Ns + "mergeCell", new XAttribute("ref", $"A{n}:H{n}")))),
                new XElement(Ns + "pageMargins", new XAttribute("left", .25), new XAttribute("right", .25), new XAttribute("top", .5), new XAttribute("bottom", .5), new XAttribute("header", .2), new XAttribute("footer", .2)),
                new XElement(Ns + "pageSetup", new XAttribute("orientation", "landscape"), new XAttribute("paperSize", 9)));
            Part("xl/worksheets/sheet1.xml", sheet.ToString());
            Part("[Content_Types].xml", """
                <Types xmlns="http://schemas.openxmlformats.org/package/2006/content-types"><Default Extension="rels" ContentType="application/vnd.openxmlformats-package.relationships+xml"/><Default Extension="xml" ContentType="application/xml"/><Override PartName="/xl/workbook.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml"/><Override PartName="/xl/worksheets/sheet1.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml"/><Override PartName="/xl/styles.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.styles+xml"/></Types>
                """);
            Part("_rels/.rels", """
                <Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships"><Relationship Id="rId1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument" Target="xl/workbook.xml"/></Relationships>
                """);
            Part("xl/workbook.xml", """
                <workbook xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main" xmlns:r="http://schemas.openxmlformats.org/officeDocument/2006/relationships"><sheets><sheet name="Danh sách vào cửa" sheetId="1" r:id="rId1"/></sheets></workbook>
                """);
            Part("xl/_rels/workbook.xml.rels", """
                <Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships"><Relationship Id="rId1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet" Target="worksheets/sheet1.xml"/><Relationship Id="rId2" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/styles" Target="styles.xml"/></Relationships>
                """);
            Part("xl/styles.xml", """
                <styleSheet xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main">
                <numFmts count="1"><numFmt numFmtId="164" formatCode="dd/mm/yyyy hh:mm:ss"/></numFmts>
                <fonts count="4"><font><sz val="11"/><name val="Calibri"/><color rgb="FF17243B"/></font><font><b/><sz val="11"/><name val="Calibri"/><color rgb="FFFFFFFF"/></font><font><b/><sz val="18"/><name val="Calibri"/><color rgb="FF4430CE"/></font><font><b/><sz val="11"/><name val="Calibri"/><color rgb="FFA51F37"/></font></fonts>
                <fills count="5"><fill><patternFill patternType="none"/></fill><fill><patternFill patternType="gray125"/></fill><fill><patternFill patternType="solid"><fgColor rgb="FF4430CE"/><bgColor indexed="64"/></patternFill></fill><fill><patternFill patternType="solid"><fgColor rgb="FFF2F3FC"/><bgColor indexed="64"/></patternFill></fill><fill><patternFill patternType="solid"><fgColor rgb="FFFFE9EE"/><bgColor indexed="64"/></patternFill></fill></fills>
                <borders count="2"><border><left/><right/><top/><bottom/><diagonal/></border><border><left/><right/><top/><bottom style="thin"><color rgb="FFE0E4EF"/></bottom><diagonal/></border></borders>
                <cellStyleXfs count="1"><xf numFmtId="0" fontId="0" fillId="0" borderId="0"/></cellStyleXfs>
                <cellXfs count="8">
                <xf numFmtId="0" fontId="0" fillId="0" borderId="0" xfId="0"/>
                <xf numFmtId="0" fontId="2" fillId="0" borderId="0" xfId="0" applyAlignment="1"><alignment vertical="center"/></xf>
                <xf numFmtId="0" fontId="1" fillId="2" borderId="1" xfId="0" applyAlignment="1"><alignment vertical="center" wrapText="1"/></xf>
                <xf numFmtId="0" fontId="0" fillId="0" borderId="1" xfId="0" applyAlignment="1"><alignment vertical="center" wrapText="1"/></xf>
                <xf numFmtId="0" fontId="0" fillId="3" borderId="1" xfId="0" applyAlignment="1"><alignment vertical="center" wrapText="1"/></xf>
                <xf numFmtId="164" fontId="0" fillId="0" borderId="1" xfId="0" applyNumberFormat="1" applyAlignment="1"><alignment vertical="center" wrapText="1"/></xf>
                <xf numFmtId="164" fontId="0" fillId="3" borderId="1" xfId="0" applyNumberFormat="1" applyAlignment="1"><alignment vertical="center" wrapText="1"/></xf>
                <xf numFmtId="0" fontId="3" fillId="4" borderId="1" xfId="0" applyAlignment="1"><alignment vertical="center" wrapText="1"/></xf>
                </cellXfs><cellStyles count="1"><cellStyle name="Normal" xfId="0" builtinId="0"/></cellStyles></styleSheet>
                """);
        }
        return output.ToArray();
    }
}
