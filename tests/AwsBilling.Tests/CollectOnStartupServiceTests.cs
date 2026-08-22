using System.Text;
using AwsBilling.Collection;
using AwsBilling.Database;
using AwsBilling.BackgroundJobs;
using Microsoft.Extensions.Logging.Abstractions;

namespace AwsBilling.Tests;

/// <summary>
/// Стартовый сбор: пустая БД → один цикл запускается, отчёты уже есть →
/// цикл пропускается; при остановке хоста фоновый цикл получает отмену.
/// </summary>
public sealed class CollectOnStartupServiceTests : IDisposable
{
    private static readonly DateTime Start = new(2026, 8, 1, 0, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime End = new(2026, 8, 20, 0, 0, 0, DateTimeKind.Utc);

    private readonly TestRepository _testRepository;
    private readonly ReportsRepository _repository;

    public CollectOnStartupServiceTests()
    {
        _testRepository = new TestRepository(TimeProvider.System);
        _repository = _testRepository.Repository;
        _repository.Migrate();
    }

    public void Dispose() => _testRepository.Dispose();

    private static Report MakeReport(string json) => new()
    {
        Id = 1,
        CollectedAtUtc = "2026-08-20T00:00:00Z",
        PeriodStart = Start.ToString("yyyy-MM-dd"),
        PeriodEnd = End.ToString("yyyy-MM-dd"),
        ServiceNames = "Amazon Lightsail",
        PageCount = 1,
        ByteSize = Encoding.UTF8.GetByteCount(json),
        Content = new ReportContent { ReportId = 1, RawJson = json },
    };

    [Fact]
    public async Task StartAsync_EmptyDatabase_StartsFirstCollection()
    {
        var runner = new ImmediateRunner(MakeReport("report-1"));
        using var service = new CollectOnStartupService(runner, _repository,
            NullLogger<CollectOnStartupService>.Instance);

        await service.StartAsync(CancellationToken.None);

        var record = await runner.WaitForCompletion();
        Assert.NotNull(record);
        Assert.Equal("report-1", record!.Content!.RawJson);
        Assert.Equal(1, runner.Calls);

        await service.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task StartAsync_ExistingReports_SkipsCollection()
    {
        _repository.Insert("report-existing", Start, End, ["Amazon Lightsail"], 1);

        var runner = new BlockingRunner();
        using var service = new CollectOnStartupService(runner, _repository,
            NullLogger<CollectOnStartupService>.Instance);

        await service.StartAsync(CancellationToken.None);

        // Если бы цикл стартовал, RunAsync вошёл бы немедленно.
        var entered = await Task.WhenAny(runner.Entered, Task.Delay(300)) == runner.Entered;
        Assert.False(entered);

        await service.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task StartAsync_RunnerThrows_LogsAndHostSurvives()
    {
        var runner = new ThrowingRunner();
        var logger = new CapturingLogger<CollectOnStartupService>();
        using var service = new CollectOnStartupService(runner, _repository, logger);

        await service.StartAsync(CancellationToken.None);
        await runner.Entered;

        // StopAsync дожидается завершения фоновой задачи — ошибка уже залогирована.
        await service.StopAsync(CancellationToken.None);

        Assert.Contains("First collection at startup failed", string.Join("\n", logger.Messages));
    }

    [Fact]
    public async Task StopAsync_CancelsBackgroundCollection()
    {
        var runner = new BlockingRunner();
        using var service = new CollectOnStartupService(runner, _repository,
            NullLogger<CollectOnStartupService>.Instance);

        await service.StartAsync(CancellationToken.None);
        await runner.Entered;
        await service.StopAsync(CancellationToken.None);

        Assert.True(runner.WasCancelled);
    }

    /// <summary>Возвращает результат сразу и считает вызовы.</summary>
    private sealed class ImmediateRunner : IBillingCollectionRunner
    {
        private readonly Report? _record;
        private readonly TaskCompletionSource<Report?> _completed = new();
        private int _calls;

        public ImmediateRunner(Report? record) => _record = record;

        public int Calls => Volatile.Read(ref _calls);

        public Task<Report?> WaitForCompletion() => _completed.Task;

        public Task<Report?> RunAsync(CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _calls);
            _completed.TrySetResult(_record);
            return Task.FromResult(_record);
        }
    }

    /// <summary>Входит в RunAsync, затем бросает (сбой коллекции).</summary>
    private sealed class ThrowingRunner : IBillingCollectionRunner
    {
        private readonly TaskCompletionSource _entered = new();

        public Task Entered => _entered.Task;

        public Task<Report?> RunAsync(CancellationToken cancellationToken = default)
        {
            _entered.TrySetResult();
            throw new InvalidDataException("simulated collection failure");
        }
    }

    private sealed class BlockingRunner : IBillingCollectionRunner
    {
        private readonly TaskCompletionSource _entered = new();

        public Task Entered => _entered.Task;

        public bool WasCancelled { get; private set; }

        public async Task<Report?> RunAsync(CancellationToken cancellationToken = default)
        {
            _entered.TrySetResult();
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                WasCancelled = true;
                throw;
            }

            return null;
        }
    }
}
