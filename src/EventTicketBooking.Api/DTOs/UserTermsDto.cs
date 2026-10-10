using System;

namespace EventTicketBooking.Api.DTOs
{
    public class UserTermsStatusDto
    {
        public Guid UserId { get; set; }
        public string Email { get; set; } = string.Empty;
        public string FullName { get; set; } = string.Empty;
        public string TermsVersion { get; set; } = string.Empty;
        public DateTime? TermsAcceptedAt { get; set; }
        public string CurrentPolicyVersion { get; set; } = string.Empty;
        public bool IsTermsCurrent { get; set; }
        public bool MarketingEmailOptIn { get; set; }
        public bool CanReceiveMarketingEmails { get; set; }
    }

    public class UpdatePolicyVersionDto
    {
        public string NewVersion { get; set; } = string.Empty;
    }

    public class SendMarketingTestDto
    {
        public string? Subject { get; set; }
        public string? Content { get; set; }
    }

    public class TermsPolicyInfoDto
    {
        public string Version { get; set; } = string.Empty;
        public string EffectiveDate { get; set; } = string.Empty;
        public string Title { get; set; } = string.Empty;
        public string Summary { get; set; } = string.Empty;
        public string TermsContent { get; set; } = string.Empty;
        public string PrivacyContent { get; set; } = string.Empty;
    }
}
