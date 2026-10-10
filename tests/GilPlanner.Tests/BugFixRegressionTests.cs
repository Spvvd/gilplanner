using System.Net;
using System.Text.Json;
using GilPlanner.Pricing;

namespace GilPlanner.Tests;

/// <summary>Regression tests for bugs found in a code review.</summary>
public class BugFixRegressionTests : IDisposable
{
    private readonly string dir = Path.Combine(Path.GetTempPath(), "gilplanner-regression-" + Guid.NewGuid());

    public void Dispose()
    {
        if (Directory.Exists(dir))
            Directory.Delete(dir, true);
    }

    private static string Read(string name) => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Data", name));

    private sealed class Network(Func<HttpRequestMessage, Task<HttpResponseMessage>> respond) : HttpMessageHandler
    {
        public int Requests;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref Requests);
            return respond(request);
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

    private static Task<HttpResponseMessage> Respond(HttpStatusCode code, string? body = null) =>
        Task.FromResult(new HttpResponseMessage(code) { Content = new StringContent(body ?? string.Empty) });

    private static string AggregatedWithPrice(long price) =>
        "{\"results\":[{\"itemId\":5333,\"nq\":{\"minListing\":{\"world\":{\"price\":" + price
        + "},\"dc\":{\"price\":" + price + ",\"worldId\":66}}},\"hq\":{},\"worldUploadTimes\":[]}],\"failedItems\":[]}";

    // ---- 1. One failing batch must not make the others retry on their own.

    [Theory]
    [InlineData(1)]
    [InlineData(3)]
    public async Task A_batch_that_fails_for_good_stops_the_remaining_batches(int concurrency)
    {
        var network = new Network(_ => Respond(HttpStatusCode.TooManyRequests));
        using var client = new UniversalisClient("test", network, TimeSpan.FromMilliseconds(1));
        var ids = Enumerable.Range(1, 1000).Select(i => (uint)i).ToList(); // 10 batches of 100

        var ex = await Assert.ThrowsAsync<UniversalisException>(() =>
            client.FetchAsync("Odin", 66, ids, CancellationToken.None, concurrency));

        Assert.Equal(FailureKind.RateLimited, ex.Kind); // the real reason, not a cancellation
        // One batch needs 4 requests (first try + 3 retries) to give up; at most `concurrency` batches are in flight.
        // Without the fix all 10 batches retried on their own: 40 requests.
        Assert.InRange(network.Requests, 4, 4 * concurrency);
    }

    [Fact]
    public async Task A_failing_listings_request_also_stops_the_remaining_batches()
    {
        var network = new Network(_ => Respond(HttpStatusCode.ServiceUnavailable));
        using var client = new UniversalisClient("test", network, TimeSpan.FromMilliseconds(1));
        var ids = Enumerable.Range(1, 500).Select(i => (uint)i).ToList();

        var ex = await Assert.ThrowsAsync<UniversalisException>(() =>
            client.FetchListingsAsync("Light", ids, 40, CancellationToken.None));

        Assert.Equal(FailureKind.ServerError, ex.Kind);
        Assert.Equal(4, network.Requests);
    }

    [Fact]
    public async Task Successful_batches_are_unaffected_by_the_fail_fast_logic()
    {
        var network = new Network(request =>
        {
            var ids = request.RequestUri!.AbsolutePath.Split('/')[^1].Split(',');
            var items = string.Join(',', ids.Select(id => "{\"itemId\":" + id + ",\"nq\":{},\"hq\":{},\"worldUploadTimes\":[]}"));
            return Respond(HttpStatusCode.OK, "{\"results\":[" + items + "]}");
        });
        using var client = new UniversalisClient("test", network, TimeSpan.FromMilliseconds(1));
        var ids = Enumerable.Range(1, 350).Select(i => (uint)i).ToList();
        var progress = new List<int>();

        var result = await client.FetchAsync("Odin", 66, ids, CancellationToken.None, 3, (done, _) => { lock (progress) progress.Add(done); });

        Assert.Equal(350, result.Count);
        Assert.Equal(4, network.Requests);
        Assert.Equal([1, 2, 3, 4], progress.Order());
    }

    // ---- 2. A refresh that was replaced must never overwrite the newer one's data.

    [Fact]
    public async Task A_replaced_refresh_does_not_overwrite_the_newer_data()
    {
        var release = new TaskCompletionSource();
        var calls = 0;
        var network = new Network(async _ =>
        {
            var call = Interlocked.Increment(ref calls);
            if (call == 1)
            {
                // The first refresh's answer arrives late, after it has been replaced, and holds an OLD price.
                await release.Task;
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(AggregatedWithPrice(111)) };
            }

            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(AggregatedWithPrice(999)) };
        });
        using var client = new UniversalisClient("test", network, TimeSpan.FromMilliseconds(1));
        using var service = new MarketService(client, new NoRecipes());

        service.Refresh("Odin", "Light", 66, [5333u], fetchWorldListings: false); // first: waits for the network
        await Task.Delay(50);
        service.Refresh("Odin", "Light", 66, [5333u], fetchWorldListings: false); // second: replaces it and finishes
        await WaitFor(() => !service.IsRefreshing);
        Assert.Equal(999, service.Data[5333].Nq.WorldMin);
        var versionBefore = service.Version;

        release.SetResult(); // now the old answer arrives
        await Task.Delay(200);

        Assert.Equal(999, service.Data[5333].Nq.WorldMin);
        Assert.Equal(versionBefore, service.Version); // nothing was published
        Assert.Null(service.Failure);
    }

    [Fact]
    public async Task A_replaced_refresh_that_fails_does_not_report_a_failure()
    {
        var release = new TaskCompletionSource();
        var calls = 0;
        var network = new Network(async _ =>
        {
            if (Interlocked.Increment(ref calls) == 1)
            {
                await release.Task;
                return new HttpResponseMessage(HttpStatusCode.NotFound); // a refusal that is not retried
            }

            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(AggregatedWithPrice(500)) };
        });
        using var client = new UniversalisClient("test", network, TimeSpan.FromMilliseconds(1));
        using var service = new MarketService(client, new NoRecipes());

        service.Refresh("Odin", "Light", 66, [5333u], false);
        await Task.Delay(50);
        service.Refresh("Odin", "Light", 66, [5333u], false);
        await WaitFor(() => !service.IsRefreshing);

        release.SetResult();
        await Task.Delay(200);

        Assert.Null(service.Failure);
        Assert.Equal(0, service.ConsecutiveFailures);
        Assert.Equal(500, service.Data[5333].Nq.WorldMin);
    }

    // ---- 4. A cache that cannot be saved must not turn a good scan into a failed one.

    [Fact]
    public async Task A_failing_cache_save_does_not_fail_the_scan()
    {
        var network = new Network(_ => Respond(HttpStatusCode.OK, Read("aggregated-sample.json")));
        using var client = new UniversalisClient("test", network, TimeSpan.FromMilliseconds(1));
        using var service = new PopularService(client, _ => throw new InvalidOperationException("disk exploded"));

        service.Scan("Odin", 66, [5333u, 36113u]);
        await WaitFor(() => !service.IsScanning);

        Assert.Null(service.Failure);
        Assert.NotEmpty(service.Results);
        Assert.Equal(66u, service.WorldId);
        Assert.NotNull(service.LastScanned);
    }

    [Fact]
    public async Task A_cache_callback_receives_the_finished_scan()
    {
        var network = new Network(_ => Respond(HttpStatusCode.OK, Read("aggregated-sample.json")));
        using var client = new UniversalisClient("test", network, TimeSpan.FromMilliseconds(1));
        PopularScan? saved = null;
        using var service = new PopularService(client, scan => saved = scan);

        service.Scan("Odin", 66, [5333u, 36113u]);
        await WaitFor(() => !service.IsScanning);

        Assert.NotNull(saved);
        Assert.Equal(66u, saved.WorldId);
    }

    // ---- 5. Damaged cache entries must be dropped, not shown.

    [Fact]
    public void Damaged_cache_entries_are_dropped_and_good_ones_kept()
    {
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "cache.json");
        File.WriteAllText(path, """
            [
              {"WorldId":1,"ScannedAt":"2026-10-10T00:00:00+00:00","Items":null},
              {"WorldId":2,"ScannedAt":"2026-10-10T00:00:00+00:00","Items":[null]},
              null,
              {"WorldId":3,"ScannedAt":"2026-10-10T00:00:00+00:00","Items":[{"ItemId":5,"Price":10,"IsHq":false,"Velocity":2.5,"GilPerDay":25}]}
            ]
            """);

        var cache = new PopularCache(path);

        Assert.Null(cache.Get(1));
        Assert.Null(cache.Get(2));
        Assert.Equal(5u, cache.Get(3)!.Items.Single().ItemId);
    }

    [Fact]
    public void Restoring_only_ever_sees_scans_with_a_list()
    {
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "cache.json");
        File.WriteAllText(path, """[{"WorldId":1,"ScannedAt":"2026-10-10T00:00:00+00:00","Items":null}]""");
        using var client = new UniversalisClient("test");
        using var service = new PopularService(client);

        if (new PopularCache(path).Get(1) is { } scan)
            service.Restore(scan);

        Assert.Empty(service.Results); // would have been null (and crashed the tab) before
    }

    private static async Task WaitFor(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!condition() && DateTime.UtcNow < deadline)
            await Task.Delay(10);
        Assert.True(condition(), "timed out waiting");
    }
}
