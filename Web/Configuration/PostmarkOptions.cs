using System;
using System.Collections.Generic;
using System.Linq;
using Web.Models;

namespace Web.Configuration
{
    public class PostmarkOptions
    {
        /// <summary>
        /// Master switch for Postmark integration. When false, all emails go through the
        /// fallback transport regardless of <see cref="UsePostmarkAsDefault"/>.
        /// </summary>
        public bool Enabled { get; set; }

        /// <summary>
        /// Postmark Server API Token. Used as the SMTP username when sending emails.
        /// Set via user secrets or environment variable (Postmark__ServerToken).
        /// </summary>
        public string ServerToken { get; set; } = "";

        /// <summary>
        /// Shared secret configured in the Postmark webhook settings.
        /// Compared against the <c>X-Postmark-Webhook-Token</c> header on incoming webhook requests.
        /// Set via user secrets or environment variable (Postmark__WebhookToken).
        /// </summary>
        public string WebhookToken { get; set; } = "";

        /// <summary>
        /// Postmark Message Stream ID for broadcast emails (committee blasts).
        /// </summary>
        public string BroadcastStream { get; set; } = "broadcast";

        /// <summary>
        /// Postmark Message Stream ID for transactional emails (notifications, forwards).
        /// </summary>
        public string TransactionalStream { get; set; } = "outbound";

        /// <summary>
        /// Email job categories that should be sent via the transactional stream.
        /// All other categories use the broadcast stream.
        /// </summary>
        public List<string> TransactionalCategories { get; set; } =
            new() { "registration", EmailJob.CommitteeForwardCategory, "notification-escalation" };

        /// <summary>
        /// When true (default) and Postmark is <see cref="Enabled"/>, all emails are sent
        /// through Postmark. Set to false to use the fallback transport for sending
        /// while keeping the Postmark webhook receiver active (requires <see cref="WebhookToken"/>
        /// but not <see cref="ServerToken"/>).
        /// Controlled via environment variable: Postmark__UsePostmarkAsDefault.
        /// </summary>
        public bool UsePostmarkAsDefault { get; set; } = true;

        /// <summary>
        /// Per-operation timeout in seconds for SMTP connect, authenticate, and send.
        /// </summary>
        public int TimeoutSeconds { get; set; } = 30;

        /// <summary>
        /// Maximum idle time in seconds before forcing an SMTP reconnect.
        /// </summary>
        public int MaxIdleSeconds { get; set; } = 60;

        /// <summary>
        /// SMTP host for transactional emails.
        /// </summary>
        public string TransactionalSmtpHost { get; set; } = "smtp.postmarkapp.com";

        /// <summary>
        /// SMTP host for broadcast emails.
        /// </summary>
        public string BroadcastSmtpHost { get; set; } = "smtp-broadcasts.postmarkapp.com";

        /// <summary>
        /// Whether the Postmark suppression API is callable and meaningful: the integration is
        /// on and a server token exists. Deliberately NOT conditioned on
        /// <see cref="UsePostmarkAsDefault"/>: in webhook-only mode Postmark still owns the
        /// stream suppression lists the reconciliation mirrors into COHAD, so the admin-clear
        /// reactivation must keep working there - gating it on the send path would let the
        /// sync re-suppress every clear, the exact fight issue #11 removes. The one definition
        /// of this predicate; the reactivation registration reads it, and any future consumer
        /// should too.
        /// </summary>
        public bool SuppressionApiConfigured =>
            Enabled && !string.IsNullOrWhiteSpace(ServerToken);

        /// <summary>
        /// The distinct configured message streams, blanks dropped - the one definition of
        /// "every stream" for the callers that act per stream (the suppression-dump
        /// reconciliation and the admin-clear reactivation). Deduped because a misconfiguration
        /// pointing both settings at the same stream must not act on it twice.
        /// </summary>
        public IReadOnlyList<string> GetConfiguredStreams()
        {
            return new[] { BroadcastStream, TransactionalStream }
                .Where(s => !string.IsNullOrWhiteSpace(s))
                .Distinct(StringComparer.Ordinal)
                .ToList();
        }
    }
}
