using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Extensions.Options;
using Web.Configuration;

namespace Web.Services
{
    /// <summary>
    /// Routes email recipients to the appropriate transport (default SMTP or Postmark).
    /// Recipients listed in <see cref="PostmarkOptions.RoutedRecipients"/> are sent via Postmark;
    /// all others go through the default transport (SendGrid). When Postmark is not enabled,
    /// always returns the default transport.
    /// </summary>
    public class EmailTransportRouter
    {
        private readonly IEmailTransport _defaultTransport;
        private readonly IEmailTransport _postmarkTransport;
        private readonly HashSet<string> _postmarkRecipients;
        private readonly bool _postmarkEnabled;

        public EmailTransportRouter(
            IEmailTransport defaultTransport,
            IEmailTransport postmarkTransport,
            IOptions<PostmarkOptions> postmarkOptions
        )
        {
            _defaultTransport = defaultTransport;
            _postmarkTransport = postmarkTransport;
            var opts = postmarkOptions.Value;
            _postmarkEnabled = opts.Enabled;
            _postmarkRecipients = new HashSet<string>(
                (opts.RoutedRecipients ?? Enumerable.Empty<string>()).Select(r => r.Trim()).Where(r => r.Length > 0),
                StringComparer.OrdinalIgnoreCase
            );
        }

        /// <summary>
        /// Returns the appropriate transport for a given recipient email address.
        /// </summary>
        public IEmailTransport GetTransportForRecipient(string recipientEmail)
        {
            if (_postmarkEnabled && _postmarkRecipients.Contains(recipientEmail?.Trim() ?? ""))
                return _postmarkTransport;

            return _defaultTransport;
        }

        /// <summary>
        /// Returns the default transport (SendGrid). Used for grouped sends where
        /// per-recipient routing is not applicable.
        /// </summary>
        public IEmailTransport DefaultTransport => _defaultTransport;
    }
}
