using System;
using Microsoft.Extensions.Options;
using Web.Configuration;
using Web.Services;

namespace Web.UnitTests
{
    public class UnsubscribeTokenServiceTests
    {
        private const string TestKey = "this-is-a-test-signing-key-32-bytes!";

        private static UnsubscribeTokenService CreateService(string key = TestKey)
        {
            var options = Options.Create(new UnsubscribeTokenOptions { SigningKey = key });
            return new UnsubscribeTokenService(options);
        }

        [Fact]
        public void GenerateToken_ReturnsNonEmptyString()
        {
            var service = CreateService();
            var token = service.GenerateToken(Guid.NewGuid(), "test@example.com");
            Assert.False(string.IsNullOrWhiteSpace(token));
        }

        [Fact]
        public void GenerateToken_ContainsDotSeparator()
        {
            var service = CreateService();
            var token = service.GenerateToken(Guid.NewGuid(), "test@example.com");
            Assert.Contains('.', token);
        }

        [Fact]
        public void ValidateToken_RoundTrips()
        {
            var service = CreateService();
            var homeId = Guid.NewGuid();
            var email = "jane@example.com";

            var token = service.GenerateToken(homeId, email);
            var payload = service.ValidateToken(token);

            Assert.NotNull(payload);
            Assert.Equal(homeId, payload.HomeId);
            Assert.Equal(email, payload.Email);
            Assert.True(payload.Issued <= DateTimeOffset.UtcNow);
            Assert.True(payload.Issued > DateTimeOffset.UtcNow.AddMinutes(-1));
        }

        [Fact]
        public void ValidateToken_RejectsEmptyString()
        {
            var service = CreateService();
            Assert.Null(service.ValidateToken(""));
            Assert.Null(service.ValidateToken(null!));
            Assert.Null(service.ValidateToken("   "));
        }

        [Fact]
        public void ValidateToken_RejectsGarbage()
        {
            var service = CreateService();
            Assert.Null(service.ValidateToken("not-a-valid-token"));
            Assert.Null(service.ValidateToken("abc.def"));
            Assert.Null(service.ValidateToken("."));
        }

        [Fact]
        public void ValidateToken_RejectsTamperedPayload()
        {
            var service = CreateService();
            var token = service.GenerateToken(Guid.NewGuid(), "test@example.com");

            // Flip a character in the payload portion (before the dot)
            var dotIndex = token.IndexOf('.');
            var chars = token.ToCharArray();
            chars[0] = chars[0] == 'A' ? 'B' : 'A';
            var tampered = new string(chars);

            Assert.Null(service.ValidateToken(tampered));
        }

        [Fact]
        public void ValidateToken_RejectsTamperedSignature()
        {
            var service = CreateService();
            var token = service.GenerateToken(Guid.NewGuid(), "test@example.com");

            var dotIndex = token.IndexOf('.');
            var chars = token.ToCharArray();
            var sigIdx = dotIndex + 1;
            chars[sigIdx] = chars[sigIdx] == 'A' ? 'B' : 'A';
            var tampered = new string(chars);

            Assert.Null(service.ValidateToken(tampered));
        }

        [Fact]
        public void ValidateToken_RejectsTokenFromDifferentKey()
        {
            var service1 = CreateService("key-one-at-least-32-bytes-long!!");
            var service2 = CreateService("key-two-at-least-32-bytes-long!!");

            var token = service1.GenerateToken(Guid.NewGuid(), "test@example.com");
            Assert.Null(service2.ValidateToken(token));
        }

        [Fact]
        public void Constructor_ThrowsForShortKey()
        {
            Assert.Throws<InvalidOperationException>(() => CreateService("short"));
        }

        [Fact]
        public void Constructor_ThrowsForEmptyKey()
        {
            Assert.Throws<InvalidOperationException>(() => CreateService(""));
        }

        [Fact]
        public void GenerateToken_ThrowsForEmptyEmail()
        {
            var service = CreateService();
            Assert.Throws<ArgumentException>(() => service.GenerateToken(Guid.NewGuid(), ""));
            Assert.Throws<ArgumentException>(() => service.GenerateToken(Guid.NewGuid(), "  "));
        }

        [Fact]
        public void ValidateToken_HandlesSpecialCharactersInEmail()
        {
            var service = CreateService();
            var email = "user+tag@sub.domain.com";
            var homeId = Guid.NewGuid();

            var token = service.GenerateToken(homeId, email);
            var payload = service.ValidateToken(token);

            Assert.NotNull(payload);
            Assert.Equal(email, payload.Email);
            Assert.Equal(homeId, payload.HomeId);
        }

        [Fact]
        public void GenerateToken_ProducesDifferentTokensForDifferentInputs()
        {
            var service = CreateService();
            var token1 = service.GenerateToken(Guid.NewGuid(), "a@example.com");
            var token2 = service.GenerateToken(Guid.NewGuid(), "b@example.com");
            Assert.NotEqual(token1, token2);
        }

        [Fact]
        public void Token_IsUrlSafe()
        {
            var service = CreateService();
            var token = service.GenerateToken(Guid.NewGuid(), "test@example.com");

            Assert.DoesNotContain("+", token);
            Assert.DoesNotContain("/", token);
            Assert.DoesNotContain("=", token);
        }

        [Fact]
        public void ValidateToken_RejectsExpiredToken()
        {
            // Manually craft a token with a timestamp older than MaxTokenAge
            var service = CreateService();
            var homeId = Guid.NewGuid();
            var email = "test@example.com";
            var expiredUnixSeconds = DateTimeOffset.UtcNow
                .Subtract(UnsubscribeTokenService.MaxTokenAge)
                .AddDays(-1) // one day past expiry
                .ToUnixTimeSeconds();

            // Use reflection or craft the token manually
            var payload = $"{homeId:D}|{email}|{expiredUnixSeconds}";
            var payloadBytes = System.Text.Encoding.UTF8.GetBytes(payload);

            // Compute HMAC with the test key
            using var hmac = new System.Security.Cryptography.HMACSHA256(
                System.Text.Encoding.UTF8.GetBytes(TestKey));
            var signature = hmac.ComputeHash(payloadBytes);

            var token = Base64UrlEncode(payloadBytes) + "." + Base64UrlEncode(signature);

            Assert.Null(service.ValidateToken(token));
        }

        [Fact]
        public void ValidateToken_AcceptsRecentToken()
        {
            var service = CreateService();
            var token = service.GenerateToken(Guid.NewGuid(), "test@example.com");

            // A just-generated token should be valid
            Assert.NotNull(service.ValidateToken(token));
        }

        [Fact]
        public void ValidateToken_RejectsFutureTimestamp()
        {
            var service = CreateService();
            var homeId = Guid.NewGuid();
            var email = "test@example.com";
            // 10 minutes in the future (beyond the 5-minute clock-skew allowance)
            var futureUnixSeconds = DateTimeOffset.UtcNow.AddMinutes(10).ToUnixTimeSeconds();

            var payload = $"{homeId:D}|{email}|{futureUnixSeconds}";
            var payloadBytes = System.Text.Encoding.UTF8.GetBytes(payload);

            using var hmac = new System.Security.Cryptography.HMACSHA256(
                System.Text.Encoding.UTF8.GetBytes(TestKey));
            var signature = hmac.ComputeHash(payloadBytes);

            var token = Base64UrlEncode(payloadBytes) + "." + Base64UrlEncode(signature);

            Assert.Null(service.ValidateToken(token));
        }

        private static string Base64UrlEncode(byte[] data)
        {
            return Convert.ToBase64String(data)
                .TrimEnd('=')
                .Replace('+', '-')
                .Replace('/', '_');
        }
    }
}
