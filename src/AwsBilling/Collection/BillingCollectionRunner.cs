using Microsoft.Extensions.Options;
using AwsBilling.Configuration;
using AwsBilling.Database;

namespace AwsBilling.Collection;

/// <summary>Пайплайн «коллекция → сохранение».</summary>
public sealed class BillingCollectionRunner : IBillingCollectionRunner, IDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1);

    private readonly ICostExplorerCollector _collector;
    private readonly ReportsRepository _repository;
    private readonly AppOptions _options;
    private readonly ILogger<BillingCollectionRunner> _logger;

    public BillingCollectionRunner(
        ICostExplorerCollector collector,
        ReportsRepository repository,
        IOptions<AppOptions> options,
        ILogger<BillingCollectionRunner> logger)
    {
        _collector = collector;
        _repository = repository;
        _options = options.Value;
        _logger = logger;
    }

    /// <summary>
    /// Выполняет один полный цикл. Бросает исключение при ошибке —
    /// политику применения (лог + продолжение работы хоста) задаёт вызывающий.
    /// Возвращает null, если уже идёт другой цикл: такой вызов пропускается
    /// (не ставится в очередь) — нет параллельных AWS-вызовов и дубля в истории.
    /// </summary>
    public async Task<Report?> RunAsync(CancellationToken cancellationToken = default)
    {
        if (!_gate.Wait(0, cancellationToken))
        {
            _logger.LogInformation("Collection skipped: another cycle is already in progress.");
            return null;
        }

        try
        {
            var serviceNames = _options.ServiceNames!;
            var result = await _collector.CollectAsync(serviceNames, cancellationToken);

            var report = _repository.Insert(
                result.ReportJson,
                result.PeriodStartUtc,
                result.PeriodEndUtc,
                serviceNames,
                result.PageCount);

            _logger.LogInformation(
                "Stored report #{Id}: {PeriodStart} -> {PeriodEnd}, {Bytes} bytes, {Pages} page(s).",
                report.Id, report.PeriodStart, report.PeriodEnd, report.ByteSize, report.PageCount);

            return report;
        }
        finally
        {
            _gate.Release();
        }
    }

    public void Dispose() => _gate.Dispose();
}
