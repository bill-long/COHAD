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

            string signingKey;
            try
            {
                signingKey = MockJwtSigningKey.ResolveValidated(config);
            }
            catch (InvalidOperationException ex)
            {
                return StatusCode(500, ex.Message);
            }

            var token = MockJwtIssuer.CreateAccessToken(signingKey, TimeSpan.FromHours(24));
            return Ok(new { accessToken = token, expiresIn = 86400 });
        }
    }
}
