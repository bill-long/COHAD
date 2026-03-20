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
            _ = await _userRepository.GetByUniqueIdAsync(uniqueId);
            return await _paymentRepository.GetByPayerUniqueIdAsync(uniqueId);
        }

        [HttpPost]
        public async Task<Payment> Add([FromBody] Payment payment)
        {
            var uniqueId = Models.User.GetUniqueIdFromClaims(User.Claims);
            var user = await _userRepository.GetByUniqueIdAsync(uniqueId);
            payment.Id = Guid.NewGuid();
            payment.PayerUniqueId = user.UniqueId;
            return await _paymentRepository.AddAsync(payment);
        }
    }
}
