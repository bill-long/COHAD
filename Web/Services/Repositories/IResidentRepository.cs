using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Web.Models;

namespace Web.Services.Repositories
{
    public interface IResidentRepository
    {
        Task<List<Resident>> GetAllAsync();

        Task<Resident> GetByIdAsync(Guid id);

        Task<List<Resident>> GetByIdsAsync(IEnumerable<Guid> ids);

        Task<List<Resident>> GetByHomeIdAsync(Guid homeId);

        Task<List<Resident>> GetByHomeIdsAsync(IEnumerable<Guid> homeIds);

        Task<Resident> UpsertAsync(Resident resident);

        Task DeleteAsync(Guid id);

        Task DeleteByHomeIdAsync(Guid homeId);
    }
}
