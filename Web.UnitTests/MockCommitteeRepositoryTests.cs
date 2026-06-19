using System.Linq;
using System.Threading.Tasks;
using Web.MockData;
using Web.Models;
using Xunit;

namespace Web.UnitTests;

public sealed class MockCommitteeRepositoryTests
{
    [Fact]
    public async Task Seed_includes_landscape_committee_with_its_management_role()
    {
        var repo = new MockCommitteeRepository();

        var landscape = (await repo.GetAllAsync()).SingleOrDefault(c => c.Id == "landscape");

        Assert.NotNull(landscape);
        Assert.Equal("landscape@cohad.org", landscape.CommitteeEmail);
        Assert.Equal("Landscape Committee", landscape.DisplayName);
        Assert.Equal(User.Role.LandscapeCommittee, landscape.ManagementRole);
    }

    [Fact]
    public async Task Seed_returns_committees_ordered_by_display_order()
    {
        var repo = new MockCommitteeRepository();

        var ordered = await repo.GetAllAsync();

        Assert.Equal(ordered.Select(c => c.DisplayOrder).OrderBy(o => o), ordered.Select(c => c.DisplayOrder));
    }
}
