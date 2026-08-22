using Amazon.CostExplorer;
using Amazon.CostExplorer.Model;
using AwsBilling.Collection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace AwsBilling.Tests;

/// <summary>Проверяет запрос и пагинацию без AWS SDK-сети.</summary>
public sealed class CostExplorerCollectorTests
{
    [Fact]
    public async Task CollectAsync_RequestsDailyUsageTypes_AndAggregatesAllPages()
    {
        var fake = CreateFakeClient(
            Page("TypeA", "1", "2", "next"),
            Page("TypeA", "3", "4", null));
        var timeProvider = new FixedTimeProvider(new DateTimeOffset(2026, 8, 20, 15, 30, 0, TimeSpan.Zero));
        var collector = new CostExplorerCollector(fake.Client.Object, timeProvider, NullLogger<CostExplorerCollector>.Instance);

        var result = await collector.CollectAsync(["Amazon Lightsail"], CancellationToken.None);

        Assert.Equal(2, result.PageCount);
        Assert.Equal(new DateTime(2026, 8, 1, 0, 0, 0, DateTimeKind.Utc), result.PeriodStartUtc);
        Assert.Equal(new DateTime(2026, 8, 20, 0, 0, 0, DateTimeKind.Utc), result.PeriodEndUtc);
        Assert.Equal(2, fake.Requests.Count);
        Assert.Equal("2026-08-01", fake.Requests[0].TimePeriod.Start);
        Assert.Equal("2026-08-21", fake.Requests[0].TimePeriod.End);
        Assert.Equal("DAILY", fake.Requests[0].Granularity.Value);
        Assert.Equal("USAGE_TYPE", fake.Requests[0].GroupBy.Single().Key);
        // Фильтр SERVICE с человеческими именами — контракт, проверенный живым
        // отказом AWS: SERVICE_CODE допущен только в group-by, не в Filter.
        var filter = fake.Requests[0].Filter!;
        Assert.Equal("SERVICE", filter.Dimensions!.Key);
        Assert.Equal(["Amazon Lightsail"], filter.Dimensions.Values);
        Assert.Equal("next", fake.Requests[1].NextPageToken);
        Assert.Contains("\"cost\":4", result.ReportJson);
    }

    private static GetCostAndUsageResponse Page(string usageType, string cost, string quantity, string? nextPageToken) => new()
    {
        NextPageToken = nextPageToken,
        ResultsByTime =
        [
            new ResultByTime
            {
                Groups =
                [
                    new Group
                    {
                        Keys = [usageType],
                        Metrics = new Dictionary<string, MetricValue>
                        {
                            ["UnblendedCost"] = new() { Amount = cost, Unit = "USD" },
                            ["UsageQuantity"] = new() { Amount = quantity, Unit = "Bytes" },
                        },
                    },
                ],
            },
        ],
    };

    private sealed record FakeClient(Mock<IAmazonCostExplorer> Client, List<GetCostAndUsageRequest> Requests);

    private static FakeClient CreateFakeClient(params GetCostAndUsageResponse[] pages)
    {
        var queue = new Queue<GetCostAndUsageResponse>(pages);
        var requests = new List<GetCostAndUsageRequest>();
        var client = new Mock<IAmazonCostExplorer>();
        client
            .Setup(c => c.GetCostAndUsageAsync(It.IsAny<GetCostAndUsageRequest>(), It.IsAny<CancellationToken>()))
            .Returns((GetCostAndUsageRequest request, CancellationToken _) =>
            {
                requests.Add(request);
                return Task.FromResult(queue.Dequeue());
            });
        return new FakeClient(client, requests);
    }

    private sealed class FixedTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => utcNow;
    }
}
