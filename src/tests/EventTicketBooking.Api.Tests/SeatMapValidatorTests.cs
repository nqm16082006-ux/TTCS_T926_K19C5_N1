using System.Collections.Generic;
using EventTicketBooking.Api.DTOs;
using EventTicketBooking.Api.Services;
using Xunit;

namespace EventTicketBooking.Api.Tests
{
    public class SeatMapValidatorTests
    {
        [Fact]
        public void Validate_EmptyList_ReturnsError()
        {
            // Arrange
            var items = new List<SeatImportItemDto>();

            // Act
            var errors = SeatMapValidator.Validate(items);

            // Assert
            Assert.Single(errors);
            Assert.Contains("trống hoặc không chứa", errors[0].ErrorMessage);
        }

        [Fact]
        public void Validate_MissingRequiredFields_ReturnsErrors()
        {
            // Arrange
            var items = new List<SeatImportItemDto>
            {
                new SeatImportItemDto { Row = "", SeatNumber = 0, Category = "" }, // Thiếu cả 3
                new SeatImportItemDto { Row = "A", SeatNumber = 1, Category = "" }, // Thiếu category
            };

            // Act
            var errors = SeatMapValidator.Validate(items);

            // Assert
            Assert.Equal(4, errors.Count); // Ghế 0 có 3 lỗi, ghế 1 có 1 lỗi

            // Lỗi ghế 0
            Assert.Contains(errors, e => e.SeatIndex == 0 && e.ErrorMessage.Contains("Thiếu thông tin Hàng ghế"));
            Assert.Contains(errors, e => e.SeatIndex == 0 && e.ErrorMessage.Contains("Số ghế"));
            Assert.Contains(errors, e => e.SeatIndex == 0 && e.ErrorMessage.Contains("Thiếu thông tin Hạng ghế"));

            // Lỗi ghế 1
            Assert.Contains(errors, e => e.SeatIndex == 1 && e.ErrorMessage.Contains("Thiếu thông tin Hạng ghế"));
        }

        [Fact]
        public void Validate_DuplicateSeats_ReturnsError()
        {
            // Arrange
            var items = new List<SeatImportItemDto>
            {
                new SeatImportItemDto { Row = "A", SeatNumber = 1, Category = "VIP" },
                new SeatImportItemDto { Row = "B", SeatNumber = 1, Category = "VIP" },
                new SeatImportItemDto { Row = "A", SeatNumber = 1, Category = "Standard" } // Trùng A1
            };

            // Act
            var errors = SeatMapValidator.Validate(items);

            // Assert
            Assert.Single(errors);
            Assert.Equal(2, errors[0].SeatIndex);
            Assert.Contains("bị trùng lặp", errors[0].ErrorMessage);
        }

        [Fact]
        public void Validate_ValidList_ReturnsEmpty()
        {
            // Arrange
            var items = new List<SeatImportItemDto>
            {
                new SeatImportItemDto { Row = "A", SeatNumber = 1, Category = "VIP" },
                new SeatImportItemDto { Row = "B", SeatNumber = 2, Category = "Standard" }
            };

            // Act
            var errors = SeatMapValidator.Validate(items);

            // Assert
            Assert.Empty(errors);
        }
    }
}
