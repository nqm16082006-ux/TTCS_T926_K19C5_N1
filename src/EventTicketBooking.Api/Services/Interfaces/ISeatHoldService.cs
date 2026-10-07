using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using EventTicketBooking.Api.DTOs;

namespace EventTicketBooking.Api.Services.Interfaces
{
    public enum HoldSeatsResultStatus
    {
        Success,
        NotFound,
        InvalidRequest,
        Conflict,
        Forbidden,
        ServerError
    }

    public class HoldSeatsResult
    {
        public HoldSeatsResultStatus Status { get; set; }
        public string Message { get; set; } = string.Empty;
        public HoldSeatsResponseDto? Data { get; set; }
        public List<Guid>? ConflictingSeatIds { get; set; }

        public static HoldSeatsResult SuccessResult(HoldSeatsResponseDto data, string message = "Giữ chỗ ghế thành công.")
        {
            return new HoldSeatsResult { Status = HoldSeatsResultStatus.Success, Data = data, Message = message };
        }

        public static HoldSeatsResult NotFoundResult(string message)
        {
            return new HoldSeatsResult { Status = HoldSeatsResultStatus.NotFound, Message = message };
        }

        public static HoldSeatsResult InvalidResult(string message)
        {
            return new HoldSeatsResult { Status = HoldSeatsResultStatus.InvalidRequest, Message = message };
        }

        public static HoldSeatsResult ConflictResult(string message, List<Guid>? conflictingSeatIds = null)
        {
            return new HoldSeatsResult { Status = HoldSeatsResultStatus.Conflict, Message = message, ConflictingSeatIds = conflictingSeatIds };
        }

        public static HoldSeatsResult ErrorResult(string message)
        {
            return new HoldSeatsResult { Status = HoldSeatsResultStatus.ServerError, Message = message };
        }

        public static HoldSeatsResult ForbiddenResult(string message)
        {
            return new HoldSeatsResult { Status = HoldSeatsResultStatus.Forbidden, Message = message };
        }
    }

    public interface ISeatHoldService
    {
        Task<HoldSeatsResult> HoldSeatsAsync(Guid showtimeId, List<Guid> seatIds, Guid userId, CancellationToken cancellationToken = default);
        Task<HoldSeatsResult> CancelSeatHoldAsync(Guid showtimeId, Guid seatId, Guid userId, CancellationToken cancellationToken = default);
        Task<HoldSeatsResult> GetUserActiveHoldsAsync(Guid showtimeId, Guid userId, DateTime? nowOverride = null, CancellationToken cancellationToken = default);
        Task<int> GetUserTicketCountForShowtimeAsync(
    Guid showtimeId,
    Guid userId,
    CancellationToken cancellationToken = default);
    }
}
