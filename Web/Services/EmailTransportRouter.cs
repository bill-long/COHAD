using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Extensions.Options;
using Web.Configuration;

namespace Web.Services
{
    /// <summary>
    /// Routes emails to the appropriate transport. When Postmark is enabled and
    /// <see cref="PostmarkOptions.UsePostmarkAsDefault"/> is true, all emails go through
    /// Postmark (broadcast or transactional based on category). Otherwise, emails go
    /// through the fallback transport (SendGrid).
    /// </summary>
    public class EmailTransportRouter : IDisposable
    {
        private readonly IEmailTransport _sendGridTransport;
        private readonly IEmailTransport _postmarkBroadcast;
        private readonly IEmailTransport _postmarkTransactional;
        private readonly HashSet<string> _transactionalCategories;
        private readonly bool _usePostmark;

        public EmailTransportRouter(
            IEmailTransport sendGridTransport,
            IEmailTransport postmarkBroadcast,
            IEmailTransport postmarkTransactional,
            IOptions<PostmarkOptions> postmarkOptions
        )
        {
            _sendGridTransport = sendGridTransport;
            _postmarkBroadcast = postmarkBroadcast;
            _postmarkTransactional = postmarkTransactional;
            var opts = postmarkOptions.Value;
            _usePostmark = opts.Enabled && opts.UsePostmarkAsDefault;
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
            if (_usePostmark)
                return _transactionalCategories.Contains(category ?? "") ? _postmarkTransactional : _postmarkBroadcast;

            return _sendGridTransport;
        }

        /// <summary>
        /// Returns the transport for grouped sends based on category.
        /// Grouped sends can't do per-recipient routing but still need
        /// the correct stream (broadcast vs transactional).
        /// </summary>
        public IEmailTransport GetDefaultTransport(string category)
        {
            if (_usePostmark)
                return _transactionalCategories.Contains(category ?? "") ? _postmarkTransactional : _postmarkBroadcast;

            return _sendGridTransport;
        }

        public void Dispose()
        {
            if (_postmarkBroadcast is IDisposable broadcast && !ReferenceEquals(_postmarkBroadcast, _sendGridTransport))
                broadcast.Dispose();
            if (_postmarkTransactional is IDisposable transactional && !ReferenceEquals(_postmarkTransactional, _sendGridTransport))
                transactional.Dispose();
        }
    }
}
