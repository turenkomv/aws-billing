using AwsBilling.Collection;
using AwsBilling.Database;

namespace AwsBilling.BackgroundJobs;

/// <summary>
/// Запускает один цикл коллекции сразу после старта хоста, если в БД ещё нет
/// ни одного отчёта (первый деплой / пустая база) — первый отчёт не ждёт
/// cron-срабатывания. Если отчёты уже есть, цикл не стартует: свежие данные
/// придут по расписанию.
/// Работает в фоне: ошибки — только в лог, старт хоста не блокируется.
/// </summary>
public sealed class CollectOnStartupService : IHostedService, IDisposable
{
    private const int StopGracePeriodMs = 10_000;

    private readonly IBillingCollectionRunner _runner;
    private readonly ReportsRepository _repository;
    private readonly ILogger<CollectOnStartupService> _logger;
    private CancellationTokenSource? _cts;
    private Task? _startTask;

    public CollectOnStartupService(
        IBillingCollectionRunner runner,
        ReportsRepository repository,
        ILogger<CollectOnStartupService> logger)
    {
        _runner = runner;
        _repository = repository;
        _logger = logger;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        if (_repository.HasAnyReports())
        {
            _logger.LogInformation(
                "Database already contains reports — skipping the first collection at startup.");
            return Task.CompletedTask;
        }

        _logger.LogInformation(
            "Database is empty — starting first collection cycle in the background.");

        _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var token = _cts.Token;

        _startTask = Task.Run(async () =>
        {
            try
            {
                await _runner.RunAsync(token);
            }
            catch (OperationCanceledException)
            {
                _logger.LogInformation(
                    "First collection at startup was cancelled during shutdown.");
            }
            catch (Exception ex)
            {
                _logger.LogError(
                    "First collection at startup failed: {Error}; host keeps running, waiting for the next cron trigger.",
                    ex.Message);
            }
        }, CancellationToken.None);

        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        if (_startTask is null)
            return;

        _cts?.Cancel();

        // cancellationToken (а не None): при форс-остановке задержка не тянет 10 секунд —
        // Task.Delay с отменённым токеном завершается сразу, и WhenAny его не выбрасывает.
        var done = await Task.WhenAny(_startTask, Task.Delay(StopGracePeriodMs, cancellationToken));
        if (done != _startTask)
            _logger.LogWarning("First collection did not finish within {Ms} ms — proceeding with shutdown.", StopGracePeriodMs);
        else
            await _startTask; // исключения уже обработаны внутри задачи — ждём только завершения

        _cts?.Dispose();
        _cts = null;
    }

    public void Dispose() => _cts?.Dispose();
}
