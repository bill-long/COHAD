using System;
using System.IO;
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
    /// Sends emails through a Postmark SMTP relay endpoint.
    /// Each instance targets a single SMTP host (transactional or broadcast).
    /// Manages MailKit SmtpClient lifecycle and adds Postmark-specific metadata headers.
    /// </summary>
    public sealed class PostmarkEmailTransport : IEmailTransport, IDisposable
    {
        private readonly string _smtpHost;
        private readonly string _serverToken;
        private readonly string _streamId;
        private readonly int _timeoutSeconds;
        private readonly int _maxIdleSeconds;
        private readonly bool _logSmtpProtocolOnFailure;
        private readonly ILogger _logger;

        private SmtpClient _smtpClient;
        private MemoryStream _protocolLog;
        private DateTime _lastActivityUtc;

        private const int MaxProtocolLogBytes = 64 * 1024;
        private const int SmtpPort = 587;

        public string ProviderName => "Postmark";

        public PostmarkEmailTransport(
            string smtpHost,
            string serverToken,
            string streamId,
            int timeoutSeconds,
            int maxIdleSeconds,
            bool logSmtpProtocolOnFailure,
            ILogger logger
        )
        {
            _smtpHost = smtpHost;
            _serverToken = serverToken;
            _streamId = streamId;
            _timeoutSeconds = timeoutSeconds;
            _maxIdleSeconds = maxIdleSeconds;
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

                // Message stream is determined by which SMTP host this instance connects to,
                // but the header is still required.
                message.Headers.Add("X-PM-Message-Stream", _streamId);

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
                    var transcript = SmtpTranscriptHelper.FormatForLogs(_protocolLog);
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

        private async Task EnsureConnectedAsync(CancellationToken ct)
        {
            if (
                _smtpClient != null
                && _smtpClient.IsConnected
                && _maxIdleSeconds > 0
                && (DateTime.UtcNow - _lastActivityUtc).TotalSeconds > _maxIdleSeconds
            )
            {
                _logger.LogInformation(
                    "Postmark SMTP connection to {Host} idle for >{MaxIdle}s — reconnecting",
                    _smtpHost,
                    _maxIdleSeconds
                );
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

            if (_timeoutSeconds > 0)
                _smtpClient.Timeout = _timeoutSeconds * 1000;

            await _smtpClient.ConnectAsync(_smtpHost, SmtpPort, MailKit.Security.SecureSocketOptions.StartTls, ct);
            await _smtpClient.AuthenticateAsync(_serverToken, _serverToken, ct);
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
    }
}
