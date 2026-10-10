using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace GilPlanner.Pricing;

/// <summary>Thin client for the Universalis aggregated-prices endpoint. Not used from the UI thread.</summary>
public sealed class UniversalisClient : IDisposable
{
    private const string AggregatedUrl = "https://universalis.app/api/v2/aggregated/";
    private const string CurrentUrl = "https://universalis.app/api/v2/";
    private const int MaxItemsPerRequest = 100;
    private const int MaxRetries = 3;

    private static readonly TimeSpan DelayBetweenRequests = TimeSpan.FromMilliseconds(150);

    /// <summary>A server asking for a long pause is honoured by the retry policy, not by blocking this request.</summary>
    private static readonly TimeSpan MaxInlineWait = TimeSpan.FromSeconds(10);

    private readonly HttpClient http;
    private readonly TimeSpan retryBaseDelay;

    /// <param name="handler">Replaces the real network, for tests.</param>
    /// <param name="retryBaseDelay">Wait before the first retry, growing by that much each retry; 2 seconds by default.</param>
    public UniversalisClient(string userAgent, HttpMessageHandler? handler = null, TimeSpan? retryBaseDelay = null)
    {
        http = new HttpClient(handler ?? new HttpClientHandler { AutomaticDecompression = DecompressionMethods.All })
        {
            Timeout = TimeSpan.FromSeconds(30),
        };
        http.DefaultRequestHeaders.UserAgent.ParseAdd(userAgent);
        this.retryBaseDelay = retryBaseDelay ?? TimeSpan.FromSeconds(2);
    }

    /// <summary>
    /// Fetches aggregated market data for all <paramref name="itemIds"/>, 100 per request. <paramref name="onProgress"/>
    /// is called with (requests done, requests total) from background threads.
    /// </summary>
    public Task<Dictionary<uint, ItemMarket>> FetchAsync(
        string worldName,
        uint worldId,
        IEnumerable<uint> itemIds,
        CancellationToken token,
        int maxConcurrency = 1,
        Action<int, int>? onProgress = null) =>
        FetchBatchesAsync(
            itemIds,
            batch => $"{AggregatedUrl}{Uri.EscapeDataString(worldName)}/{string.Join(',', batch)}",
            json => UniversalisParser.Parse(json, worldId),
            token,
            maxConcurrency,
            onProgress);

    /// <summary>
    /// Fetches the cheapest current listings of each item on a world or a whole data center
    /// (<paramref name="scope"/> is the world or data center name).
    /// </summary>
    public Task<Dictionary<uint, ItemListings>> FetchListingsAsync(
        string scope,
        IEnumerable<uint> itemIds,
        int listingsPerItem,
        CancellationToken token,
        int maxConcurrency = 1) =>
        FetchBatchesAsync(
            itemIds,
            batch =>
            {
                // A request for several items nests them under "items"; a single item comes back unnested.
                var prefix = batch.Length > 1 ? "items." : string.Empty;
                var fields = string.Join(',', new[] { "itemID", "listings.pricePerUnit", "listings.quantity", "listings.worldID", "listings.hq", "listings.onMannequin" }
                    .Select(f => prefix + f));
                return $"{CurrentUrl}{Uri.EscapeDataString(scope)}/{string.Join(',', batch)}?listings={listingsPerItem}&entries=0&fields={fields}";
            },
            UniversalisParser.ParseListings,
            token,
            maxConcurrency,
            onProgress: null);

    private async Task<Dictionary<uint, T>> FetchBatchesAsync<T>(
        IEnumerable<uint> itemIds,
        Func<uint[], string> buildUrl,
        Func<string, Dictionary<uint, T>> parse,
        CancellationToken token,
        int maxConcurrency,
        Action<int, int>? onProgress)
    {
        var batches = itemIds.Distinct().Chunk(MaxItemsPerRequest).ToList();
        var result = new ConcurrentDictionary<uint, T>();
        var done = 0;
        using var gate = new SemaphoreSlim(maxConcurrency);

        // The first batch that fails for good stops all the others. Otherwise each remaining batch would go through its
        // own round of retries, hammering a server that is already refusing us and delaying the error message.
        using var abort = CancellationTokenSource.CreateLinkedTokenSource(token);
        var abortToken = abort.Token;

        await Task.WhenAll(batches.Select(async batch =>
        {
            await gate.WaitAsync(abortToken).ConfigureAwait(false);
            try
            {
                // A batch that was already waiting for its turn can still be let through just as another batch aborts.
                abortToken.ThrowIfCancellationRequested();

                var json = await GetWithRetryAsync(buildUrl(batch), abortToken).ConfigureAwait(false);

                foreach (var (id, value) in parse(json))
                    result[id] = value;

                onProgress?.Invoke(Interlocked.Increment(ref done), batches.Count);
                await Task.Delay(DelayBetweenRequests, abortToken).ConfigureAwait(false);
            }
            catch when (!token.IsCancellationRequested)
            {
                abort.Cancel();
                throw;
            }
            finally
            {
                gate.Release();
            }
        })).ConfigureAwait(false);

        return new Dictionary<uint, T>(result);
    }

    /// <summary>Gets <paramref name="url"/>, retrying rate limits, server errors, timeouts and network errors.</summary>
    /// <exception cref="UniversalisException">The request still failed after <see cref="MaxRetries"/> retries, or the server refused it.</exception>
    private async Task<string> GetWithRetryAsync(string url, CancellationToken token)
    {
        for (var attempt = 0; ; attempt++)
        {
            // Never start a request once the whole fetch has been given up on or cancelled.
            token.ThrowIfCancellationRequested();

            FailureKind kind;
            string detail;
            TimeSpan? retryAfter = null;

            try
            {
                using var response = await http.GetAsync(url, token).ConfigureAwait(false);

                if (response.IsSuccessStatusCode)
                    return await response.Content.ReadAsStringAsync(token).ConfigureAwait(false);

                var status = (int)response.StatusCode;
                detail = $"HTTP {status} {response.ReasonPhrase}".TrimEnd();

                // Rate limits and server errors are worth retrying; other refusals (e.g. 404) are not.
                if (response.StatusCode == HttpStatusCode.TooManyRequests)
                    kind = FailureKind.RateLimited;
                else if (status >= 500)
                    kind = FailureKind.ServerError;
                else
                    throw new UniversalisException(FailureKind.Other, detail);

                retryAfter = response.Headers.RetryAfter?.Delta;
            }
            catch (HttpRequestException ex)
            {
                kind = FailureKind.Network;
                detail = ex.Message;
            }
            catch (OperationCanceledException) when (!token.IsCancellationRequested)
            {
                // HttpClient timeout, not a cancellation by us.
                kind = FailureKind.Timeout;
                detail = "request timed out";
            }

            if (attempt >= MaxRetries)
                throw new UniversalisException(kind, $"{detail} (gave up after {MaxRetries} retries)", retryAfter);

            var wait = retryAfter ?? retryBaseDelay * (attempt + 1);
            await Task.Delay(wait > MaxInlineWait ? MaxInlineWait : wait, token).ConfigureAwait(false);
        }
    }

    public void Dispose() => http.Dispose();
}
