using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Web.Models;
using Web.Services;
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

        [HttpGet]
        public async Task<IActionResult> Get()
        {
            var uniqueId = Models.User.GetUniqueIdFromClaims(User.Claims);
            var user = await _userRepository.GetByUniqueIdAsync(uniqueId);
            if (user == null)
            {
                return NotFound();
            }

            return Ok(await _paymentRepository.GetPaymentsForResidentAsync(user));
        }

        [HttpPost]
        public async Task<IActionResult> Add([FromBody] Payment payment)
        {
            if (string.IsNullOrWhiteSpace(payment.Amount) ||
                !decimal.TryParse(payment.Amount,
                    NumberStyles.AllowDecimalPoint | NumberStyles.AllowLeadingSign,
                    CultureInfo.InvariantCulture, out var amount) ||
                amount <= 0)
            {
                return BadRequest("A positive numeric Amount is required.");
            }

            if (payment.PaymentType != Payment.Type.OneTime &&
                payment.PaymentType != Payment.Type.SubscriptionCreated)
            {
                return BadRequest("PaymentType must be OneTime or SubscriptionCreated for client-recorded payments.");
            }

            payment.Date ??= DateTime.UtcNow;

            var uniqueId = Models.User.GetUniqueIdFromClaims(User.Claims);
            var user = await _userRepository.GetByUniqueIdAsync(uniqueId);
            if (user == null)
            {
                return NotFound();
            }

            if (!string.IsNullOrWhiteSpace(payment.PayPalTransactionId))
            {
                payment.PayPalTransactionId = payment.PayPalTransactionId.Trim();
                var existing = await _paymentRepository.GetByPayPalTransactionIdAsync(payment.PayPalTransactionId);
                if (existing != null)
                {
                    if (!string.IsNullOrEmpty(existing.PayerUniqueId))
                    {
                        if (!string.Equals(existing.PayerUniqueId, uniqueId, StringComparison.Ordinal))
                        {
                            return Conflict();
                        }

                        return Ok(existing);
                    }

                    if (UserEmailHelpers.EmailMatchesUser(existing.PayerEmail, user))
                    {
                        existing.PayerUniqueId = uniqueId;
                        return Ok(await _paymentRepository.ReplaceAsync(existing));
                    }

                    return Conflict();
                }
            }
            else
            {
                payment.PayPalTransactionId = null;
            }

            payment.PayerEmail = null;
            payment.PayerName = null;

            var ownedHomeIds = user.OwnedHomeIds ?? new List<Guid>();
            if (payment.HomeId.HasValue)
            {
                if (!ownedHomeIds.Contains(payment.HomeId.Value))
                {
                    return Forbid();
                }
            }
            else if (ownedHomeIds.Count > 0)
            {
                payment.HomeId = ownedHomeIds.OrderBy(g => g).First();
            }

            payment.Id = Guid.NewGuid();
            payment.PayerUniqueId = user.UniqueId;

            return Ok(await _paymentRepository.AddAsync(payment));
        }
    }
}
