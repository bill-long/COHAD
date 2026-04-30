using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Extensions.Options;
using Web.Configuration;

namespace Web.Services
{
    /// <summary>
    /// Routes email recipients to the appropriate transport. Recipients listed in
    /// <see cref="PostmarkOptions.RoutedRecipients"/> are sent via Postmark (using the
    /// broadcast or transactional instance based on email job category); all others go
    /// through the default transport (SendGrid). When Postmark is not enabled, always
    /// returns the default transport.
    /// </summary>
    public class EmailTransportRouter : IDisposable
    {
        private readonly IEmailTransport _defaultTransport;
        private readonly IEmailTransport _postmarkBroadcast;
        private readonly IEmailTransport _postmarkTransactional;
        private readonly HashSet<string> _postmarkRecipients;
        private readonly HashSet<string> _transactionalCategories;
        private readonly bool _postmarkEnabled;

        public EmailTransportRouter(
            IEmailTransport defaultTransport,
            IEmailTransport postmarkBroadcast,
            IEmailTransport postmarkTransactional,
            IOptions<PostmarkOptions> postmarkOptions
        )
        {
            _defaultTransport = defaultTransport;
            _postmarkBroadcast = postmarkBroadcast;
            _postmarkTransactional = postmarkTransactional;
            var opts = postmarkOptions.Value;
            _postmarkEnabled = opts.Enabled;
            _postmarkRecipients = new HashSet<string>(
                (opts.RoutedRecipients ?? Enumerable.Empty<string>()).Select(r => r.Trim()).Where(r => r.Length > 0),
                StringComparer.OrdinalIgnoreCase
            );
            _transactionalCategories = new HashSet<string>(
                opts.TransactionalCategories ?? Enumerable.Empty<string>(),
                StringComparer.OrdinalIgnoreCase
            );
        }

        /// <summary>
        /// Returns the appropriate transport for a given recipient and email job category.
        /// </summary>
        public IEmailTransport GetTransportForRecipient(string recipientEmail, string category)
        {
            if (_postmarkEnabled && _postmarkRecipients.Contains(recipientEmail?.Trim() ?? ""))
                return _transactionalCategories.Contains(category ?? "") ? _postmarkTransactional : _postmarkBroadcast;

            return _defaultTransport;
        }

        /// <summary>
        /// Returns the default transport (SendGrid). Used for grouped sends where
        /// per-recipient routing is not applicable.
        /// </summary>
        public IEmailTransport DefaultTransport => _defaultTransport;

        public void Dispose()
        {
            if (_postmarkBroadcast is IDisposable broadcast && !ReferenceEquals(_postmarkBroadcast, _defaultTransport))
                broadcast.Dispose();
            if (_postmarkTransactional is IDisposable transactional && !ReferenceEquals(_postmarkTransactional, _defaultTransport))
                transactional.Dispose();
        }
    }
}
