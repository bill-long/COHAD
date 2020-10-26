using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Web.Models;
using Web.Repository;

namespace Web.Controllers
{
    [Route("api/[controller]")]
    [ApiController]
    [Authorize(Policy = "Committee")]
    public class AuditLogController : ControllerBase
    {
        private readonly CohadWebDbContext _dbContext;

        public AuditLogController(CohadWebDbContext dbContext)
        {
            _dbContext = dbContext;
        }

        public async Task<IEnumerable<NewAuditLogEntry>> Get()
        {
            var audits = await _dbContext.AuditLog.ToListAsync();

            return audits.OrderByDescending(a => a.Time);
        }
    }
}
