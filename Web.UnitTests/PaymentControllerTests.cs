using System;
using System.Collections.Generic;
using System.Security.Claims;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Moq;
using Web.Controllers;
using Web.Models;
using Web.Services.Repositories;
using Xunit;

namespace Web.UnitTests;

public sealed class PaymentControllerTests
{
    private const string IdentityProviderClaim = "http://schemas.microsoft.com/identity/claims/identityprovider";

    private static PaymentController CreateController(
        IUserRepository users,
        IPaymentRepository payments,
        string nameId = "u1",
        string idp = "google.com")
    {
        var c = new PaymentController(users, payments)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext
                {
                    User = new ClaimsPrincipal(new ClaimsIdentity(new[]
                    {
                        new Claim(ClaimTypes.NameIdentifier, nameId),
                        new Claim(IdentityProviderClaim, idp)
                    }, "Test"))
                }
            }
        };
        return c;
    }

    private static string UniqueId(string nameId, string idp = "google.com") => $"{idp}{nameId}";

    // ── Get ──────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Get_returns_payments_for_authenticated_user()
    {
        var uniqueId = UniqueId("u1");
        var expected = new List<Payment>
        {
            new Payment { Id = Guid.NewGuid(), PayerUniqueId = uniqueId, Amount = "100.00" }
        };

        var mockPayments = new Mock<IPaymentRepository>();
        mockPayments.Setup(r => r.GetByPayerUniqueIdAsync(uniqueId)).ReturnsAsync(expected);

        var c = CreateController(Mock.Of<IUserRepository>(), mockPayments.Object);

        var result = await c.Get();

        Assert.Equal(expected, result);
    }

    // ── Add ──────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Add_returns_BadRequest_when_amount_is_missing()
    {
        var c = CreateController(Mock.Of<IUserRepository>(), Mock.Of<IPaymentRepository>());
        var payment = new Payment { Amount = null };

        var result = await c.Add(payment);

        Assert.IsType<BadRequestObjectResult>(result);
    }

    [Fact]
    public async Task Add_returns_BadRequest_when_amount_is_zero()
    {
        var c = CreateController(Mock.Of<IUserRepository>(), Mock.Of<IPaymentRepository>());
        var payment = new Payment { Amount = "0" };

        var result = await c.Add(payment);

        Assert.IsType<BadRequestObjectResult>(result);
    }

    [Fact]
    public async Task Add_returns_BadRequest_when_amount_is_negative()
    {
        var c = CreateController(Mock.Of<IUserRepository>(), Mock.Of<IPaymentRepository>());
        var payment = new Payment { Amount = "-5.00" };

        var result = await c.Add(payment);

        Assert.IsType<BadRequestObjectResult>(result);
    }

    [Fact]
    public async Task Add_returns_BadRequest_when_amount_is_not_numeric()
    {
        var c = CreateController(Mock.Of<IUserRepository>(), Mock.Of<IPaymentRepository>());
        var payment = new Payment { Amount = "not-a-number" };

        var result = await c.Add(payment);

        Assert.IsType<BadRequestObjectResult>(result);
    }

    [Fact]
    public async Task Add_returns_NotFound_when_user_not_in_database()
    {
        var uniqueId = UniqueId("u1");

        var mockUsers = new Mock<IUserRepository>();
        mockUsers.Setup(r => r.GetByUniqueIdAsync(uniqueId)).ReturnsAsync((User?)null);

        var c = CreateController(mockUsers.Object, Mock.Of<IPaymentRepository>());
        var payment = new Payment { Amount = "50.00" };

        var result = await c.Add(payment);

        Assert.IsType<NotFoundResult>(result);
    }

    [Fact]
    public async Task Add_sets_payer_from_authenticated_user_and_returns_Ok()
    {
        var uniqueId = UniqueId("u1");
        var user = new User { UniqueId = uniqueId };

        var mockUsers = new Mock<IUserRepository>();
        mockUsers.Setup(r => r.GetByUniqueIdAsync(uniqueId)).ReturnsAsync(user);

        Payment? saved = null;
        var mockPayments = new Mock<IPaymentRepository>();
        mockPayments
            .Setup(r => r.AddAsync(It.IsAny<Payment>()))
            .Callback<Payment>(p => saved = p)
            .ReturnsAsync((Payment p) => p);

        var c = CreateController(mockUsers.Object, mockPayments.Object);
        var payment = new Payment { Amount = "75.50" };

        var result = await c.Add(payment);

        Assert.IsType<OkObjectResult>(result);
        Assert.NotNull(saved);
        Assert.Equal(uniqueId, saved!.PayerUniqueId);
        Assert.NotEqual(Guid.Empty, saved.Id);
        Assert.NotNull(saved.Date);
    }

    [Fact]
    public async Task Add_defaults_date_to_UtcNow_when_not_provided()
    {
        var uniqueId = UniqueId("u1");
        var user = new User { UniqueId = uniqueId };

        var mockUsers = new Mock<IUserRepository>();
        mockUsers.Setup(r => r.GetByUniqueIdAsync(uniqueId)).ReturnsAsync(user);

        Payment? saved = null;
        var mockPayments = new Mock<IPaymentRepository>();
        mockPayments
            .Setup(r => r.AddAsync(It.IsAny<Payment>()))
            .Callback<Payment>(p => saved = p)
            .ReturnsAsync((Payment p) => p);

        var before = DateTime.UtcNow;
        var c = CreateController(mockUsers.Object, mockPayments.Object);
        await c.Add(new Payment { Amount = "10.00", Date = null });
        var after = DateTime.UtcNow;

        Assert.NotNull(saved?.Date);
        Assert.InRange(saved!.Date!.Value, before, after);
    }

    [Fact]
    public async Task Add_preserves_date_when_provided_by_caller()
    {
        var uniqueId = UniqueId("u1");
        var user = new User { UniqueId = uniqueId };

        var mockUsers = new Mock<IUserRepository>();
        mockUsers.Setup(r => r.GetByUniqueIdAsync(uniqueId)).ReturnsAsync(user);

        Payment? saved = null;
        var mockPayments = new Mock<IPaymentRepository>();
        mockPayments
            .Setup(r => r.AddAsync(It.IsAny<Payment>()))
            .Callback<Payment>(p => saved = p)
            .ReturnsAsync((Payment p) => p);

        var explicitDate = new DateTime(2025, 1, 15, 12, 0, 0, DateTimeKind.Utc);
        var c = CreateController(mockUsers.Object, mockPayments.Object);
        await c.Add(new Payment { Amount = "25.00", Date = explicitDate });

        Assert.Equal(explicitDate, saved?.Date);
    }
}
