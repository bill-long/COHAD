#nullable enable
using System;
using Web.Models;

namespace Web.PresentationModels
{
    /// <summary>
    /// DTO for individual delivery events (admin diagnostics).
    /// </summary>
    public class EmailDeliveryEventDetail
    {
        public string Email { get; set; } = "";

        public DeliveryStatus DeliveryStatus { get; set; }

        public string? ProviderEventType { get; set; }

        public string? ProviderEventId { get; set; }

        public string? ProviderMessageId { get; set; }

        public string? Provider { get; set; }

        public DateTime ReceivedUtc { get; set; }

        public string? ProviderPayloadJson { get; set; }

        public static EmailDeliveryEventDetail FromEvent(EmailDeliveryEvent evt, bool includePayload)
        {
            return new EmailDeliveryEventDetail
            {
                Email = evt.Email,
                DeliveryStatus = evt.DeliveryStatus,
                ProviderEventType = evt.ProviderEventType,
                ProviderEventId = evt.ProviderEventId,
                ProviderMessageId = evt.ProviderMessageId,
                Provider = evt.Provider,
                ReceivedUtc = evt.ReceivedUtc,
                ProviderPayloadJson = includePayload ? evt.ProviderPayloadJson : null,
            };
        }
    }
}
