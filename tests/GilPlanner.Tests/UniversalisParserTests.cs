using GilPlanner.Pricing;

namespace GilPlanner.Tests;

public class UniversalisParserTests
{
    // Real response of /api/v2/aggregated/Odin/5333,36113 saved on 2026-10-09.
    private static string Sample => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Data", "aggregated-sample.json"));

    [Fact]
    public void Parses_both_items()
    {
        var result = UniversalisParser.Parse(Sample, 66);

        Assert.True(result.ContainsKey(5333));
        Assert.True(result.ContainsKey(36113));
    }

    [Fact]
    public void Reads_world_dc_prices_velocity_and_cheapest_world()
    {
        var item = UniversalisParser.Parse(Sample, 66)[36113];

        Assert.Equal(500, item.Hq.WorldMin);
        Assert.Equal(500, item.Hq.DcMin);
        Assert.Equal(66u, item.Hq.DcMinWorldId);
        Assert.Equal(600, item.Hq.WorldAvg);
        Assert.Equal(134.46, item.Hq.WorldVelocity, 2);
        Assert.Equal(495, item.Nq.WorldMin);
    }

    [Fact]
    public void Missing_blocks_become_null_or_zero()
    {
        var nq = UniversalisParser.Parse(Sample, 66)[36113].Nq;

        // The sample has no world-level average sale price or velocity for NQ.
        Assert.Null(nq.WorldAvg);
        Assert.Equal(0, nq.WorldVelocity);
        Assert.Equal(0.81, nq.DcVelocity, 2);
    }

    [Fact]
    public void Finds_upload_time_for_own_world_only()
    {
        var item = UniversalisParser.Parse(Sample, 66)[36113];
        Assert.Equal(DateTimeOffset.FromUnixTimeMilliseconds(1791489808413), item.WorldUploadTime);

        Assert.Null(UniversalisParser.Parse(Sample, 12345)[36113].WorldUploadTime);
    }

    [Fact]
    public void Empty_or_unexpected_json_yields_no_items()
    {
        Assert.Empty(UniversalisParser.Parse("{}", 1));
        Assert.Empty(UniversalisParser.Parse("{\"results\":[{\"nq\":{}}]}", 1));
    }
}
