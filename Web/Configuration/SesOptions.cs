using System;
using System.Collections.Generic;

namespace Web.Configuration
{
    public class SesOptions
    {
        public bool Enabled { get; set; }

        public string Region { get; set; } = "us-west-2";

        /// <summary>
        /// SES Configuration Set name for delivery event routing (SNS destination).
        /// </summary>
        public string ConfigurationSetName { get; set; } = string.Empty;

        /// <summary>
        /// Optional ARN for cross-account sending via SES.
        /// </summary>
        public string FromArn { get; set; } = string.Empty;

        /// <summary>
        /// Email addresses that should be routed through SES instead of SMTP.
        /// Matched case-insensitively.
        /// </summary>
        public List<string> RoutedRecipients { get; set; } = new();

        /// <summary>
        /// SNS Topic ARNs allowed to send webhook notifications. When non-empty,
        /// messages from any other TopicArn are rejected. Leave empty to skip the check
        /// (e.g. during initial setup before the ARN is known).
        /// </summary>
        public List<string> AllowedTopicArns { get; set; } = new();
    }
}
