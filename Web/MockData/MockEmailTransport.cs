using System;
using System.Threading;
using System.Threading.Tasks;
using MimeKit;
using Web.Services;

namespace Web.MockData
{
    /// <summary>
    /// No-op email transport for MockData environment.
    /// Returns a successful result with a fake message ID so the transport
    /// abstraction works end-to-end without real SMTP or SendGrid credentials.
    /// </summary>
    public sealed class MockEmailTransport : IEmailTransport
    {
        public string ProviderName => "Mock";

        public Task<EmailSendResult> SendAsync(
            MimeMessage message,
            string jobId,
            string recipientEmail,
            string category,
            CancellationToken ct
        )
        {
            return Task.FromResult(
                new EmailSendResult
                {
                    Success = true,
                    ProviderMessageId = $"mock-{Guid.NewGuid():N}",
                    ProviderName = ProviderName,
                }
            );
        }
    }
}
