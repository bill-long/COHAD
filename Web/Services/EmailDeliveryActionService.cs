#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Web.Models;
using Web.Services.Repositories;

namespace Web.Services
{
    public interface IEmailDeliveryActionService
    {
        /// <summary>
        /// Processes a delivery event and takes automatic action (e.g. opt-out on bounce/spam).
        /// </summary>
        Task ProcessDeliveryEventAsync(string email, DeliveryStatus status, string? category);
    }

    public class EmailDeliveryActionService : IEmailDeliveryActionService
    {
        private readonly IHomeRepository _homeRepository;
        private readonly IResidentRepository _residentRepository;
        private readonly IAuditLogRepository _auditLogRepository;
        private readonly ILogger<EmailDeliveryActionService> _logger;

        public EmailDeliveryActionService(
            IHomeRepository homeRepository,
            IResidentRepository residentRepository,
            IAuditLogRepository auditLogRepository,
            ILogger<EmailDeliveryActionService> logger
        )
        {
            _homeRepository = homeRepository;
            _residentRepository = residentRepository;
            _auditLogRepository = auditLogRepository;
            _logger = logger;
        }

        public async Task ProcessDeliveryEventAsync(string email, DeliveryStatus status, string? category)
        {
            if (status != DeliveryStatus.Bounced && status != DeliveryStatus.SpamReport)
            {
                _logger.LogDebug("Delivery event {Status} for {Email} — no auto-action required.", status, email);
                return;
            }

            var reason = status == DeliveryStatus.Bounced ? "hard bounce" : "spam report";
            _logger.LogInformation("Auto-opting-out email {Email} due to {Reason}.", email, reason);

            var allHomes = await _homeRepository.GetAllAsync();
            var matchingHomes = allHomes
                .Where(h => string.Equals(h.EmailAddress?.Address?.Trim(), email, StringComparison.OrdinalIgnoreCase))
                .ToList();

            foreach (var home in matchingHomes)
            {
                OptOutEmailAddress(home.EmailAddress);
                await _homeRepository.UpsertAsync(home);
                _logger.LogInformation("Opted-out home email for home {HomeId}.", home.Id);
            }

            var allResidents = await _residentRepository.GetAllAsync();
            var matchingResidents = allResidents
                .Where(r =>
                    r.EmailAddresses?.Any(ea =>
                        string.Equals(ea.Address?.Trim(), email, StringComparison.OrdinalIgnoreCase)
                    ) == true
                )
                .ToList();

            foreach (var resident in matchingResidents)
            {
                foreach (
                    var ea in resident.EmailAddresses.Where(ea =>
                        string.Equals(ea.Address?.Trim(), email, StringComparison.OrdinalIgnoreCase)
                    )
                )
                {
                    OptOutEmailAddress(ea);
                }
                await _residentRepository.UpsertAsync(resident);
                _logger.LogInformation(
                    "Opted-out resident email for resident {ResidentId} on home {HomeId}.",
                    resident.Id,
                    resident.HomeId
                );
            }

            var totalOptOuts = matchingHomes.Count + matchingResidents.Count;
            if (totalOptOuts > 0)
            {
                // Redact email in audit log (show first 3 chars + domain)
                var redacted = RedactEmail(email);
                await _auditLogRepository.AddAsync(
                    new NewAuditLogEntry
                    {
                        Id = Guid.NewGuid(),
                        Time = DateTime.UtcNow,
                        UserId = "system",
                        UserDisplayName = "System (auto)",
                        SubjectId = email,
                        SubjectName = redacted,
                        Action = $"Auto-opted-out due to {reason}. {totalOptOuts} address(es) updated.",
                    }
                );
            }
        }

        private static void OptOutEmailAddress(EmailAddress ea)
        {
            ea.BoardEmailOptedIn = false;
            ea.WelcomeEmailOptedIn = false;
            ea.GardenClubEmailOptedIn = false;
            ea.SocialCommitteeEmailOptedIn = false;
            ea.SunshineCommitteeEmailOptedIn = false;
        }

        internal static string RedactEmail(string email)
        {
            var atIndex = email.IndexOf('@');
            if (atIndex <= 0)
                return "***";
            var localPart = email[..atIndex];
            var domain = email[atIndex..];
            var visible = localPart.Length <= 3 ? localPart : localPart[..3];
            return visible + "***" + domain;
        }
    }
}
