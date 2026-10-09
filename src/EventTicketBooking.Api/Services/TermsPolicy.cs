using EventTicketBooking.Api.Models;

namespace EventTicketBooking.Api.Services
{
    public static class TermsPolicy
    {
        public const string CurrentVersion = "v1.0";

        public static bool IsCurrent(User user) =>
            user.TermsAcceptedAt.HasValue &&
            string.Equals(user.TermsVersion, CurrentVersion, StringComparison.Ordinal);

        public static void AcceptCurrent(User user)
        {
            user.TermsVersion = CurrentVersion;
            user.TermsAcceptedAt = DateTime.UtcNow;
            user.UpdatedAt = DateTime.UtcNow;
        }
    }
}
