using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace Web.Services
{
    /// <summary>
    /// Shared SMTP protocol transcript formatting and redaction logic.
    /// Used by both SmtpEmailTransport and PostmarkEmailTransport.
    /// </summary>
    internal static class SmtpTranscriptHelper
    {
        private const int MaxSmtpTranscriptCharsToLog = 32 * 1024;
        private static readonly Regex Base64LikeRedaction = new(@"[A-Za-z0-9+/=]{40,}", RegexOptions.Compiled);

        public static string FormatForLogs(MemoryStream protocolLog)
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
}
