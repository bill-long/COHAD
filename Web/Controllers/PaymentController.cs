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
    [Authorize(Policy = "Resident")]
    public class PaymentController : Controller
    {
        private readonly CohadWebDbContext _dbContext;

        public PaymentController(CohadWebDbContext dbContext)
        {
            _dbContext = dbContext;
        }

        public async Task<IEnumerable<Payment>> Get()
        {
            var uniqueId = Models.User.GetUniqueIdFromClaims(User.Claims);
            var user = await _dbContext.Users.FindAsync(uniqueId);
            var payments = await _dbContext.Payments.Where(p => p.PayerUniqueId == uniqueId).ToListAsync();
            return payments;
        }

        [HttpPost]
        public async Task<Payment> Add([FromBody] Payment payment)
        {
            var uniqueId = Models.User.GetUniqueIdFromClaims(User.Claims);
            var user = await _dbContext.Users.FindAsync(uniqueId);
            payment.Id = Guid.NewGuid();
            payment.PayerUniqueId = user.UniqueId;
            _dbContext.Payments.Add(payment);
            await _dbContext.SaveChangesAsync();
            return payment;
        }
    }
}
