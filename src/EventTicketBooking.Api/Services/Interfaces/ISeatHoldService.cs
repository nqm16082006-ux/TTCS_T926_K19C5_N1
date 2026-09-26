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
        ServerError
    }

    public class HoldSeatsResult
    {
        public HoldSeatsResultStatus Status { get; set; }
        public string Message { get; set; } = string.Empty;
        public HoldSeatsResponseDto? Data { get; set; }

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

        public static HoldSeatsResult ConflictResult(string message)
        {
            return new HoldSeatsResult { Status = HoldSeatsResultStatus.Conflict, Message = message };
        }

        public static HoldSeatsResult ErrorResult(string message)
        {
            return new HoldSeatsResult { Status = HoldSeatsResultStatus.ServerError, Message = message };
        }
    }

    public interface ISeatHoldService
    {
        Task<HoldSeatsResult> HoldSeatsAsync(Guid showtimeId, List<Guid> seatIds, Guid userId, CancellationToken cancellationToken = default);
    }
}
