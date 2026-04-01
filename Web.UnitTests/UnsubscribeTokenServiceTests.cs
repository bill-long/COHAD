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
        public void GenerateToken_TokenIsOpaque()
        {
            var service = CreateService();
            var token = service.GenerateToken(Guid.NewGuid(), "test@example.com");
            // Token should not contain the email in plaintext (it's encrypted)
            Assert.DoesNotContain("test@example.com", token);
            // No dot separator (single base64url blob, not payload.signature)
            Assert.DoesNotContain(".", token);
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
        public void ValidateToken_RejectsTamperedToken()
        {
            var service = CreateService();
            var token = service.GenerateToken(Guid.NewGuid(), "test@example.com");

            // Flip a character — AES-GCM authentication will fail
            var chars = token.ToCharArray();
            chars[0] = chars[0] == 'A' ? 'B' : 'A';
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
        public void ValidateToken_HandlesEmailContainingPipeCharacter()
        {
            var service = CreateService();
            var email = "user|pipe@example.com";
            var homeId = Guid.NewGuid();

            var token = service.GenerateToken(homeId, email);
            var payload = service.ValidateToken(token);

            Assert.NotNull(payload);
            Assert.Equal(email, payload.Email);
            Assert.Equal(homeId, payload.HomeId);
        }

        [Fact]
        public void GenerateToken_ProducesDifferentTokensForSameInputs()
        {
            var service = CreateService();
            var homeId = Guid.NewGuid();
            // Same inputs produce different tokens due to random nonce
            var token1 = service.GenerateToken(homeId, "a@example.com");
            var token2 = service.GenerateToken(homeId, "a@example.com");
            Assert.NotEqual(token1, token2);
            // But both validate to the same data
            var p1 = service.ValidateToken(token1);
            var p2 = service.ValidateToken(token2);
            Assert.Equal(p1.HomeId, p2.HomeId);
            Assert.Equal(p1.Email, p2.Email);
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
            var service = CreateService();
            var expired = DateTimeOffset.UtcNow
                .Subtract(UnsubscribeTokenService.MaxTokenAge)
                .AddDays(-1);

            var token = service.GenerateToken(Guid.NewGuid(), "test@example.com", expired);
            Assert.Null(service.ValidateToken(token));
        }

        [Fact]
        public void ValidateToken_AcceptsRecentToken()
        {
            var service = CreateService();
            var token = service.GenerateToken(Guid.NewGuid(), "test@example.com");
            Assert.NotNull(service.ValidateToken(token));
        }

        [Fact]
        public void ValidateToken_RejectsFutureTimestamp()
        {
            var service = CreateService();
            var future = DateTimeOffset.UtcNow.AddMinutes(10);

            var token = service.GenerateToken(Guid.NewGuid(), "test@example.com", future);
            Assert.Null(service.ValidateToken(token));
        }
    }
}
