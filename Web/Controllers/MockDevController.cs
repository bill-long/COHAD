using System;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Hosting;
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
        [HttpGet("mock-auth")]
        [AllowAnonymous]
        public IActionResult GetMockToken([FromServices] IWebHostEnvironment env, [FromServices] IConfiguration config)
        {
            if (!env.IsEnvironment("MockData"))
            {
                return NotFound();
            }

            var signingKey = config["MockJwt:SigningKey"];
            if (string.IsNullOrEmpty(signingKey))
            {
                return StatusCode(500, "MockJwt:SigningKey is not configured.");
            }

            var token = MockJwtIssuer.CreateAccessToken(signingKey, TimeSpan.FromHours(24));
            return Ok(new { accessToken = token, expiresIn = 86400 });
        }
    }
}
