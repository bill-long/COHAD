using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Web.Models;
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
        private readonly IResidentRepository _residentRepository;

        public DirectoryController(IHomeRepository homeRepository, IResidentRepository residentRepository)
        {
            _homeRepository = homeRepository;
            _residentRepository = residentRepository;
        }

        public async Task<IEnumerable<DirectoryHome>> GetDirectory()
        {
            var homes = await _homeRepository.GetAllAsync();
            var allResidents = await _residentRepository.GetAllAsync();
            var residentsByHome = allResidents.GroupBy(r => r.HomeId).ToDictionary(g => g.Key, g => g.ToList());
            return homes
                .Select(h =>
                    DirectoryHome.FromStorageModel(
                        h,
                        residentsByHome.TryGetValue(h.Id, out var r) ? r : new List<Resident>()
                    )
                )
                .ToList();
        }
    }
}
