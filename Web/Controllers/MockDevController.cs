using System;
using System.Net;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Web.MockData;

namespace Web.Controllers
{
    [Route("api/dev")]
    [ApiController]
    public class MockDevController : ControllerBase
    {
        private static readonly TimeSpan MockTokenLifetime = TimeSpan.FromMinutes(15);

        [HttpGet("mock-auth")]
        [AllowAnonymous]
        public IActionResult GetMockToken([FromServices] IWebHostEnvironment env, [FromServices] IConfiguration config, [FromQuery] string userId = null)
        {
            if (!env.IsEnvironment("MockData"))
            {
                return NotFound();
            }

            if (!IsLoopbackRequest(HttpContext))
            {
                return NotFound();
            }

            string signingKey;
            try
            {
                signingKey = MockJwtSigningKey.ResolveValidated(config);
            }
            catch (InvalidOperationException ex)
            {
                return StatusCode(500, ex.Message);
            }

            var token = MockJwtIssuer.CreateAccessToken(signingKey, MockTokenLifetime, userId);
            return Ok(new { accessToken = token, expiresIn = (int)MockTokenLifetime.TotalSeconds });
        }

        private static bool IsLoopbackRequest(HttpContext context)
        {
            var remoteAddress = context?.Connection.RemoteIpAddress;
            if (remoteAddress == null)
            {
                return false;
            }

            return IsLoopback(remoteAddress);
        }

        private static bool IsLoopback(IPAddress address)
        {
            if (IPAddress.IsLoopback(address))
            {
                return true;
            }

            if (address.IsIPv4MappedToIPv6)
            {
                return IPAddress.IsLoopback(address.MapToIPv4());
            }

            return false;
        }
    }
}
