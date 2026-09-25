using System;
using System.Collections.Generic;
using EventTicketBooking.Api.DTOs;

namespace EventTicketBooking.Api.Exceptions
{
    public class SeatValidationException : Exception
    {
        public List<SeatImportError> Errors { get; }

        public SeatValidationException(List<SeatImportError> errors)
            : base("Quá trình kiểm tra tệp sơ đồ ghế phát hiện lỗi.")
        {
            Errors = errors;
        }
    }
}
