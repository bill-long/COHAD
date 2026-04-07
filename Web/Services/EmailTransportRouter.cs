using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Extensions.Options;
using Web.Configuration;

namespace Web.Services
{
    /// <summary>
    /// Routes email recipients to the appropriate transport (SMTP or SES).
    /// Recipients listed in <see cref="SesOptions.RoutedRecipients"/> are sent via SES;
    /// all others go through SMTP. When SES is not enabled, always returns SMTP.
    /// </summary>
    public class EmailTransportRouter
    {
        private readonly IEmailTransport _smtpTransport;
        private readonly IEmailTransport _sesTransport;
        private readonly HashSet<string> _sesRecipients;
        private readonly bool _sesEnabled;

        public EmailTransportRouter(
            IEmailTransport smtpTransport,
            IEmailTransport sesTransport,
            IOptions<SesOptions> sesOptions
        )
        {
            _smtpTransport = smtpTransport;
            _sesTransport = sesTransport;
            var opts = sesOptions.Value;
            _sesEnabled = opts.Enabled;
            _sesRecipients = new HashSet<string>(
                (opts.RoutedRecipients ?? Enumerable.Empty<string>())
                    .Select(r => r.Trim())
                    .Where(r => r.Length > 0),
                StringComparer.OrdinalIgnoreCase
            );
        }

        public IEmailTransport GetTransportForRecipient(string recipientEmail)
        {
            if (_sesEnabled && _sesRecipients.Contains(recipientEmail?.Trim() ?? ""))
                return _sesTransport;

            return _smtpTransport;
        }
    }
}
