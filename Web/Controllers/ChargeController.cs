using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Claims;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Newtonsoft.Json;
using Stripe;
using Web.Models;
using Web.Repository;

namespace Web.Controllers
{
    [Route("api/[controller]")]
    [ApiController]
    [Authorize]
    public class ChargeController : ControllerBase
    {
        private readonly AzureTableRepository<User> _userRepository;
        private readonly CustomerService _customerService;
        private readonly ChargeService _chargeService;
        private readonly SubscriptionService _subscriptionService;
        private readonly SourceService _sourceService;

        public ChargeController(AzureTableRepository<User> userRepository)
        {
            _userRepository = userRepository;
            StripeConfiguration.SetApiKey(Environment.GetEnvironmentVariable("StripeSecretKey"));
            _customerService = new CustomerService();
            _chargeService = new ChargeService();
            _subscriptionService = new SubscriptionService();
            _sourceService = new SourceService();
        }

        [HttpGet]
        public async Task<IActionResult> Get()
        {
            var nameId = User.Claims.First(c => c.Type == ClaimTypes.NameIdentifier).Value;
            var user = await _userRepository.FindByKey(nameId);
            if (user == null)
            {
                return NotFound("User not found.");
            }

            var charges = new List<ChargeViewModel>();
            if (user.ChargeIds != null)
            {
                foreach (var chargeId in user.ChargeIds)
                {
                    var charge = await _chargeService.GetAsync(chargeId);
                    charges.Add(ChargeViewModel.FromChargeAndSource(charge, charge.Source as Card));
                }
            }

            var subscriptions = new List<SubscriptionViewModel>();
            if (user.SubscriptionIds != null)
            {
                var customer = await GetCustomer(user);
                var source = await _sourceService.GetAsync(customer.DefaultSourceId);
                foreach (var subId in user.SubscriptionIds)
                {
                    var sub = await _subscriptionService.GetAsync(subId);
                    subscriptions.Add(SubscriptionViewModel.FromSubscription(sub, sub.Status == "active" ? source : null));
                }
            }

            return new JsonResult(new {charges, subscriptions});
        }

        [HttpPost("once")]
        public async Task<IActionResult> ChargeOnce([FromBody] Token token)
        {
            var nameId = User.Claims.First(c => c.Type == ClaimTypes.NameIdentifier).Value;
            var user = await _userRepository.FindByKey(nameId);
            if (user == null)
            {
                return NotFound("User not found.");
            }

            var customer = await GetCustomer(user);

            var options = new ChargeCreateOptions
            {
                Amount = 20000,
                Currency = "usd",
                Description = "Annual COHAD Dues - One-time Payment",
                SourceId = token.Id
            };

            var charge = await _chargeService.CreateAsync(options);

            if (user.ChargeIds == null)
            {
                user.ChargeIds = new List<string>();
            }

            user.ChargeIds.Add(charge.Id);
            await _userRepository.Replace(user);

            Console.WriteLine("Charge result:");
            Console.WriteLine(JsonConvert.SerializeObject(charge, Formatting.Indented));

            return Ok();
        }

        [HttpPost("recurring")]
        public async Task<IActionResult> ChargeRecurring([FromBody] Source source)
        {
            var nameId = User.Claims.First(c => c.Type == ClaimTypes.NameIdentifier).Value;
            var user = await _userRepository.FindByKey(nameId);
            if (user == null)
            {
                return NotFound("User not found.");
            }

            var customer = await GetCustomer(user);
            customer = await _customerService.UpdateAsync(customer.Id, new CustomerUpdateOptions
            {
                SourceToken = source.Id
            });

            var result = await _subscriptionService.CreateAsync(new SubscriptionCreateOptions
            {
                CustomerId = customer.Id,
                Items = new List<SubscriptionItemOption> { new SubscriptionItemOption { PlanId = "plan_EfuphNRUVArveO" } }
            });

            if (user.SubscriptionIds == null)
            {
                user.SubscriptionIds = new List<string>();
            }

            user.SubscriptionIds.Add(result.Id);
            await _userRepository.Replace(user);

            return Ok();
        }

        /// <summary>
        /// Get Customer from Stripe API
        /// </summary>
        /// <param name="user"></param>
        /// <returns></returns>
        private async Task<Customer> GetCustomer(User user)
        {
            Customer customer;
            if (string.IsNullOrEmpty(user.CustomerId))
            {
                customer = await _customerService.CreateAsync(new CustomerCreateOptions());
                user.CustomerId = customer.Id;
                await _userRepository.Replace(user);
            }
            else
            {
                customer = await _customerService.GetAsync(user.CustomerId);
            }

            return customer;
        }

        public class ChargeViewModel
        {
            public long Amount { get; set; }
            public DateTime Created { get; set; }
            public string SellerMessage { get; set; }
            public string Name { get; set; }
            public string Brand { get; set; }
            public string Last4 { get; set; }
            public long ExpMonth { get; set; }
            public long ExpYear { get; set; }

            public static ChargeViewModel FromChargeAndSource(Charge charge, Card source)
            {
                return new ChargeViewModel
                {
                    Amount = charge.Amount,
                    Created = charge.Created,
                    SellerMessage = charge.Outcome.SellerMessage,
                    Name = source.Name,
                    Brand = source.Brand,
                    Last4 = source.Last4,
                    ExpMonth = source.ExpMonth,
                    ExpYear = source.ExpYear
                };
            }
        }

        public class SubscriptionViewModel
        {
            public long? Amount { get; set; }
            public DateTime? Created { get; set; }
            public long IntervalCount { get; set; }
            public string Interval { get; set; }
            public string Status { get; set; }
            public string Name { get; set; }
            public string Brand { get; set; }
            public string Last4 { get; set; }
            public long? ExpMonth { get; set; }
            public long? ExpYear { get; set; }

            public static SubscriptionViewModel FromSubscription(Subscription sub, Source source)
            {
                return new SubscriptionViewModel
                {
                    Amount = sub.Plan.Amount,
                    Created = sub.Created,
                    Interval = sub.Plan.Interval,
                    IntervalCount = sub.Plan.IntervalCount,
                    Status = sub.Status,
                    Name = source?.Owner.Name,
                    Brand = source?.Card.Brand,
                    Last4 = source?.Card.Last4,
                    ExpMonth = source?.Card.ExpMonth,
                    ExpYear = source?.Card.ExpYear
                };
            }
        }
    }
}