using GilPlanner.Pricing;

namespace GilPlanner.Tests;

public class PricingWorldSelectorTests
{
    private const uint Odin = 66;
    private const uint Zodiark = 20;

    [Fact]
    public void Home_world_is_used_even_while_visiting_another_world() =>
        Assert.Equal(Odin, PricingWorldSelector.Select(useHomeWorld: true, homeWorldId: Odin, currentWorldId: Zodiark));

    [Fact]
    public void Current_world_is_used_when_that_is_chosen() =>
        Assert.Equal(Zodiark, PricingWorldSelector.Select(useHomeWorld: false, homeWorldId: Odin, currentWorldId: Zodiark));

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Both_choices_agree_when_you_are_on_your_home_world(bool useHomeWorld) =>
        Assert.Equal(Odin, PricingWorldSelector.Select(useHomeWorld, Odin, Odin));

    [Fact]
    public void An_unknown_preferred_world_falls_back_to_the_other_one()
    {
        Assert.Equal(Zodiark, PricingWorldSelector.Select(true, homeWorldId: 0, currentWorldId: Zodiark));
        Assert.Equal(Odin, PricingWorldSelector.Select(false, homeWorldId: Odin, currentWorldId: 0));
    }

    [Fact]
    public void Nothing_is_chosen_when_no_world_is_known() =>
        Assert.Null(PricingWorldSelector.Select(true, 0, 0));
}
