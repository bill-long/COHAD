using Web.Models;
using Web.PresentationModels;
using Xunit;

namespace Web.UnitTests;

public sealed class CommitteeMemberHelpersTests
{
    [Theory]
    [InlineData("John", "Doe", "John Doe")]
    [InlineData("John ", "Doe", "John Doe")] // trailing space on given name
    [InlineData("John", " Doe", "John Doe")] // leading space on surname
    [InlineData("  John  ", "  Doe  ", "John Doe")]
    [InlineData("John", "", "John")] // no surname (e.g. children)
    [InlineData("John", null, "John")]
    [InlineData("", "Doe", "Doe")]
    [InlineData(null, "Doe", "Doe")]
    [InlineData(null, null, "")]
    [InlineData("  ", "  ", "")] // whitespace-only parts
    public void FormatName_collapses_whitespace_and_skips_empty_parts(
        string? given,
        string? surname,
        string expected
    )
    {
        Assert.Equal(expected, CommitteeMemberHelpers.FormatName(given, surname));
    }

    [Fact]
    public void ResidentDisplayName_returns_unknown_for_null_resident()
    {
        Assert.Equal("Unknown", CommitteeMemberHelpers.ResidentDisplayName(null));
    }

    [Fact]
    public void ResidentDisplayName_collapses_internal_whitespace()
    {
        var resident = new Resident { GivenName = "John ", Surname = " Doe" };
        Assert.Equal("John Doe", CommitteeMemberHelpers.ResidentDisplayName(resident));
    }
}
