using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Web.Models;
using Web.Services.Repositories;

namespace Web.Controllers
{
    [Route("api/[controller]")]
    [ApiController]
    [Authorize(Policy = "Resident")]
    public class PaymentController : Controller
    {
        private readonly IUserRepository _userRepository;
        private readonly IPaymentRepository _paymentRepository;

        public PaymentController(IUserRepository userRepository, IPaymentRepository paymentRepository)
        {
            _userRepository = userRepository;
            _paymentRepository = paymentRepository;
        }

        public async Task<IEnumerable<Payment>> Get()
        {
            var uniqueId = Models.User.GetUniqueIdFromClaims(User.Claims);
            return await _paymentRepository.GetByPayerUniqueIdAsync(uniqueId);
        }

        [HttpPost]
        public async Task<IActionResult> Add([FromBody] Payment payment)
        {
            if (string.IsNullOrWhiteSpace(payment.Amount) ||
                !decimal.TryParse(payment.Amount, System.Globalization.NumberStyles.Any,
                    System.Globalization.CultureInfo.InvariantCulture, out var amount) ||
                amount <= 0)
            {
                return BadRequest("A positive numeric Amount is required.");
            }

            payment.Date ??= DateTime.UtcNow;

            var uniqueId = Models.User.GetUniqueIdFromClaims(User.Claims);
            var user = await _userRepository.GetByUniqueIdAsync(uniqueId);
            if (user == null)
            {
                return NotFound();
            }

            payment.Id = Guid.NewGuid();
            payment.PayerUniqueId = user.UniqueId;
            return Ok(await _paymentRepository.AddAsync(payment));
        }
    }
}
