using System.Threading.Tasks;
using Moq;
using Web.Controllers;
using Web.Models;
using Web.Services.Repositories;

namespace Web.UnitTests;

public sealed class AuditLogControllerTests
{
    [Fact]
    public async Task Get_uses_default_limit_50_when_limit_null()
    {
        var mock = new Mock<IAuditLogRepository>(MockBehavior.Strict);
        mock.Setup(r => r.GetPageAsync(50, null, null))
            .ReturnsAsync(new AuditLogPage())
            .Verifiable();

        var c = new AuditLogController(mock.Object);
        await c.Get(null, null, null, null);

        mock.Verify();
    }

    [Fact]
    public async Task Get_clamps_limit_below_1_to_50()
    {
        var mock = new Mock<IAuditLogRepository>(MockBehavior.Strict);
        mock.Setup(r => r.GetPageAsync(50, null, null))
            .ReturnsAsync(new AuditLogPage());

        var c = new AuditLogController(mock.Object);
        await c.Get(0, null, null, null);

        mock.Verify(r => r.GetPageAsync(50, null, null), Times.Once);
    }

    [Fact]
    public async Task Get_clamps_limit_above_200_to_200()
    {
        var mock = new Mock<IAuditLogRepository>(MockBehavior.Strict);
        mock.Setup(r => r.GetPageAsync(200, null, null))
            .ReturnsAsync(new AuditLogPage());

        var c = new AuditLogController(mock.Object);
        await c.Get(500, null, null, null);

        mock.Verify(r => r.GetPageAsync(200, null, null), Times.Once);
    }

    [Fact]
    public async Task Get_prefers_header_cursor_over_query_cursor()
    {
        var mock = new Mock<IAuditLogRepository>(MockBehavior.Strict);
        mock.Setup(r => r.GetPageAsync(50, "from-header", null))
            .ReturnsAsync(new AuditLogPage());

        var c = new AuditLogController(mock.Object);
        await c.Get(null, "from-query", "from-header", null);

        mock.Verify(r => r.GetPageAsync(50, "from-header", null), Times.Once);
    }

    [Fact]
    public async Task Get_whitespace_only_query_cursor_passes_null_to_repository()
    {
        var mock = new Mock<IAuditLogRepository>(MockBehavior.Strict);
        mock.Setup(r => r.GetPageAsync(50, null, null))
            .ReturnsAsync(new AuditLogPage());

        var c = new AuditLogController(mock.Object);
        await c.Get(null, "   \t  ", null, null);

        mock.Verify(r => r.GetPageAsync(50, null, null), Times.Once);
    }

    [Fact]
    public async Task Get_whitespace_only_header_cursor_falls_back_to_query_when_query_has_value()
    {
        var mock = new Mock<IAuditLogRepository>(MockBehavior.Strict);
        mock.Setup(r => r.GetPageAsync(50, "from-query", null))
            .ReturnsAsync(new AuditLogPage());

        var c = new AuditLogController(mock.Object);
        await c.Get(null, "from-query", "   ", null);

        mock.Verify(r => r.GetPageAsync(50, "from-query", null), Times.Once);
    }

    [Fact]
    public async Task Get_passes_search_query_unchanged()
    {
        var mock = new Mock<IAuditLogRepository>(MockBehavior.Strict);
        mock.Setup(r => r.GetPageAsync(50, null, "  find me  "))
            .ReturnsAsync(new AuditLogPage());

        var c = new AuditLogController(mock.Object);
        await c.Get(null, null, null, "  find me  ");

        mock.Verify(r => r.GetPageAsync(50, null, "  find me  "), Times.Once);
    }
}
