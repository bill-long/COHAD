using System.Collections.Generic;

namespace Web.UpdateModels
{
    public class EmailInfo
    {
        public string Subject { get; set; }

        public string HtmlBody { get; set; }

        public bool IsTestEmail { get; set; }

        /// <summary>
        /// When <see cref="IsTestEmail"/> is true, the specific email addresses to send
        /// the test to. Must be addresses associated with the sender's own home(s).
        /// </summary>
        public List<string> TestRecipientEmails { get; set; }
    }
}
