using System.Text;
using AwsBilling.Database;
using Microsoft.Data.Sqlite;

namespace AwsBilling.Tests;

/// <summary>
/// Реальные тесты репозитория на in-memory SQLite (без сети, без AWS, без файлов).
/// </summary>
public sealed class ReportsRepositoryTests : IDisposable
{
    private static readonly DateTime Start = new(2026, 8, 1, 0, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime End = new(2026, 8, 20, 0, 0, 0, DateTimeKind.Utc);
    // Фиксированный «сейчас» для collected_at_utc — метка времени вставки детерминирована.
    private static readonly DateTimeOffset CollectedAtUtc = new(2026, 8, 20, 12, 0, 0, TimeSpan.Zero);
    private static readonly string[] ServiceNames = { "Amazon Lightsail" };

    private readonly TestRepository _testRepository;
    private readonly ReportsRepository _repository;

    public ReportsRepositoryTests()
    {
        _testRepository = new TestRepository(new FixedTimeProvider(CollectedAtUtc));
        _repository = _testRepository.Repository;
        _repository.Migrate();
    }

    public void Dispose() => _testRepository.Dispose();

    [Fact]
    public void GetLatestContent_EmptyDatabase_ReturnsNull()
    {
        Assert.Null(_repository.GetLatestContent());
        Assert.Null(_repository.GetContentById(1));
    }

    [Fact]
    public void Insert_StoresRows_AndGetLatestContentReturnsIt()
    {
        var reportJson = """{"resultsByTime":[]}""";
        var record = _repository.Insert(reportJson, Start, End, ServiceNames, 1);

        var content = _repository.GetLatestContent();
        Assert.NotNull(content);
        Assert.Equal(record.Id, content.ReportId);
        Assert.Equal(reportJson, content.RawJson);

        var meta = _repository.List(10).Single();
        Assert.Equal(record.Id, meta.Id);
        Assert.Equal("2026-08-01", meta.PeriodStart);
        Assert.Equal("2026-08-20", meta.PeriodEnd);
        Assert.Equal("Amazon Lightsail", meta.ServiceNames);
        Assert.Equal(1, meta.PageCount);
        Assert.Equal(Encoding.UTF8.GetByteCount(reportJson), meta.ByteSize);
        Assert.Equal("2026-08-20T12:00:00.0000000Z", meta.CollectedAtUtc);
    }

    [Fact]
    public void Insert_Twice_GetLatestContentReturnsSecond()
    {
        var first = _repository.Insert("first", Start, End, ServiceNames, 1);
        var second = _repository.Insert("second", Start, End, ServiceNames, 2);

        var latest = _repository.GetLatestContent();

        Assert.NotNull(latest);
        Assert.True(second.Id > first.Id);
        Assert.Equal(second.Id, latest.ReportId);
        Assert.Equal("second", latest.RawJson);
    }

    [Fact]
    public void List_ReturnsNewestFirst_RespectsTake()
    {
        for (var i = 1; i <= 3; i++)
            _repository.Insert($"report-{i}", Start, End, ServiceNames, 1);

        var list = _repository.List(take: 2);

        Assert.Equal(2, list.Count);
        Assert.Equal(3, list[0].Id);
        Assert.Equal(2, list[1].Id);
        Assert.Equal("Amazon Lightsail", list[0].ServiceNames);
    }

    [Fact]
    public void Insert_BodyStoredInReportContents_MetadataInReports()
    {
        var reportJson = """{"total":{"cost":1.23}}""";
        var record = _repository.Insert(reportJson, Start, End, ServiceNames, 1);

        using var command = _testRepository.Connection.CreateCommand();
        command.CommandText = "SELECT raw_json FROM report_contents WHERE report_id = $id;";
        command.Parameters.AddWithValue("$id", record.Id);
        Assert.Equal(reportJson, command.ExecuteScalar());
    }

    [Fact]
    public void Migrate_IsIdempotent()
    {
        _repository.Migrate();
        _repository.Migrate();

        _repository.Insert("still-works", Start, End, ServiceNames, 1);

        Assert.NotNull(_repository.GetLatestContent());
    }

    [Fact]
    public void Insert_MultipleServiceNames_RoundTripThroughCsv()
    {
        var names = new[] { "Amazon Lightsail", "Amazon CloudWatch" };
        _repository.Insert("report", Start, End, names, 1);

        var meta = _repository.List(10).Single();
        Assert.Equal("Amazon Lightsail,Amazon CloudWatch", meta.ServiceNames);

        using var command = _testRepository.Connection.CreateCommand();
        command.CommandText = "SELECT service_names FROM reports ORDER BY id DESC LIMIT 1;";
        Assert.Equal("Amazon Lightsail,Amazon CloudWatch", command.ExecuteScalar());
    }

    [Fact]
    public void Insert_NameWithComma_IsStoredAndReturnedAsIs()
    {
        _repository.Insert("report", Start, End, new[] { "A, B" }, 1);

        var meta = _repository.List(10).Single();
        Assert.Equal("A, B", meta.ServiceNames);
    }

    private sealed class FixedTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => utcNow;
    }
}
