<<<<<<< HEAD
﻿namespace EventTicketBooking.Api.Models;

public class Seat
{
    public int Id { get; set; }
    public int ShowtimeId { get; set; }
    public string Status { get; set; } = "AVAILABLE"; // AVAILABLE, HELD, SOLD
}
=======
using System;

namespace EventTicketBooking.Api.Models
{
    public class Seat
    {
        public Guid Id { get; set; } = Guid.NewGuid();

        public Guid ShowtimeId { get; set; }

        public Guid SeatCategoryId { get; set; }

        public string Row { get; set; } = string.Empty;

        public int SeatNumber { get; set; }

        public Showtime Showtime { get; set; } = null!;

        public SeatCategory SeatCategory { get; set; } = null!;
    }
}
>>>>>>> b0c1f8ec9976b1e05175d677d1c6c02ec1d9723c
