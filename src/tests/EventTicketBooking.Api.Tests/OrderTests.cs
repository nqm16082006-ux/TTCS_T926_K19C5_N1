using System;
using System.Collections.Generic;
using EventTicketBooking.Api.Models;
using Xunit;

namespace EventTicketBooking.Api.Tests
{
    public class OrderTests
    {
        [Fact]
        public void CalculateTotal_EmptyOrder_ReturnsZero()
        {
            // Arrange
            var order = new Order
            {
                OrderItems = new List<OrderItem>()
            };

            // Act
            order.CalculateTotal();

            // Assert
            Assert.Equal(0, order.TotalAmount);
        }

        [Fact]
        public void CalculateTotal_MultipleCategories_CalculatesCorrectly()
        {
            // Arrange
            var order = new Order
            {
                OrderItems = new List<OrderItem>
                {
                    new OrderItem { Price = 100000 }, // Standard
                    new OrderItem { Price = 200000 }, // VIP
                    new OrderItem { Price = 200000 }  // VIP
                }
            };

            // Act
            order.CalculateTotal();

            // Assert
            Assert.Equal(500000, order.TotalAmount);
        }

        [Fact]
        public void CalculateTotal_PriceChangedAfterBooking_KeepsBookedPrice()
        {
            // Arrange
            var seatCategory = new SeatCategory { Id = Guid.NewGuid(), Name = "VIP", Price = 200000 };
            var seat = new Seat { Id = Guid.NewGuid(), SeatCategory = seatCategory, Row = "A", SeatNumber = 1 };

            // Giả lập lúc đặt đơn, lấy giá lúc đó (200k)
            var orderItem = new OrderItem { Seat = seat, SeatId = seat.Id, Price = seatCategory.Price.Value };
            var order = new Order
            {
                OrderItems = new List<OrderItem> { orderItem }
            };

            // Act - Tính lần đầu
            order.CalculateTotal();
            int totalBeforeChange = order.TotalAmount;

            // Mô phỏng việc admin đổi giá hạng ghế sau khi đặt
            seatCategory.Price = 300000;

            // Act - Tính lại (sử dụng CalculateTotal)
            order.CalculateTotal();
            int totalAfterChange = order.TotalAmount;

            // Assert - Tổng phải dựa trên OrderItem.Price (giá lúc đặt), không bị ảnh hưởng bởi thay đổi ở hạng ghế
            Assert.Equal(200000, totalBeforeChange);
            Assert.Equal(200000, totalAfterChange);
        }
    }
}
