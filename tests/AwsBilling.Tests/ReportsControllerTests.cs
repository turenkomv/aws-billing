using System.Text.Json;
using AwsBilling.Controllers;
using AwsBilling.Database;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;

namespace AwsBilling.Tests;

/// <summary>
/// Тесты контроллера на реальном репозитории поверх in-memory БД:
/// 404 до первой коллекции, 200 + байт-в-байт тело после.
/// </summary>
public sealed class ReportsControllerTests : IDisposable
{
    private static readonly DateTime Start = new(2026, 8, 1, 0, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime End = new(2026, 8, 20, 0, 0, 0, DateTimeKind.Utc);
    private static readonly string[] ServiceNames = { "Amazon Lightsail" };

    private readonly TestRepository _testRepository;
    private readonly ReportsRepository _repository;
    private readonly ReportsController _controller;

    public ReportsControllerTests()
    {
        _testRepository = new TestRepository(TimeProvider.System);
        _repository = _testRepository.Repository;
        _repository.Migrate();
        _controller = new ReportsController(_repository, NullLogger<ReportsController>.Instance);
    }

    public void Dispose() => _testRepository.Dispose();

    [Fact]
    public void GetLatest_NoReports_Returns404()
    {
        var result = _controller.GetLatest();

        Assert.IsType<NotFoundObjectResult>(result);
    }

    [Fact]
    public void GetLatest_WithReport_Returns200AndExactReport()
    {
        var reportJson = """{"period":{"start":"2026-08-01","end":"2026-08-20"},"generatedAtUtc":"2026-08-20T00:00:12.0000000Z","source":"aws-cost-explorer:GetCostAndUsage","currency":"USD","total":{"cost":12.34},"usageTypes":[{"name":"EUN1-TotalDataXfer-Out-Bytes","cost":12.34,"quantity":2000,"unit":"Bytes"}]}""";
        _repository.Insert(reportJson, Start, End, ServiceNames, 1);

        var result = _controller.GetLatest();

        var content = Assert.IsType<ContentResult>(result);
        Assert.Equal("application/json; charset=utf-8", content.ContentType);
        Assert.Equal(reportJson, content.Content);
    }

    [Fact]
    public void GetById_NoSuchReport_Returns404()
    {
        var result = _controller.GetById(999);

        Assert.IsType<NotFoundObjectResult>(result);
    }

    [Fact]
    public void GetById_ExistingReport_Returns200AndExactReport()
    {
        var reportJson = """{"period":{"start":"2026-08-01","end":"2026-08-20"},"currency":"USD","total":{"cost":12.34},"usageTypes":[{"name":"EUN1-TotalDataXfer-Out-Bytes","cost":12.34,"quantity":2000,"unit":"Bytes"}]}""";
        var record = _repository.Insert(reportJson, Start, End, ServiceNames, 1);

        var result = _controller.GetById(record.Id);

        var content = Assert.IsType<ContentResult>(result);
        Assert.Equal("application/json; charset=utf-8", content.ContentType);
        Assert.Equal(reportJson, content.Content);
    }

    [Fact]
    public void List_ReturnsMetadataOnly_NewestFirst()
    {
        _repository.Insert("report-1", Start, End, ServiceNames, 1);
        _repository.Insert("report-2", Start, End, ServiceNames, 2);

        var result = _controller.List(take: 1);

        var ok = Assert.IsType<OkObjectResult>(result);
        var items = Assert.IsAssignableFrom<IEnumerable<Report>>(ok.Value);
        var first = Assert.Single(items);

        Assert.Equal("Amazon Lightsail", first.ServiceNames);
    }

    [Fact]
    public void List_TakeClamped_NegativeBecomesOne()
    {
        _repository.Insert("report-1", Start, End, ServiceNames, 1);
        _repository.Insert("report-2", Start, End, ServiceNames, 2);

        var result = _controller.List(take: -5);

        var ok = Assert.IsType<OkObjectResult>(result);
        var items = Assert.IsAssignableFrom<IEnumerable<Report>>(ok.Value);
        var records = items.ToList();

        Assert.Single(records);
        Assert.Equal("Amazon Lightsail", records[0].ServiceNames);
    }

    [Fact]
    public void List_SerializedEntity_ServiceNamesCsvAndNoContent()
    {
        _repository.Insert("report-1", Start, End, ["Amazon Lightsail", "Amazon CloudWatch"], 1);
        var report = Assert.Single(_repository.List(10));

        // Те же опции сериализации, что и у ASP.NET Core (camelCase).
        var json = JsonSerializer.Serialize(report, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        Assert.Equal("Amazon Lightsail,Amazon CloudWatch", root.GetProperty("serviceNames").GetString());
        Assert.False(root.TryGetProperty("content", out _));
    }
}
