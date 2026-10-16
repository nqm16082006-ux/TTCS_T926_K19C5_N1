using System;
using System.Collections.Generic;
using System.Linq;

namespace EventTicketBooking.Api.Models
{
    /// <summary>
    /// Centralized definition of the 5 allowed system roles in EventTicketBooking.
    /// S-02 Invariant: Exactly 5 roles exist, every user has exactly 1 role, no role inheritance.
    /// </summary>
    public static class SystemRoles
    {
        public const string Admin = "Admin";
        public const string Organizer = "Organizer";
        public const string Staff = "Staff";
        public const string Customer = "Customer";
        public const string Auditor = "Auditor";

        public static readonly IReadOnlyList<string> All = new[]
        {
            Admin,
            Organizer,
            Staff,
            Customer,
            Auditor
        };

        public static bool IsValid(string? role)
        {
            if (string.IsNullOrWhiteSpace(role)) return false;
            return All.Contains(role, StringComparer.Ordinal);
        }
    }
}
