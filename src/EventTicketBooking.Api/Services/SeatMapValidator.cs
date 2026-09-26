using System.Collections.Generic;
using EventTicketBooking.Api.DTOs;

namespace EventTicketBooking.Api.Services
{
    public static class SeatMapValidator
    {
        public static List<SeatImportError> Validate(List<SeatImportItemDto>? items)
        {
            var errors = new List<SeatImportError>();

            if (items == null || items.Count == 0)
            {
                errors.Add(new SeatImportError { ErrorMessage = "Tệp sơ đồ ghế trống hoặc không chứa danh sách ghế hợp lệ." });
                return errors; // Nếu trống thì không cần duyệt tiếp
            }

            var importedSeatKeys = new HashSet<(string Row, int SeatNumber)>();

            for (int i = 0; i < items.Count; i++)
            {
                var item = items[i];
                var row = item.Row?.Trim();
                var categoryName = item.Category?.Trim();

                bool hasRowError = false;
                bool hasSeatNumError = false;

                // 1. Kiểm tra trường bắt buộc và độ dài
                if (string.IsNullOrEmpty(row) || row.Length > 10)
                {
                    errors.Add(new SeatImportError { SeatIndex = i, ErrorMessage = "Thiếu thông tin Hàng ghế (Row) hoặc dài hơn 10 ký tự." });
                    hasRowError = true;
                }

                if (item.SeatNumber <= 0)
                {
                    errors.Add(new SeatImportError { SeatIndex = i, ErrorMessage = "Số ghế (SeatNumber) phải lớn hơn 0." });
                    hasSeatNumError = true;
                }

                if (string.IsNullOrEmpty(categoryName) || categoryName.Length > 100)
                {
                    errors.Add(new SeatImportError { SeatIndex = i, ErrorMessage = "Thiếu thông tin Hạng ghế (Category) hoặc dài hơn 100 ký tự." });
                }

                // 2. Kiểm tra trùng lặp trong chính file (nếu các trường hợp lệ)
                if (!hasRowError && !hasSeatNumError && !string.IsNullOrEmpty(row))
                {
                    if (!importedSeatKeys.Add((row, item.SeatNumber)))
                    {
                        errors.Add(new SeatImportError { SeatIndex = i, ErrorMessage = $"Ghế {row}{item.SeatNumber} bị trùng lặp trong tệp." });
                    }
                }
            }

            return errors;
        }
    }
}
