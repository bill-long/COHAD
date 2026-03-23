using System;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Web.Models;
using Web.Services.Repositories;

namespace Web.Controllers
{
    [Route("api/[controller]")]
    [ApiController]
    [Authorize(Policy = "Administrator")]
    public class AuditLogController : ControllerBase
    {
        private readonly IAuditLogRepository _auditLogRepository;

        public AuditLogController(IAuditLogRepository auditLogRepository)
        {
            _auditLogRepository = auditLogRepository;
        }

        /// <summary>
        /// Paged audit log. Continuation tokens can be long; prefer <c>X-Audit-Log-Cursor</c> header over query <c>cursor</c> to avoid URL length limits.
        /// </summary>
        [HttpGet]
        public async Task<AuditLogPage> Get(
            [FromQuery] int? limit = null,
            [FromQuery] string cursor = null,
            [FromHeader(Name = "X-Audit-Log-Cursor")] string cursorHeader = null,
            [FromQuery(Name = "q")] string query = null)
        {
            var pageSize = limit.GetValueOrDefault(50);
            if (pageSize < 1)
            {
                pageSize = 50;
            }

            if (pageSize > 200)
            {
                pageSize = 200;
            }

            string effectiveCursor = null;
            if (!string.IsNullOrWhiteSpace(cursorHeader))
            {
                effectiveCursor = cursorHeader.Trim();
            }
            else if (!string.IsNullOrWhiteSpace(cursor))
            {
                effectiveCursor = cursor.Trim();
            }

            return await _auditLogRepository.GetPageAsync(pageSize, effectiveCursor, query);
        }
    }
}
