using System;
using System.IO;
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
    /// Sends emails through SMTP (currently SendGrid relay).
    /// Manages MailKit SmtpClient lifecycle and adds SendGrid-specific correlation headers.
    /// </summary>
    public sealed class SmtpEmailTransport : IEmailTransport, IDisposable
    {
        private readonly SmtpOptions _smtpOptions;
        private readonly bool _logSmtpProtocolOnFailure;
        private readonly ILogger<SmtpEmailTransport> _logger;

        private SmtpClient _smtpClient;
        private MemoryStream _protocolLog;
        private DateTime _lastActivityUtc;

        /// <summary>Maximum bytes the protocol log stream will buffer before silently discarding writes.</summary>
        private const int MaxProtocolLogBytes = 64 * 1024;
        private const int MaxSmtpTranscriptCharsToLog = 32 * 1024;
        private static readonly Regex Base64LikeRedaction = new(@"[A-Za-z0-9+/=]{40,}", RegexOptions.Compiled);

        public string ProviderName => "SendGrid";

        public SmtpEmailTransport(
            SmtpOptions smtpOptions,
            bool logSmtpProtocolOnFailure,
            ILogger<SmtpEmailTransport> logger
        )
        {
            _smtpOptions = smtpOptions;
            _logSmtpProtocolOnFailure = logSmtpProtocolOnFailure;
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
                await EnsureConnectedAsync(ct);

                // Reset protocol log between messages to bound memory
                _protocolLog?.SetLength(0);

                // SendGrid custom args for webhook event correlation
                message.Headers.Add(
                    "X-SMTPAPI",
                    System.Text.Json.JsonSerializer.Serialize(
                        new { unique_args = new { cohad_job_id = jobId, cohad_email = recipientEmail } }
                    )
                );

                await _smtpClient.SendAsync(message, ct);
                _lastActivityUtc = DateTime.UtcNow;

                return new EmailSendResult { Success = true, ProviderName = ProviderName };
            }
            catch (OperationCanceledException)
            {
                throw; // Let cancellation propagate
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "SMTP send failed for {Email} in job {JobId}", recipientEmail, jobId);

                if (_logSmtpProtocolOnFailure && _protocolLog != null)
                {
                    var transcript = FormatSmtpTranscriptForLogs(_protocolLog);
                    _logger.LogWarning(
                        "SMTP transcript (redacted) for {Email} in job {JobId}: {SmtpTranscript}",
                        recipientEmail,
                        jobId,
                        transcript
                    );
                }

                // Dispose the client so the next send forces a fresh connection
                DisposeSmtpClient();

                return new EmailSendResult
                {
                    Success = false,
                    ProviderName = ProviderName,
                    Error = ex.Message,
                };
            }
        }

        private async Task EnsureConnectedAsync(CancellationToken ct)
        {
            // Force reconnect if the connection has been idle too long.
            // SendGrid load balancers can silently drop idle TCP connections,
            // and MailKit's IsConnected only checks local state — it won't
            // detect a half-open socket until the next read/write times out.
            if (
                _smtpClient != null
                && _smtpClient.IsConnected
                && _smtpOptions.MaxIdleSeconds > 0
                && (DateTime.UtcNow - _lastActivityUtc).TotalSeconds > _smtpOptions.MaxIdleSeconds
            )
            {
                _logger.LogInformation(
                    "SMTP connection idle for >{MaxIdle}s — reconnecting",
                    _smtpOptions.MaxIdleSeconds
                );
                DisposeSmtpClient();
            }

            if (_smtpClient != null && _smtpClient.IsConnected)
                return;

            // Dispose previous client if it exists but is disconnected
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

            if (_smtpOptions.TimeoutSeconds > 0)
                _smtpClient.Timeout = _smtpOptions.TimeoutSeconds * 1000;

            await _smtpClient.ConnectAsync(
                _smtpOptions.SmtpHost,
                587,
                MailKit.Security.SecureSocketOptions.StartTls,
                ct
            );
            await _smtpClient.AuthenticateAsync(_smtpOptions.SmtpUser, _smtpOptions.SmtpPassword, ct);
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

                // Skip email body content to avoid logging PII/message content
                if (lower.StartsWith("c: data") || lower == "data")
                {
                    sb.AppendLine(line);
                    inDataSection = true;
                    continue;
                }
                if (inDataSection)
                {
                    // DATA section ends with a line that is just "."
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

    /// <summary>
    /// A MemoryStream wrapper that silently discards writes once a byte cap is reached,
    /// preventing unbounded memory growth when logging SMTP protocol transcripts.
    /// </summary>
    internal sealed class BoundedMemoryStream : MemoryStream
    {
        private readonly int _maxBytes;

        public BoundedMemoryStream(int maxBytes) => _maxBytes = maxBytes;

        public override void Write(byte[] buffer, int offset, int count)
        {
            var remaining = _maxBytes - (int)Length;
            if (remaining <= 0)
                return;
            base.Write(buffer, offset, Math.Min(count, remaining));
        }

        public override void Write(ReadOnlySpan<byte> buffer)
        {
            var remaining = _maxBytes - (int)Length;
            if (remaining <= 0)
                return;
            base.Write(buffer.Length <= remaining ? buffer : buffer.Slice(0, remaining));
        }

        public override void WriteByte(byte value)
        {
            if (Length < _maxBytes)
                base.WriteByte(value);
        }

        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken ct)
        {
            var remaining = _maxBytes - (int)Length;
            if (remaining <= 0)
                return Task.CompletedTask;
            return base.WriteAsync(buffer, offset, Math.Min(count, remaining), ct);
        }

        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken ct = default)
        {
            var remaining = _maxBytes - (int)Length;
            if (remaining <= 0)
                return ValueTask.CompletedTask;
            return base.WriteAsync(buffer.Length <= remaining ? buffer : buffer.Slice(0, remaining), ct);
        }
    }
}
