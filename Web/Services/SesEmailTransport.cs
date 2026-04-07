using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Amazon.SimpleEmailV2;
using Amazon.SimpleEmailV2.Model;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MimeKit;
using Web.Configuration;

namespace Web.Services
{
    /// <summary>
    /// Sends emails through Amazon SES v2 API using raw MIME messages.
    /// Uses the standard AWS credential chain (env vars, IAM role, ~/.aws/credentials).
    /// </summary>
    public sealed class SesEmailTransport : IEmailTransport, IDisposable
    {
        private readonly IAmazonSimpleEmailServiceV2 _sesClient;
        private readonly SesOptions _options;
        private readonly ILogger<SesEmailTransport> _logger;

        public string ProviderName => "Ses";

        public SesEmailTransport(
            IAmazonSimpleEmailServiceV2 sesClient,
            IOptions<SesOptions> options,
            ILogger<SesEmailTransport> logger
        )
        {
            _sesClient = sesClient;
            _options = options.Value;
            _logger = logger;
        }

        public async Task<EmailSendResult> SendAsync(
            MimeMessage message,
            string jobId,
            string recipientEmail,
            CancellationToken ct
        )
        {
            try
            {
                // Serialize MimeMessage to raw bytes
                using var memoryStream = new MemoryStream();
                await message.WriteToAsync(memoryStream, ct);
                memoryStream.Position = 0;

                var request = new SendEmailRequest
                {
                    Content = new EmailContent { Raw = new RawMessage { Data = memoryStream } },
                    // Explicit From so SES doesn't have to parse the raw MIME envelope
                    FromEmailAddress = message.From.Mailboxes.FirstOrDefault()?.Address,
                    Destination = new Destination
                    {
                        ToAddresses = message.To.Mailboxes.Select(m => m.Address).ToList(),
                        CcAddresses = message.Cc.Mailboxes.Select(m => m.Address).ToList(),
                        BccAddresses = message.Bcc.Mailboxes.Select(m => m.Address).ToList(),
                    },
                    EmailTags = new()
                    {
                        new MessageTag { Name = "cohad_job_id", Value = jobId },
                        new MessageTag { Name = "cohad_email", Value = recipientEmail },
                    },
                };

                if (!string.IsNullOrEmpty(_options.ConfigurationSetName))
                    request.ConfigurationSetName = _options.ConfigurationSetName;

                if (!string.IsNullOrEmpty(_options.FromArn))
                    request.FromEmailAddressIdentityArn = _options.FromArn;

                var response = await _sesClient.SendEmailAsync(request, ct);

                return new EmailSendResult
                {
                    Success = true,
                    ProviderMessageId = response.MessageId,
                    ProviderName = ProviderName,
                };
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "SES send failed for {Email} in job {JobId}", recipientEmail, jobId);

                return new EmailSendResult
                {
                    Success = false,
                    ProviderName = ProviderName,
                    Error = ex.Message,
                };
            }
        }

        public void Dispose()
        {
            _sesClient?.Dispose();
        }
    }
}
