using System.Globalization;
using System.Text.Json;
using Amazon.CostExplorer;
using Amazon.CostExplorer.Model;

namespace AwsBilling.Collection;

/// <summary>
/// Результат одного цикла коллекции: упрощённый месячный отчёт (JSON) + метаданные периода.
/// </summary>
public sealed record CollectionResult(
    string ReportJson,
    DateTime PeriodStartUtc,
    DateTime PeriodEndUtc,
    int PageCount);

/// <summary>
/// Запрос к AWS Cost Explorer (GetCostAndUsage) с фильтром по SERVICE
/// (человеческие имена сервисов) и агрегацией всех страниц пагинации
/// в один упрощённый месячный отчёт (контракт — README, тест MonthlyReportTests).
/// </summary>
public sealed class CostExplorerCollector : ICostExplorerCollector
{
    private const string CostMetric = "UnblendedCost";
    private const string QuantityMetric = "UsageQuantity";

    private static readonly JsonSerializerOptions ReportJsonOptions =
        new(JsonSerializerDefaults.Web);

    private readonly IAmazonCostExplorer _client;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<CostExplorerCollector> _logger;

    public CostExplorerCollector(
        IAmazonCostExplorer client,
        TimeProvider timeProvider,
        ILogger<CostExplorerCollector> logger)
    {
        _client = client;
        _timeProvider = timeProvider;
        _logger = logger;
    }

    public async Task<CollectionResult> CollectAsync(
        IReadOnlyList<string> serviceNames,
        CancellationToken cancellationToken)
    {
        // Период: 1-е число текущего месяца → текущий день (оба включительно).
        // Cost Explorer: Start inclusive, End EXCLUSIVE — поэтому End = день после текущего.
        var now = _timeProvider.GetUtcNow().UtcDateTime;
        var periodStartUtc = new DateTime(now.Year, now.Month, 1, 0, 0, 0, DateTimeKind.Utc);
        var periodEndUtc = new DateTime(now.Year, now.Month, now.Day, 0, 0, 0, DateTimeKind.Utc);
        var exclusiveEnd = periodEndUtc.AddDays(1);

        var request = new GetCostAndUsageRequest
        {
            TimePeriod = new DateInterval
            {
                Start = periodStartUtc.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                End = exclusiveEnd.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            },
            // DAILY-гранулярность + агрегация в месячную сумму на нашей стороне:
            // детерминированно и не зависит от поведения AWS по «месячным» бакетам
            // на неполный месяц (требование: сумма с 1-го числа по сегодня).
            Granularity = Granularity.DAILY,
            Metrics = [ CostMetric, QuantityMetric ],
            // Ограничение API Cost Explorer (проверено живым отказом AWS):
            // SERVICE_CODE допущен только для group-by, в Filter — SERVICE
            // (человеческие имена, напр. "Amazon Lightsail").
            Filter = new Expression
            {
                Dimensions = new DimensionValues
                {
                    Key = Dimension.SERVICE,
                    Values = [..serviceNames],
                },
            },
            GroupBy = [
                new() { Type = GroupDefinitionType.DIMENSION, Key = "USAGE_TYPE" },
            ],
        };

        var pages = new List<GetCostAndUsageResponse>();
        string? nextPageToken = null;
        do
        {
            request.NextPageToken = nextPageToken;
            var page = await _client.GetCostAndUsageAsync(request, cancellationToken);
            pages.Add(page);
            nextPageToken = page.NextPageToken;
        } while (!string.IsNullOrWhiteSpace(nextPageToken));

        _logger.LogInformation(
            "Fetched {PageCount} page(s) of Cost Explorer data for [{Services}] " +
            "({Start:yyyy-MM-dd} -> {End:yyyy-MM-dd}, granularity DAILY).",
            pages.Count, string.Join(", ", serviceNames), periodStartUtc, periodEndUtc);

        return new CollectionResult(
            ReportJson: BuildReport(pages, periodStartUtc, periodEndUtc, now),
            PeriodStartUtc: periodStartUtc,
            PeriodEndUtc: periodEndUtc,
            PageCount: pages.Count);
    }

    /// <summary>
    /// Агрегирует все дни и все страницы в один месячный отчёт:
    /// суммарная стоимость за месяц и разбивка по usage type
    /// (стоимость + объёмы, сначала самые дорогие).
    /// Публичный статический метод: контракт отчёта закреплён тестами (MonthlyReportTests).
    /// </summary>
    public static string BuildReport(
        IReadOnlyList<GetCostAndUsageResponse> pages,
        DateTime periodStartUtc,
        DateTime periodEndUtc,
        DateTime generatedAtUtc)
    {
        decimal totalCost = 0m;
        string? currency = null;
        var perUsageType = new Dictionary<string, UsageTypeAccumulator>();

        foreach (var page in pages)
        {
            foreach (var day in page.ResultsByTime ?? new List<ResultByTime>())
            {
                foreach (var group in day.Groups ?? new List<Group>())
                {
                    var usageType = group.Keys?.FirstOrDefault()
                        ?? throw new InvalidDataException("AWS returned a usage group without a usage type key.");
                    var cost = TryGetMetric(group.Metrics, CostMetric);
                    var quantity = TryGetMetric(group.Metrics, QuantityMetric);

                    if (cost is not null)
                    {
                        totalCost += ParseAmount(cost.Amount, CostMetric, usageType);
                        if (string.IsNullOrWhiteSpace(currency) && !string.IsNullOrWhiteSpace(cost.Unit))
                            currency = cost.Unit;
                    }

                    if (!perUsageType.TryGetValue(usageType, out var acc))
                        perUsageType[usageType] = acc = new UsageTypeAccumulator();

                    if (cost is not null)
                        acc.Cost += ParseAmount(cost.Amount, CostMetric, usageType);
                    if (quantity is not null)
                    {
                        acc.Quantity += ParseAmount(quantity.Amount, QuantityMetric, usageType);
                        if (!string.IsNullOrWhiteSpace(acc.Unit)
                            && !string.IsNullOrWhiteSpace(quantity.Unit)
                            && !string.Equals(acc.Unit, quantity.Unit, StringComparison.Ordinal))
                        {
                            throw new InvalidDataException(
                                $"AWS returned different quantity units for usage type '{usageType}'.");
                        }
                        acc.Unit ??= quantity.Unit;
                    }
                }
            }
        }

        var report = new MonthlyReport(
            Period: new ReportPeriod(
                Start: periodStartUtc.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                End: periodEndUtc.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)),
            GeneratedAtUtc: generatedAtUtc.ToString("o", CultureInfo.InvariantCulture),
            Source: "aws-cost-explorer:GetCostAndUsage",
            Currency: currency,
            Total: new ReportTotal(totalCost),
            UsageTypes: perUsageType
                .OrderByDescending(kv => kv.Value.Cost)
                .ThenBy(kv => kv.Key, StringComparer.Ordinal)
                .Select(kv => new UsageTypeReport(kv.Key, kv.Value.Cost, kv.Value.Quantity, kv.Value.Unit))
                .ToList());

        return JsonSerializer.Serialize(report, ReportJsonOptions);
    }

    private sealed class UsageTypeAccumulator
    {
        public decimal Cost { get; set; }
        public decimal Quantity { get; set; }
        public string? Unit { get; set; }
    }

    private static MetricValue? TryGetMetric(
        Dictionary<string, MetricValue>? metrics, string name)
        => metrics is not null && metrics.TryGetValue(name, out var value) ? value : null;

    private static decimal ParseAmount(string? value, string metricName, string usageType)
    {
        if (decimal.TryParse(value, NumberStyles.Number, CultureInfo.InvariantCulture, out var amount))
            return amount;

        throw new InvalidDataException(
            $"AWS returned an invalid {metricName} amount '{value}' for usage type '{usageType}'.");
    }

    private sealed record MonthlyReport(
        ReportPeriod Period,
        string GeneratedAtUtc,
        string Source,
        string? Currency,
        ReportTotal Total,
        IReadOnlyList<UsageTypeReport> UsageTypes);

    private sealed record ReportPeriod(string Start, string End);

    private sealed record ReportTotal(decimal Cost);

    private sealed record UsageTypeReport(string Name, decimal Cost, decimal Quantity, string? Unit);
}
