using AwsBilling.Collection;
using AwsBilling.Configuration;
using AwsBilling.Database;
using Microsoft.Extensions.Logging.Abstractions;

namespace AwsBilling.Tests;

/// <summary>
/// Гейт BillingCollectionRunner — skip-if-busy: цикл, пришедший во время другого,
/// пропускается (RunAsync → null, ряд не создаётся), а последующий выполняется.
/// </summary>
public class BillingCollectionRunnerTests : IDisposable
{
    private static readonly DateTime Start = new(2026, 8, 1, 0, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime End = new(2026, 8, 20, 0, 0, 0, DateTimeKind.Utc);

    private readonly TestRepository _testRepository;
    private readonly ReportsRepository _repository;

    public BillingCollectionRunnerTests()
    {
        _testRepository = new TestRepository(TimeProvider.System);
        _repository = _testRepository.Repository;
        _repository.Migrate();
    }

    public void Dispose() => _testRepository.Dispose();

    private BillingCollectionRunner CreateRunner(
        Func<IReadOnlyList<string>, CancellationToken, Task<CollectionResult>> collect)
        => new(new MockCollector(collect), _repository,
            Microsoft.Extensions.Options.Options.Create(new AppOptions
            {
                Cron = "0 30 4 1-15/3,16-24/2,25-31 * ?",
                ServiceNames = [ "Amazon Lightsail" ],
            }),
            NullLogger<BillingCollectionRunner>.Instance);

    /// <summary>Легковесный мок коллектора через явную границу приложения.</summary>
    private sealed class MockCollector : ICostExplorerCollector
    {
        private readonly Func<IReadOnlyList<string>, CancellationToken, Task<CollectionResult>> _fn;

        public MockCollector(Func<IReadOnlyList<string>, CancellationToken, Task<CollectionResult>> fn)
            => _fn = fn;

        public Task<CollectionResult> CollectAsync(
            IReadOnlyList<string> serviceNames, CancellationToken cancellationToken)
            => _fn(serviceNames, cancellationToken);
    }

    [Fact]
    public async Task RunAsync_SecondWhileFirstInProgress_IsSkipped_NoDuplicateRow()
    {
        var collector = new GatedCollector();
        var runner = CreateRunner(collector.CollectAsync);
        using var cts = new CancellationTokenSource();

        var first = runner.RunAsync(cts.Token);
        await collector.Entered; // первый цикл уже внутри гейта

        var second = await runner.RunAsync(cts.Token);

        Assert.Null(second);

        collector.Finish(new CollectionResult("report-1", Start, End, 1));
        var record = await first;

        Assert.NotNull(record);
        Assert.Single(_repository.List(10));
    }

    [Fact]
    public async Task RunAsync_SequentialRuns_BothExecute()
    {
        var collector = new ImmediateCollector();
        var runner = CreateRunner(collector.CollectAsync);
        using var cts = new CancellationTokenSource();

        var first = await runner.RunAsync(cts.Token);
        var second = await runner.RunAsync(cts.Token);

        Assert.NotNull(first);
        Assert.NotNull(second);
        Assert.Equal(2, collector.Calls);
        Assert.Equal(2, _repository.List(10).Count);
    }

    /// <summary>Зависает в CollectAsync, пока тест явно не «отпустит» его.</summary>
    private sealed class GatedCollector
    {
        private readonly TaskCompletionSource _entered = new();
        private readonly TaskCompletionSource<CollectionResult> _finish = new();

        public Task Entered => _entered.Task;

        public void Finish(CollectionResult result) => _finish.TrySetResult(result);

        public async Task<CollectionResult> CollectAsync(
            IReadOnlyList<string> serviceNames, CancellationToken cancellationToken)
        {
            _entered.TrySetResult();
            return await _finish.Task;
        }
    }

    /// <summary>Возвращает результат сразу и считает вызовы.</summary>
    private sealed class ImmediateCollector
    {
        private int _calls;

        public int Calls => Volatile.Read(ref _calls);

        public Task<CollectionResult> CollectAsync(
            IReadOnlyList<string> serviceNames, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _calls);
            return Task.FromResult(new CollectionResult($"report-{Calls}", Start, End, 1));
        }
    }
}
