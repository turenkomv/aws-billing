using System.Text.Json;
using Amazon.CostExplorer.Model;
using AwsBilling.Collection;

namespace AwsBilling.Tests;

/// <summary>
/// Контракт упрощённого месячного отчёта: сумма за месяц (а не разбивка по дням)
/// и разбивка по usage type (стоимость + объёмы) — простым camelCase-JSON,
/// а не «сырым» ответом AWS.
/// </summary>
public sealed class MonthlyReportTests
{
    private static readonly DateTime Start = new(2026, 8, 1, 0, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime End = new(2026, 8, 20, 0, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime GeneratedAt = new(2026, 8, 20, 12, 0, 0, DateTimeKind.Utc);

    /// <summary>Одна страница: один день с группами по usage type.</summary>
    private static GetCostAndUsageResponse Page(params Group[] groups) => new()
    {
        ResultsByTime = new List<ResultByTime>
        {
            new()
            {
                TimePeriod = new DateInterval { Start = "2026-08-01", End = "2026-08-02" },
                Groups = groups.ToList(),
            },
        },
    };

    /// <summary>Группа (один usage type) с метриками стоимости и/или объёма.</summary>
    private static Group GroupOf(string usageType, string? costAmount, string? quantityAmount)
    {
        var metrics = new Dictionary<string, MetricValue>();
        if (costAmount is not null)
            metrics["UnblendedCost"] = new MetricValue { Amount = costAmount, Unit = "USD" };
        if (quantityAmount is not null)
            metrics["UsageQuantity"] = new MetricValue { Amount = quantityAmount, Unit = "Bytes" };
        return new Group
        {
            Keys = new List<string> { usageType },
            Metrics = metrics,
        };
    }

    /// <summary>Группа с одним UsageQuantity (без стоимости) и заданной единицей.</summary>
    private static Group QuantityOnlyGroup(string usageType, string amount, string unit) => new()
    {
        Keys = new List<string> { usageType },
        Metrics = new Dictionary<string, MetricValue>
        {
            ["UsageQuantity"] = new() { Amount = amount, Unit = unit },
        },
    };

    [Fact]
    public void BuildReport_SumsMonth_TotalTransferAndBreakdown()
    {
        // Два дня (две страницы), трафик Out/In + платный usage type без объёма.
        var pages = new[]
        {
            Page(
                GroupOf("EUN1-TotalDataXfer-Out-Bytes", "1.50", "1000"),
                GroupOf("EUN1-TotalDataXfer-In-Bytes", "0", "2000")),
            Page(
                GroupOf("EUN1-TotalDataXfer-Out-Bytes", "2.50", "3000"),
                GroupOf("EUN1-StaticIP", "1.00", null)),
        };

        var json = CostExplorerCollector.BuildReport(pages, Start, End, GeneratedAt);
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        Assert.Equal("2026-08-01", root.GetProperty("period").GetProperty("start").GetString());
        Assert.Equal("2026-08-20", root.GetProperty("period").GetProperty("end").GetString());

        // Сумма за месяц = все usage type обоих дней: 1.50 + 0 + 2.50 + 1.00.
        Assert.Equal(5.0m, root.GetProperty("total").GetProperty("cost").GetDecimal());
        Assert.Equal("USD", root.GetProperty("currency").GetString());

        var usageTypes = root.GetProperty("usageTypes").EnumerateArray().ToList();
        Assert.Equal(3, usageTypes.Count);
        Assert.Equal("EUN1-TotalDataXfer-Out-Bytes", usageTypes[0].GetProperty("name").GetString());
        Assert.Equal(4.0m, usageTypes[0].GetProperty("cost").GetDecimal());
        Assert.Equal(4000m, usageTypes[0].GetProperty("quantity").GetDecimal());
        Assert.Equal("Bytes", usageTypes[0].GetProperty("unit").GetString());

        Assert.Contains("\"usageTypes\"", json);
        Assert.DoesNotContain("UsageTypes", json);
        Assert.DoesNotContain("ResultsByTime", json);
    }

    [Fact]
    public void BuildReport_TypeWithoutCost_QuantityStillCounts()
    {
        GetCostAndUsageResponse[] pages = [ Page(GroupOf("USE1-TotalDataXfer-In-Bytes", null, "500")) ];

        var json = CostExplorerCollector.BuildReport(pages, Start, End, GeneratedAt);
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        Assert.Equal(0m, root.GetProperty("total").GetProperty("cost").GetDecimal());
        Assert.Null(root.GetProperty("currency").GetString());

        var entry = Assert.Single(root.GetProperty("usageTypes").EnumerateArray());
        Assert.Equal(500m, entry.GetProperty("quantity").GetDecimal());
    }

    [Fact]
    public void BuildReport_EmptyDay_ZeroDocument()
    {
        var pages = new[]
        {
            new GetCostAndUsageResponse
            {
                ResultsByTime = new List<ResultByTime>
                {
                    new()
                    {
                        TimePeriod = new DateInterval { Start = "2026-08-01", End = "2026-08-02" },
                        Groups = new List<Group>(),
                    },
                },
            },
        };

        var json = CostExplorerCollector.BuildReport(pages, Start, End, GeneratedAt);
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        Assert.Equal(0m, root.GetProperty("total").GetProperty("cost").GetDecimal());
        Assert.Empty(root.GetProperty("usageTypes").EnumerateArray());
    }

    [Fact]
    public void BuildReport_ConflictingQuantityUnits_FailsInsteadOfMixingUnits()
    {
        GetCostAndUsageResponse[] pages =
        [
            Page(QuantityOnlyGroup("USE1-StaticIP", "100", "Bytes")),
            Page(QuantityOnlyGroup("USE1-StaticIP", "50", "Gibibytes")),
        ];

        var exception = Assert.Throws<InvalidDataException>(
            () => CostExplorerCollector.BuildReport(pages, Start, End, GeneratedAt));

        Assert.Contains("different quantity units", exception.Message);
        Assert.Contains("USE1-StaticIP", exception.Message);
    }

    [Fact]
    public void BuildReport_InvalidAmount_FailsInsteadOfSilentlyUnderreporting()
    {
        var pages = new[] { Page(GroupOf("EUN1-StaticIP", "not-a-number", "1")) };

        var exception = Assert.Throws<InvalidDataException>(
            () => CostExplorerCollector.BuildReport(pages, Start, End, GeneratedAt));

        Assert.Contains("UnblendedCost", exception.Message);
        Assert.Contains("EUN1-StaticIP", exception.Message);
    }
}
