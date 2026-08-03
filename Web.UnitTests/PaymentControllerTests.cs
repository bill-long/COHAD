using System;
using System.Collections.Generic;
using System.Security.Claims;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Moq;
using Web.Controllers;
using Web.Models;
using Web.Services;
using Web.PresentationModels;
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
        string idp = "google.com"
    )
    {
        var c = new PaymentController(new CurrentUserAccessor(users), payments)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext
                {
                    User = new ClaimsPrincipal(
                        new ClaimsIdentity(
                            new[]
                            {
                                new Claim(ClaimTypes.NameIdentifier, nameId),
                                new Claim(IdentityProviderClaim, idp),
                            },
                            "Test"
                        )
                    ),
                },
            },
        };
        return c;
    }

    private static string UniqueId(string nameId, string idp = "google.com") => $"{idp}{nameId}";

    // ── Get ──────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Get_returns_payments_for_authenticated_user()
    {
        var uniqueId = UniqueId("u1");
        var user = new User { UniqueId = uniqueId };
        var expected = new List<Payment>
        {
            new Payment
            {
                Id = Guid.NewGuid(),
                PayerUniqueId = uniqueId,
                Amount = "100.00",
            },
        };

        var mockUsers = new Mock<IUserRepository>();
        mockUsers.Setup(r => r.GetByUniqueIdAsync(uniqueId)).ReturnsAsync(user);

        var mockPayments = new Mock<IPaymentRepository>();
        mockPayments.Setup(r => r.GetPaymentsForResidentAsync(user)).ReturnsAsync(expected);

        var c = CreateController(mockUsers.Object, mockPayments.Object);

        var result = await c.Get();

        var ok = Assert.IsType<OkObjectResult>(result);
        var list = Assert.IsType<List<PaymentSummary>>(ok.Value);
        Assert.Single(list);
        Assert.Equal(expected[0].Id, list[0].Id);
        Assert.Equal(expected[0].Amount, list[0].Amount);
    }

    [Fact]
    public async Task Get_returns_NotFound_when_user_not_in_database()
    {
        var uniqueId = UniqueId("u1");

        var mockUsers = new Mock<IUserRepository>();
        mockUsers.Setup(r => r.GetByUniqueIdAsync(uniqueId)).ReturnsAsync((User?)null);

        var c = CreateController(mockUsers.Object, Mock.Of<IPaymentRepository>());

        var result = await c.Get();

        Assert.IsType<NotFoundResult>(result);
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
    public async Task Add_returns_BadRequest_when_payment_type_is_server_reserved()
    {
        var uniqueId = UniqueId("u1");
        var user = new User { UniqueId = uniqueId };

        var mockUsers = new Mock<IUserRepository>();
        mockUsers.Setup(r => r.GetByUniqueIdAsync(uniqueId)).ReturnsAsync(user);

        var c = CreateController(mockUsers.Object, Mock.Of<IPaymentRepository>());
        var payment = new Payment { Amount = "10.00", PaymentType = Payment.Type.PayPalSync };

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

    [Fact]
    public async Task Add_returns_existing_without_second_insert_when_PayPal_transaction_id_is_duplicate_for_same_user()
    {
        var uniqueId = UniqueId("u1");
        var user = new User { UniqueId = uniqueId };
        var txId = "03A84379GE3808324";
        var existing = new Payment
        {
            Id = Guid.NewGuid(),
            PayerUniqueId = uniqueId,
            PayPalTransactionId = txId,
            Amount = "75.50",
        };

        var mockUsers = new Mock<IUserRepository>();
        mockUsers.Setup(r => r.GetByUniqueIdAsync(uniqueId)).ReturnsAsync(user);

        var mockPayments = new Mock<IPaymentRepository>();
        mockPayments.Setup(r => r.GetByPayPalTransactionIdAsync(txId)).ReturnsAsync(existing);

        var c = CreateController(mockUsers.Object, mockPayments.Object);
        var result = await c.Add(new Payment { Amount = "75.50", PayPalTransactionId = txId });

        var ok = Assert.IsType<OkObjectResult>(result);
        var summary = Assert.IsType<PaymentSummary>(ok.Value);
        Assert.Equal(existing.Id, summary.Id);
        Assert.Equal(existing.Amount, summary.Amount);
        mockPayments.Verify(r => r.AddAsync(It.IsAny<Payment>()), Times.Never);
    }

    [Fact]
    public async Task Add_returns_Conflict_when_PayPal_transaction_id_exists_for_different_user()
    {
        var uniqueId = UniqueId("u1");
        var user = new User { UniqueId = uniqueId, Emails = "me@test.local" };
        var txId = "03A84379GE3808324";
        var existing = new Payment
        {
            Id = Guid.NewGuid(),
            PayerUniqueId = UniqueId("other"),
            PayPalTransactionId = txId,
            Amount = "75.50",
        };

        var mockUsers = new Mock<IUserRepository>();
        mockUsers.Setup(r => r.GetByUniqueIdAsync(uniqueId)).ReturnsAsync(user);

        var mockPayments = new Mock<IPaymentRepository>();
        mockPayments.Setup(r => r.GetByPayPalTransactionIdAsync(txId)).ReturnsAsync(existing);

        var c = CreateController(mockUsers.Object, mockPayments.Object);
        var result = await c.Add(new Payment { Amount = "75.50", PayPalTransactionId = txId });

        Assert.IsType<ConflictResult>(result);
        mockPayments.Verify(r => r.AddAsync(It.IsAny<Payment>()), Times.Never);
    }

    [Fact]
    public async Task Add_links_unlinked_payment_when_PayPal_transaction_id_matches_user_email()
    {
        var uniqueId = UniqueId("u1");
        var user = new User { UniqueId = uniqueId, Emails = "payer@test.local" };
        var txId = "03A84379GE3808324";
        var existing = new Payment
        {
            Id = Guid.NewGuid(),
            PayerUniqueId = null,
            PayerEmail = "payer@test.local",
            PayPalTransactionId = txId,
            Amount = "75.50",
        };

        var mockUsers = new Mock<IUserRepository>();
        mockUsers.Setup(r => r.GetByUniqueIdAsync(uniqueId)).ReturnsAsync(user);

        Payment? replaced = null;
        var mockPayments = new Mock<IPaymentRepository>();
        mockPayments.Setup(r => r.GetByPayPalTransactionIdAsync(txId)).ReturnsAsync(existing);
        mockPayments
            .Setup(r => r.ReplaceAsync(It.IsAny<Payment>()))
            .Callback<Payment>(p => replaced = p)
            .ReturnsAsync((Payment p) => p);

        var c = CreateController(mockUsers.Object, mockPayments.Object);
        var result = await c.Add(new Payment { Amount = "75.50", PayPalTransactionId = txId });

        var ok = Assert.IsType<OkObjectResult>(result);
        Assert.NotNull(replaced);
        Assert.Equal(uniqueId, replaced!.PayerUniqueId);
        mockPayments.Verify(r => r.AddAsync(It.IsAny<Payment>()), Times.Never);
    }

    [Fact]
    public async Task Add_returns_Conflict_when_unlinked_payment_email_does_not_match_user()
    {
        var uniqueId = UniqueId("u1");
        var user = new User { UniqueId = uniqueId, Emails = "me@test.local" };
        var txId = "03A84379GE3808324";
        var existing = new Payment
        {
            Id = Guid.NewGuid(),
            PayerUniqueId = null,
            PayerEmail = "someone.else@test.local",
            PayPalTransactionId = txId,
            Amount = "75.50",
        };

        var mockUsers = new Mock<IUserRepository>();
        mockUsers.Setup(r => r.GetByUniqueIdAsync(uniqueId)).ReturnsAsync(user);

        var mockPayments = new Mock<IPaymentRepository>();
        mockPayments.Setup(r => r.GetByPayPalTransactionIdAsync(txId)).ReturnsAsync(existing);

        var c = CreateController(mockUsers.Object, mockPayments.Object);
        var result = await c.Add(new Payment { Amount = "75.50", PayPalTransactionId = txId });

        Assert.IsType<ConflictResult>(result);
        mockPayments.Verify(r => r.ReplaceAsync(It.IsAny<Payment>()), Times.Never);
    }

    [Fact]
    public async Task Add_returns_Forbid_when_HomeId_is_not_an_owned_home()
    {
        var uniqueId = UniqueId("u1");
        var owned = Guid.Parse("11111111-1111-1111-1111-111111111111");
        var other = Guid.Parse("99999999-9999-9999-9999-999999999999");
        var user = new User
        {
            UniqueId = uniqueId,
            OwnedHomeIds = new List<Guid> { owned },
        };

        var mockUsers = new Mock<IUserRepository>();
        mockUsers.Setup(r => r.GetByUniqueIdAsync(uniqueId)).ReturnsAsync(user);

        var c = CreateController(mockUsers.Object, Mock.Of<IPaymentRepository>());
        var result = await c.Add(new Payment { Amount = "10.00", HomeId = other });

        Assert.IsType<ForbidResult>(result);
    }

    [Fact]
    public async Task Add_persists_PayPal_transaction_id_when_provided()
    {
        var uniqueId = UniqueId("u1");
        var user = new User { UniqueId = uniqueId };
        var txId = "  03A84379GE3808324  ";

        var mockUsers = new Mock<IUserRepository>();
        mockUsers.Setup(r => r.GetByUniqueIdAsync(uniqueId)).ReturnsAsync(user);

        Payment? saved = null;
        var mockPayments = new Mock<IPaymentRepository>();
        mockPayments.Setup(r => r.GetByPayPalTransactionIdAsync("03A84379GE3808324")).ReturnsAsync((Payment?)null);
        mockPayments
            .Setup(r => r.AddAsync(It.IsAny<Payment>()))
            .Callback<Payment>(p => saved = p)
            .ReturnsAsync((Payment p) => p);

        var c = CreateController(mockUsers.Object, mockPayments.Object);
        await c.Add(new Payment { Amount = "10.00", PayPalTransactionId = txId });

        Assert.Equal("03A84379GE3808324", saved?.PayPalTransactionId);
    }
}
