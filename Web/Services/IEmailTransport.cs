using System.Threading;
using System.Threading.Tasks;
using MimeKit;

namespace Web.Services
{
    public class EmailSendResult
    {
        public bool Success { get; set; }

        /// <summary>
        /// Provider-specific message ID for webhook correlation (e.g. sg_message_id, SES MessageId).
        /// </summary>
        public string ProviderMessageId { get; set; }

        /// <summary>
        /// Provider name stored on the recipient (e.g. "SendGrid", "Ses").
        /// </summary>
        public string ProviderName { get; set; }

        public string Error { get; set; }
    }

    /// <summary>
    /// Abstraction over email transport providers (SMTP/SendGrid, SES).
    /// Each implementation handles connection lifecycle and provider-specific details.
    /// </summary>
    public interface IEmailTransport
    {
        /// <summary>
        /// Sends a fully-constructed MimeMessage through the provider.
        /// The transport may add provider-specific headers (e.g. X-SMTPAPI for SendGrid correlation).
        /// </summary>
        Task<EmailSendResult> SendAsync(MimeMessage message, string jobId, string recipientEmail, CancellationToken ct);

        /// <summary>
        /// Provider name for logging and the recipient's Provider field.
        /// </summary>
        string ProviderName { get; }
    }
}
