using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Web.Data;
using Web.PresentationModels;
using Web.Services.Repositories;

namespace Web.Controllers
{
    [Route("api/[controller]")]
    [ApiController]
    [Authorize(Policy = "Resident")]
    public class DirectoryController : ControllerBase
    {
        private readonly IHomeRepository _homeRepository;

        public DirectoryController(IHomeRepository homeRepository)
        {
            _homeRepository = homeRepository;
        }

        public async Task<IEnumerable<DirectoryHome>> GetDirectory()
        {
            var homes = await _homeRepository.GetAllAsync();
            return homes.Select(DirectoryHome.FromStorageModel).ToList();
        }
    }
}
