using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Web.Models;
using Web.Repository;
using Web.UpdateModels;

namespace Web.Controllers
{
    [Route("api/[controller]")]
    [ApiController]
    [Authorize(Policy = "Administrator")]
    public class PrintableDirectoryController : Controller
    {
        private readonly CohadWebDbContext _dbContext;

        public PrintableDirectoryController(CohadWebDbContext dbContext)
        {
            _dbContext = dbContext;
        }

        public async Task<IEnumerable<PrintableDirectory>> Get()
        {
            return await _dbContext.PrintableDirectories.OrderByDescending(pd => pd.Created).ToListAsync();
        }

        [HttpPost]
        public async Task<IActionResult> Add([FromBody] UpdatedPrintableDirectory updatedPrintableDirectory)
        {
            var apiUser =
                await _dbContext.Users.FindAsync(Models.User.GetUniqueIdFromClaims(User.Claims));

            var newPD = new PrintableDirectory
            {
                Created = DateTime.UtcNow,
                CreatedBy = $"{apiUser.GivenName ?? ""} {apiUser.Surname ?? ""}",
                LastUpdated = DateTime.UtcNow,
                LastUpdatedBy = $"{apiUser.GivenName ?? ""} {apiUser.Surname ?? ""}",
                FrontCoverDataUrl = updatedPrintableDirectory.FrontCoverDataUrl,
                BackCoverDataUrl = updatedPrintableDirectory.BackCoverDataUrl
            };

            await _dbContext.PrintableDirectories.AddAsync(newPD);

            await _dbContext.AuditLog.AddAsync(new NewAuditLogEntry
            {
                Id = Guid.NewGuid(),
                SubjectId = newPD.Id.ToString(),
                SubjectName = $"{newPD.Created} {newPD.CreatedBy}",
                Action = "Added printable directory.",
                Time = DateTime.UtcNow,
                UserDisplayName = $"{apiUser.GivenName ?? ""} {apiUser.Surname ?? ""}",
                UserId = apiUser.UniqueId
            });

            await _dbContext.SaveChangesAsync();

            return Ok(newPD);
        }

        [HttpPut]
        public async Task<IActionResult> Update([FromBody] UpdatedPrintableDirectory updatedPrintableDirectory)
        {
            var apiUser =
                await _dbContext.Users.FindAsync(Models.User.GetUniqueIdFromClaims(User.Claims));

            var pdToUpdate = await _dbContext.PrintableDirectories.FirstOrDefaultAsync(p => p.Id == updatedPrintableDirectory.Id);

            if (pdToUpdate == null)
            {
                return NotFound();
            }

            pdToUpdate.LastUpdated = DateTime.UtcNow;
            pdToUpdate.LastUpdatedBy = $"{apiUser.GivenName ?? ""} {apiUser.Surname ?? ""}";
            pdToUpdate.FrontCoverDataUrl = updatedPrintableDirectory.FrontCoverDataUrl;
            pdToUpdate.BackCoverDataUrl = updatedPrintableDirectory.BackCoverDataUrl;

            await _dbContext.AuditLog.AddAsync(new NewAuditLogEntry
            {
                Id = Guid.NewGuid(),
                SubjectId = pdToUpdate.Id.ToString(),
                SubjectName = $"{pdToUpdate.Created} {pdToUpdate.CreatedBy}",
                Action = "Updated printable directory.",
                Time = DateTime.UtcNow,
                UserDisplayName = $"{apiUser.GivenName ?? ""} {apiUser.Surname ?? ""}",
                UserId = apiUser.UniqueId
            });

            await _dbContext.SaveChangesAsync();

            return Ok();
        }
    }
}
