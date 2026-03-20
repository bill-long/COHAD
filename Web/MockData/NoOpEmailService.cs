using System;
using System.Collections.Generic;
using System.Security.Claims;
using System.Threading.Tasks;
using Web.Models;
using Web.Services;
using Web.UpdateModels;

namespace Web.MockData
{
    public sealed class NoOpEmailService : IEmailService
    {
        public Task SendEmail(string fromEmail, string fromDisplay, EmailInfo emailInfo, Func<EmailAddress, bool> recipientFilter, string bccDisplayName, ClaimsPrincipal user)
        {
            return Task.CompletedTask;
        }

        public Task SendEmail(string fromEmail, string fromDisplay, EmailInfo emailInfo, List<string> toList, ClaimsPrincipal user)
        {
            return Task.CompletedTask;
        }
    }
}
