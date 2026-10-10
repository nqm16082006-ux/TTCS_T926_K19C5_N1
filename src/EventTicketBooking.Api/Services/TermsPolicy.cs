using System;
using System.Threading;
using EventTicketBooking.Api.Models;

namespace EventTicketBooking.Api.Services
{
    public static class TermsPolicy
    {
        public const string DefaultVersion = "v1.0";
        private static readonly AsyncLocal<string?> _scopedVersion = new();
        private static volatile string _globalVersion = DefaultVersion;

        public static string CurrentVersion
        {
            get => _scopedVersion.Value ?? _globalVersion;
            set => _scopedVersion.Value = string.IsNullOrWhiteSpace(value) ? null : value.Trim();
        }

        public static void SetGlobalVersion(string version)
        {
            _globalVersion = string.IsNullOrWhiteSpace(version) ? DefaultVersion : version.Trim();
        }

        public static void SetCurrentVersion(string version)
        {
            CurrentVersion = version;
        }

        public static void ResetToDefault()
        {
            _scopedVersion.Value = null;
            _globalVersion = DefaultVersion;
        }

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
