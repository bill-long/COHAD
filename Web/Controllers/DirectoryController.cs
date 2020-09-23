using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Web.Data;
using Web.PresentationModels;
using Web.Repository;

namespace Web.Controllers
{
    [Route("api/[controller]")]
    [ApiController]
    [Authorize(Policy = "Resident")]
    public class DirectoryController : ControllerBase
    {
        private readonly CohadWebDbContext dbContext;

        public DirectoryController(CohadWebDbContext dbContext)
        {
            this.dbContext = dbContext;
        }

        public async Task<IEnumerable<DirectoryHome>> GetDirectory()
        {
            var homes = await dbContext.Homes.ToListAsync();

            var visibleHomeInfo = dbContext.Homes.Select(DirectoryHome.FromStorageModel).ToList();

            return visibleHomeInfo;
        }
    }
}
