using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using EventTicketBooking.Api.DTOs;
using EventTicketBooking.Api.DTOs.DiscountCodes;
using EventTicketBooking.Api.Models;

namespace EventTicketBooking.Api.Services.Interfaces
{
    public interface IDiscountCodeService
    {
        Task<(bool Success, string Message, DiscountCodeDto? Data, int StatusCode)> CreateDiscountCodeAsync(
            Guid eventId, CreateDiscountCodeRequest request, Guid currentUserId, bool isAdmin = false);

        Task<(bool Success, string Message, List<DiscountCodeDto>? Data, int StatusCode)> GetDiscountCodesByEventAsync(
            Guid eventId, Guid currentUserId, bool isAdmin);

        Task<(bool Success, string Message, OrderDto? Order, int StatusCode)> ApplyDiscountCodeAsync(
            Guid orderId, string code, Guid currentUserId);

        Task<bool> ReleaseDiscountReservationAsync(
            Order order, CancellationToken cancellationToken = default);

        Task<bool> ReleaseDiscountReservationAsync(
            Guid orderId, CancellationToken cancellationToken = default);
    }
}
