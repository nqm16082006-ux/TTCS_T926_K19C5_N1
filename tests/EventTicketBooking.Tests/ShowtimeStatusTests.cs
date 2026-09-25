using System;
using System.Reflection;
using EventTicketBooking.Api.Models;
using Xunit;

namespace EventTicketBooking.Tests
{
    public class ShowtimeStatusTests
    {
        [Fact]
        public void Showtime_InitialStatus_ShouldBeDraft()
        {
            // Arrange & Act
            var showtime = new Showtime();

            // Assert
            Assert.Equal(ShowtimeStatus.Draft, showtime.Status);
        }

        [Fact]
        public void ChangeStatus_DraftToOnSale_WithSeats_ShouldSucceed()
        {
            // Arrange
            var showtime = new Showtime { AvailableSeats = 100 };

            // Act
            showtime.ChangeStatus(ShowtimeStatus.OnSale);

            // Assert
            Assert.Equal(ShowtimeStatus.OnSale, showtime.Status);
        }

        [Fact]
        public void ChangeStatus_DraftToOnSale_WithoutSeats_ShouldThrowInvalidOperationException()
        {
            // Arrange
            var showtime = new Showtime { AvailableSeats = 0 };

            // Act & Assert
            var ex = Assert.Throws<InvalidOperationException>(() => showtime.ChangeStatus(ShowtimeStatus.OnSale));
            Assert.Contains("chưa có ghế", ex.Message);
            Assert.Equal(ShowtimeStatus.Draft, showtime.Status);
        }

        [Fact]
        public void ChangeStatus_OnSaleToClosed_ShouldSucceed()
        {
            // Arrange
            var showtime = new Showtime { AvailableSeats = 50 };
            showtime.ChangeStatus(ShowtimeStatus.OnSale);

            // Act
            showtime.ChangeStatus(ShowtimeStatus.Closed);

            // Assert
            Assert.Equal(ShowtimeStatus.Closed, showtime.Status);
        }

        [Theory]
        [InlineData(ShowtimeStatus.Closed)] // Draft -> Closed (bỏ bước OnSale)
        public void ChangeStatus_DraftToInvalidStatus_ShouldThrowInvalidOperationException(ShowtimeStatus targetStatus)
        {
            // Arrange
            var showtime = new Showtime { AvailableSeats = 10 };

            // Act & Assert
            Assert.Throws<InvalidOperationException>(() => showtime.ChangeStatus(targetStatus));
            Assert.Equal(ShowtimeStatus.Draft, showtime.Status);
        }

        [Fact]
        public void ChangeStatus_OnSaleToDraft_ShouldThrowInvalidOperationException()
        {
            // Arrange
            var showtime = new Showtime { AvailableSeats = 50 };
            showtime.ChangeStatus(ShowtimeStatus.OnSale);

            // Act & Assert
            Assert.Throws<InvalidOperationException>(() => showtime.ChangeStatus(ShowtimeStatus.Draft));
            Assert.Equal(ShowtimeStatus.OnSale, showtime.Status);
        }

        [Theory]
        [InlineData(ShowtimeStatus.Draft)]
        [InlineData(ShowtimeStatus.OnSale)]
        public void ChangeStatus_ClosedToOtherStatus_ShouldThrowInvalidOperationException(ShowtimeStatus targetStatus)
        {
            // Arrange
            var showtime = new Showtime { AvailableSeats = 50 };
            showtime.ChangeStatus(ShowtimeStatus.OnSale);
            showtime.ChangeStatus(ShowtimeStatus.Closed);

            // Act & Assert
            Assert.Throws<InvalidOperationException>(() => showtime.ChangeStatus(targetStatus));
            Assert.Equal(ShowtimeStatus.Closed, showtime.Status);
        }

        [Fact]
        public void ChangeStatus_ToCurrentStatus_ShouldThrowInvalidOperationException()
        {
            // Arrange
            var showtime = new Showtime { AvailableSeats = 10 };

            // Act & Assert
            Assert.Throws<InvalidOperationException>(() => showtime.ChangeStatus(ShowtimeStatus.Draft));
        }

        [Fact]
        public void StatusProperty_Setter_ShouldNotBePublic()
        {
            // Arrange & Act
            var propInfo = typeof(Showtime).GetProperty(nameof(Showtime.Status));
            var setMethod = propInfo?.GetSetMethod(nonPublic: false);

            // Assert
            Assert.Null(setMethod); // Ensure setter is private or not public
        }
    }
}
