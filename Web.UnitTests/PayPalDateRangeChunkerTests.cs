using Web.Services;
using Xunit;

namespace Web.UnitTests;

public sealed class PayPalDateRangeChunkerTests
{
    [Fact]
    public void EnumerateWindowsUtc_splits_into_30_day_segments()
    {
        var start = new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var end = new DateTime(2025, 4, 1, 0, 0, 0, DateTimeKind.Utc);

        var windows = PayPalDateRangeChunker.EnumerateWindowsUtc(start, end, 30).ToList();

        Assert.Equal(3, windows.Count);
        Assert.Equal(start, windows[0].StartUtc);
        Assert.True(windows[0].EndUtc > windows[0].StartUtc);
        Assert.Equal(end, windows[^1].EndUtc);
    }

    [Fact]
    public void EnumerateWindowsUtc_empty_when_end_not_after_start()
    {
        var t = new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        Assert.Empty(PayPalDateRangeChunker.EnumerateWindowsUtc(t, t).ToList());
    }

    [Fact]
    public void EnumerateWindowsUtc_throws_when_window_size_not_positive()
    {
        var start = new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var end = new DateTime(2025, 2, 1, 0, 0, 0, DateTimeKind.Utc);
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            PayPalDateRangeChunker.EnumerateWindowsUtc(start, end, 0).ToList());
    }
}
