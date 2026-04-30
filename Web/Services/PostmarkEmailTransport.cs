using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using MailKit;
using MailKit.Net.Smtp;
using Microsoft.Extensions.Logging;
using MimeKit;
using Web.Configuration;

namespace Web.Services
{
    /// <summary>
    /// Sends emails through Postmark's SMTP relay.
    /// Manages MailKit SmtpClient lifecycle and adds Postmark-specific metadata and stream headers.
    /// </summary>
    public sealed class PostmarkEmailTransport : IEmailTransport, IDisposable
    {
        private readonly PostmarkOptions _options;
        private readonly bool _logSmtpProtocolOnFailure;
        private readonly ILogger<PostmarkEmailTransport> _logger;

        private SmtpClient _smtpClient;
        private MemoryStream _protocolLog;
        private DateTime _lastActivityUtc;

        private const int MaxProtocolLogBytes = 64 * 1024;
        private const int MaxSmtpTranscriptCharsToLog = 32 * 1024;
        private const int SmtpPort = 587;
        private const string SmtpHost = "smtp.postmarkapp.com";
        private const int TimeoutSeconds = 30;
        private const int MaxIdleSeconds = 60;
        private static readonly Regex Base64LikeRedaction = new(@"[A-Za-z0-9+/=]{40,}", RegexOptions.Compiled);

        public string ProviderName => "Postmark";

        public PostmarkEmailTransport(
            PostmarkOptions options,
            bool logSmtpProtocolOnFailure,
            ILogger<PostmarkEmailTransport> logger
        )
        {
            _options = options;
            _logSmtpProtocolOnFailure = logSmtpProtocolOnFailure;
            _logger = logger;
        }

        public async Task<EmailSendResult> SendAsync(
            MimeMessage message,
            string jobId,
            string recipientEmail,
            string category,
            CancellationToken ct
        )
        {
            try
            {
                await EnsureConnectedAsync(ct);

                _protocolLog?.SetLength(0);

                // Postmark correlation metadata (individual headers, not a JSON blob)
                message.Headers.Add("X-PM-Metadata-cohad_job_id", jobId);

                // Select the appropriate message stream based on email job category
                var streamId = IsTransactionalCategory(category)
                    ? _options.TransactionalStream
                    : _options.BroadcastStream;
                message.Headers.Add("X-PM-Message-Stream", streamId);

                await _smtpClient.SendAsync(message, ct);
                _lastActivityUtc = DateTime.UtcNow;

                return new EmailSendResult { Success = true, ProviderName = ProviderName };
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Postmark SMTP send failed for {Email} in job {JobId}", recipientEmail, jobId);

                if (_logSmtpProtocolOnFailure && _protocolLog != null)
                {
                    var transcript = FormatSmtpTranscriptForLogs(_protocolLog);
                    _logger.LogWarning(
                        "Postmark SMTP transcript (redacted) for {Email} in job {JobId}: {SmtpTranscript}",
                        recipientEmail,
                        jobId,
                        transcript
                    );
                }

                DisposeSmtpClient();

                return new EmailSendResult
                {
                    Success = false,
                    ProviderName = ProviderName,
                    Error = ex.Message,
                };
            }
        }

        private bool IsTransactionalCategory(string category)
        {
            if (string.IsNullOrEmpty(category) || _options.TransactionalCategories == null)
                return false;

            return _options.TransactionalCategories.Any(c =>
                string.Equals(c, category, StringComparison.OrdinalIgnoreCase));
        }

        private async Task EnsureConnectedAsync(CancellationToken ct)
        {
            if (
                _smtpClient != null
                && _smtpClient.IsConnected
                && MaxIdleSeconds > 0
                && (DateTime.UtcNow - _lastActivityUtc).TotalSeconds > MaxIdleSeconds
            )
            {
                _logger.LogInformation("Postmark SMTP connection idle for >{MaxIdle}s — reconnecting", MaxIdleSeconds);
                DisposeSmtpClient();
            }

            if (_smtpClient != null && _smtpClient.IsConnected)
                return;

            DisposeSmtpClient();

            if (_logSmtpProtocolOnFailure)
            {
                _protocolLog = new BoundedMemoryStream(MaxProtocolLogBytes);
                _smtpClient = new SmtpClient(new ProtocolLogger(_protocolLog));
            }
            else
            {
                _smtpClient = new SmtpClient();
            }

            if (TimeoutSeconds > 0)
                _smtpClient.Timeout = TimeoutSeconds * 1000;

            await _smtpClient.ConnectAsync(SmtpHost, SmtpPort, MailKit.Security.SecureSocketOptions.StartTls, ct);
            // Postmark uses the Server API Token as both username and password
            await _smtpClient.AuthenticateAsync(_options.ServerToken, _options.ServerToken, ct);
            _lastActivityUtc = DateTime.UtcNow;
        }

        private void DisposeSmtpClient()
        {
            if (_smtpClient != null)
            {
                try
                {
                    if (_smtpClient.IsConnected)
                        _smtpClient.Disconnect(true);
                }
                catch
                { /* best-effort disconnect */
                }
                _smtpClient.Dispose();
                _smtpClient = null;
            }
            _protocolLog?.Dispose();
            _protocolLog = null;
        }

        public void Dispose()
        {
            DisposeSmtpClient();
        }

        private static string FormatSmtpTranscriptForLogs(MemoryStream protocolLog)
        {
            if (protocolLog == null || protocolLog.Length == 0)
                return "";

            var raw = Encoding.UTF8.GetString(protocolLog.ToArray());

            var sb = new StringBuilder(raw.Length);
            using var reader = new StringReader(raw);
            string line;
            var inDataSection = false;
            while ((line = reader.ReadLine()) != null)
            {
                var trimmed = line.TrimStart();
                var lower = trimmed.ToLowerInvariant();

                if (lower.StartsWith("c: data") || lower == "data")
                {
                    sb.AppendLine(line);
                    inDataSection = true;
                    continue;
                }
                if (inDataSection)
                {
                    if (trimmed == "c: ." || trimmed == ".")
                    {
                        sb.AppendLine("[DATA content redacted]");
                        sb.AppendLine(line);
                        inDataSection = false;
                    }
                    continue;
                }

                if (lower.StartsWith("auth "))
                {
                    sb.AppendLine("[REDACTED AUTH]");
                    continue;
                }

                if (lower.Contains("xoauth2"))
                {
                    sb.AppendLine("[REDACTED XOAUTH2]");
                    continue;
                }

                if (lower.Contains("password") || lower.Contains("passwd") || lower.Contains("token"))
                {
                    sb.AppendLine("[REDACTED SENSITIVE]");
                    continue;
                }

                sb.AppendLine(Base64LikeRedaction.Replace(line, "[REDACTED]"));
            }

            var formatted = sb.ToString();
            if (formatted.Length <= MaxSmtpTranscriptCharsToLog)
                return formatted;

            return formatted.Substring(formatted.Length - MaxSmtpTranscriptCharsToLog);
        }
    }
}
