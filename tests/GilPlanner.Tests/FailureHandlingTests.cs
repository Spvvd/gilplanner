using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using GilPlanner.Pricing;

namespace GilPlanner.Tests;

public class RetryPolicyTests
{
    [Theory]
    [InlineData(1, 1)]
    [InlineData(2, 2)]
    [InlineData(3, 4)]
    [InlineData(4, 8)]
    [InlineData(5, 15)]
    [InlineData(9, 15)]
    [InlineData(500, 15)]
    public void Waits_longer_after_each_failure_up_to_fifteen_minutes(int failures, double minutes) =>
        Assert.Equal(TimeSpan.FromMinutes(minutes), RetryPolicy.NextDelay(failures, null));

    [Fact]
    public void A_longer_retry_after_from_the_server_wins_but_is_capped()
    {
        Assert.Equal(TimeSpan.FromMinutes(20), RetryPolicy.NextDelay(1, TimeSpan.FromMinutes(20)));
        Assert.Equal(TimeSpan.FromMinutes(30), RetryPolicy.NextDelay(1, TimeSpan.FromHours(5)));
        Assert.Equal(TimeSpan.FromMinutes(4), RetryPolicy.NextDelay(3, TimeSpan.FromSeconds(10))); // shorter than our own backoff
    }

    [Fact]
    public void Automatic_refresh_stops_after_five_failures_in_a_row() =>
        Assert.Equal(5, RetryPolicy.MaxAutoFailures);
}

public class DataFreshnessTests
{
    private static readonly TimeSpan Interval = TimeSpan.FromMinutes(5);

    [Theory]
    [InlineData(0, Freshness.Fresh)]
    [InlineData(10, Freshness.Fresh)] // exactly two missed refreshes is still fine
    [InlineData(11, Freshness.Stale)]
    [InlineData(30, Freshness.Stale)]
    [InlineData(31, Freshness.VeryStale)]
    public void With_auto_refresh_data_is_stale_after_two_missed_intervals(double minutes, Freshness expected) =>
        Assert.Equal(expected, DataFreshness.Classify(TimeSpan.FromMinutes(minutes), true, Interval));

    [Theory]
    [InlineData(30, Freshness.Fresh)]
    [InlineData(31, Freshness.Stale)]
    [InlineData(90, Freshness.Stale)]
    [InlineData(91, Freshness.VeryStale)]
    public void Without_auto_refresh_data_ages_on_a_flat_schedule(double minutes, Freshness expected) =>
        Assert.Equal(expected, DataFreshness.Classify(TimeSpan.FromMinutes(minutes), false, Interval));

    [Fact]
    public void Long_refresh_intervals_stretch_the_limits()
    {
        var hour = TimeSpan.FromMinutes(60);

        Assert.Equal(Freshness.Fresh, DataFreshness.Classify(TimeSpan.FromMinutes(119), true, hour));
        Assert.Equal(Freshness.Stale, DataFreshness.Classify(TimeSpan.FromMinutes(121), true, hour));
        Assert.Equal(Freshness.VeryStale, DataFreshness.Classify(TimeSpan.FromHours(6.1), true, hour));
    }
}

public class FailureDescriptionTests
{
    [Fact]
    public void Describes_failures_in_plain_language_and_keeps_the_technical_detail()
    {
        var rate = MarketFailure.Describe(new UniversalisException(FailureKind.RateLimited, "HTTP 429", TimeSpan.FromMinutes(3)));
        Assert.Equal(FailureKind.RateLimited, rate.Kind);
        Assert.Contains("rate-limiting", rate.Message);
        Assert.Equal("HTTP 429", rate.Detail);
        Assert.Equal(TimeSpan.FromMinutes(3), rate.RetryAfter);

        Assert.Equal(FailureKind.Network, MarketFailure.Describe(new HttpRequestException("boom")).Kind);
        Assert.Equal(FailureKind.Timeout, MarketFailure.Describe(new TaskCanceledException()).Kind);
        Assert.Equal(FailureKind.Other, MarketFailure.Describe(new JsonException("bad json")).Kind);
        Assert.Equal(FailureKind.Other, MarketFailure.Describe(new InvalidOperationException("x")).Kind);
    }
}

/// <summary>Drives the client and the market service against a fake network.</summary>
public class MarketFailureFlowTests
{
    private static string Read(string name) => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Data", name));

    private sealed class FakeNetwork(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public int Requests;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref Requests);
            return Task.FromResult(respond(request));
        }
    }

    private sealed class ThrowingNetwork(Func<Exception> exception) : HttpMessageHandler
    {
        public int Requests;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref Requests);
            throw exception();
        }
    }

    private sealed class NoRecipes : IRecipeSource
    {
        public IReadOnlyList<Recipe> GetRecipes(uint itemId) => [];
        public bool IsGatherable(uint itemId) => false;
        public long? GetVendorPrice(uint itemId) => null;
        public bool CanBeHq(uint itemId) => false;
        public string GetItemName(uint itemId) => "x";
    }

    private static HttpResponseMessage Json(string body) =>
        new(HttpStatusCode.OK) { Content = new StringContent(body) };

    private static HttpResponseMessage Status(HttpStatusCode code) => new(code);

    private static UniversalisClient Client(HttpMessageHandler handler) =>
        new("test", handler, TimeSpan.FromMilliseconds(1));

    private static async Task WaitForRefresh(MarketService service)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (service.IsRefreshing && DateTime.UtcNow < deadline)
            await Task.Delay(10);
        Assert.False(service.IsRefreshing, "refresh did not finish");
    }

    [Fact]
    public async Task Rate_limiting_is_retried_then_reported_as_rate_limited()
    {
        var network = new FakeNetwork(_ => Status(HttpStatusCode.TooManyRequests));
        using var client = Client(network);

        var ex = await Assert.ThrowsAsync<UniversalisException>(() =>
            client.FetchAsync("Odin", 66, [1u], CancellationToken.None));

        Assert.Equal(FailureKind.RateLimited, ex.Kind);
        Assert.Equal(4, network.Requests); // first try + 3 retries
    }

    [Fact]
    public async Task Server_errors_network_errors_and_timeouts_are_each_classified()
    {
        using (var client = Client(new FakeNetwork(_ => Status(HttpStatusCode.BadGateway))))
            Assert.Equal(FailureKind.ServerError, (await Assert.ThrowsAsync<UniversalisException>(() => client.FetchAsync("Odin", 66, [1u], CancellationToken.None))).Kind);

        using (var client = Client(new ThrowingNetwork(() => new HttpRequestException("no route"))))
            Assert.Equal(FailureKind.Network, (await Assert.ThrowsAsync<UniversalisException>(() => client.FetchAsync("Odin", 66, [1u], CancellationToken.None))).Kind);

        // HttpClient reports its own timeout as a cancellation that we did not ask for. This used to be swallowed silently.
        using (var client = Client(new ThrowingNetwork(() => new TaskCanceledException("timeout"))))
            Assert.Equal(FailureKind.Timeout, (await Assert.ThrowsAsync<UniversalisException>(() => client.FetchAsync("Odin", 66, [1u], CancellationToken.None))).Kind);
    }

    [Fact]
    public async Task A_refusal_that_will_not_get_better_is_not_retried()
    {
        var network = new FakeNetwork(_ => Status(HttpStatusCode.NotFound));
        using var client = Client(network);

        var ex = await Assert.ThrowsAsync<UniversalisException>(() => client.FetchAsync("Nowhere", 1, [1u], CancellationToken.None));

        Assert.Equal(FailureKind.Other, ex.Kind);
        Assert.Equal(1, network.Requests);
    }

    [Fact]
    public async Task A_real_cancellation_is_not_reported_as_a_failure()
    {
        using var client = Client(new FakeNetwork(_ => Json("{}")));
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.FetchAsync("Odin", 66, [1u], cts.Token));
    }

    [Fact]
    public async Task A_failed_refresh_sets_a_visible_failure_and_a_backoff()
    {
        using var client = Client(new FakeNetwork(_ => Status(HttpStatusCode.TooManyRequests)));
        using var service = new MarketService(client, new NoRecipes());
        var before = DateTimeOffset.UtcNow;

        service.Refresh("Odin", "Light", 66, [5333u], fetchWorldListings: false);
        await WaitForRefresh(service);

        Assert.Equal(FailureKind.RateLimited, service.Failure!.Kind);
        Assert.Equal(1, service.ConsecutiveFailures);
        Assert.Null(service.LastUpdated);
        Assert.True(service.IsAutoRefreshBlocked(DateTimeOffset.UtcNow));
        Assert.InRange(service.NextRetryAt!.Value - before, TimeSpan.FromSeconds(55), TimeSpan.FromSeconds(65));
        Assert.False(service.IsAutoRefreshBlocked(DateTimeOffset.UtcNow.AddMinutes(2)));
    }

    [Fact]
    public async Task Automatic_refresh_pauses_after_five_failures_in_a_row()
    {
        using var client = Client(new FakeNetwork(_ => Status(HttpStatusCode.ServiceUnavailable)));
        using var service = new MarketService(client, new NoRecipes());

        for (var i = 0; i < RetryPolicy.MaxAutoFailures; i++)
        {
            service.Refresh("Odin", "Light", 66, [5333u], false);
            await WaitForRefresh(service);
        }

        Assert.True(service.AutoRefreshPaused);
        Assert.True(service.IsAutoRefreshBlocked(DateTimeOffset.UtcNow.AddDays(1)));
    }

    [Fact]
    public async Task A_successful_refresh_clears_the_failure_and_the_backoff()
    {
        var fail = true;
        using var client = Client(new FakeNetwork(request =>
            fail ? Status(HttpStatusCode.TooManyRequests)
            : request.RequestUri!.AbsolutePath.Contains("/aggregated/") ? Json(Read("aggregated-sample.json"))
            : Json(Read("listings-multi.json"))));
        using var service = new MarketService(client, new NoRecipes());

        service.Refresh("Odin", "Light", 66, [5333u, 36113u], false);
        await WaitForRefresh(service);
        Assert.NotNull(service.Failure);

        fail = false;
        service.Refresh("Odin", "Light", 66, [5333u, 36113u], false);
        await WaitForRefresh(service);

        Assert.Null(service.Failure);
        Assert.Equal(0, service.ConsecutiveFailures);
        Assert.Null(service.NextRetryAt);
        Assert.False(service.ListingsFailed);
        Assert.False(service.IsAutoRefreshBlocked(DateTimeOffset.UtcNow));
        Assert.NotNull(service.LastUpdated);
        Assert.True(service.Data.ContainsKey(36113));
    }

    [Fact]
    public async Task Failed_listings_keep_the_prices_drop_old_listings_and_still_count_as_a_failure()
    {
        var listingsWork = true;
        using var client = Client(new FakeNetwork(request =>
            request.RequestUri!.AbsolutePath.Contains("/aggregated/") ? Json(Read("aggregated-sample.json"))
            : listingsWork ? Json(Read("listings-multi.json"))
            : Status(HttpStatusCode.InternalServerError)));
        using var service = new MarketService(client, new NoRecipes());

        // First refresh works completely (materials = nothing, so only the world listings are fetched).
        service.Refresh("Odin", "Light", 66, [5333u, 36113u], fetchWorldListings: true);
        await WaitForRefresh(service);
        Assert.NotEmpty(service.WorldListings);

        listingsWork = false;
        service.Refresh("Odin", "Light", 66, [5333u, 36113u], fetchWorldListings: true);
        await WaitForRefresh(service);

        Assert.True(service.ListingsFailed);
        Assert.Equal(FailureKind.ServerError, service.Failure!.Kind);
        Assert.Equal(1, service.ConsecutiveFailures);
        Assert.Empty(service.WorldListings); // not mixed with the older ones
        Assert.Empty(service.DcListings);
        Assert.True(service.Data.ContainsKey(36113)); // the fresh prices are still there
    }
}
