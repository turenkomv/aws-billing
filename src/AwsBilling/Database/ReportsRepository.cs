using System.Globalization;
using System.Text;
using Microsoft.EntityFrameworkCore;

namespace AwsBilling.Database;

/// <summary>
/// Репозиторий сохранённых отчётов поверх EF Core (Sqlite-провайдер).
/// Append-only: каждая коллекция — новая строка reports и связанная строка
/// report_contents (тело); последний отчёт — с максимальным id.
/// Контекст берётся из стандартной EF-фабрики (IDbContextFactory, регистрация
/// в Program.cs) на каждую операцию: репозиторий — синглтон, а DbContext не
/// рассчитан на одновременный доступ из джоба и API.
/// </summary>
public sealed class ReportsRepository
{
    private readonly IDbContextFactory<ReportsDbContext> _contextFactory;
    private readonly TimeProvider _timeProvider;

    public ReportsRepository(IDbContextFactory<ReportsDbContext> contextFactory, TimeProvider timeProvider)
    {
        _contextFactory = contextFactory;
        _timeProvider = timeProvider;
    }

    /// <summary>
    /// Применяет EF-миграции (идемпотентно). Вызывается один раз на старте хоста
    /// — до первой коллекции и до первого чтения из API.
    /// </summary>
    public void Migrate()
    {
        using var context = _contextFactory.CreateDbContext();
        context.Database.Migrate();
    }

    /// <summary>
    /// Сохраняет результат одной коллекции как новый отчёт:
    /// метаданные — в reports, JSON-тело — в report_contents (одна транзакция).
    /// </summary>
    public Report Insert(
        string reportJson,
        DateTime periodStartUtc,
        DateTime periodEndUtc,
        IReadOnlyList<string> serviceNames,
        int pageCount)
    {
        // TimeProvider — как у коллектора: метка времени вставки детерминирована в тестах.
        var collectedAtUtc = _timeProvider.GetUtcNow().UtcDateTime;
        var byteSize = Encoding.UTF8.GetByteCount(reportJson);

        using var context = _contextFactory.CreateDbContext();
        var entity = new Report
        {
            CollectedAtUtc = collectedAtUtc.ToString("o", CultureInfo.InvariantCulture),
            PeriodStart = periodStartUtc.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            PeriodEnd = periodEndUtc.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            // Имена сервисов — CSV (разделитель «,»); запятые внутри имён не поддерживаются.
            ServiceNames = string.Join(',', serviceNames),
            PageCount = pageCount,
            ByteSize = byteSize,
            Content = new ReportContent { RawJson = reportJson },
        };
        context.Reports.Add(entity);
        context.SaveChanges();

        return entity;
    }

    /// <summary>
    /// Тело отчёта по id отчёта (первичный ключ report_contents) или null, если отчёта нет.
    /// Читает только строку report_contents; строку reports не загружает.
    /// </summary>
    public ReportContent? GetContentById(long reportId)
    {
        using var context = _contextFactory.CreateDbContext();
        return context.ReportContents.FirstOrDefault(c => c.ReportId == reportId);
    }

    /// <summary>
    /// Есть ли хотя бы один сохранённый отчёт. Используется на старте хоста,
    /// чтобы решить, запускать ли первый цикл коллекции (пустая БД → да).
    /// </summary>
    public bool HasAnyReports()
    {
        using var context = _contextFactory.CreateDbContext();
        return context.Reports.Any();
    }

    /// <summary>
    /// Тело последнего отчёта (с максимальным id) или null, если отчётов ещё нет.
    /// Читает только строку report_contents; строку reports не загружает.
    /// </summary>
    public ReportContent? GetLatestContent()
    {
        using var context = _contextFactory.CreateDbContext();
        return context.ReportContents
            .OrderByDescending(c => c.ReportId)
            .FirstOrDefault();
    }

    /// <summary>
    /// Метаданные последних отчётов для списка. Читает только таблицу reports —
    /// навигация Content не загружается (null), тела в список не попадают (контракт API).
    /// </summary>
    public IReadOnlyList<Report> List(int take = 20)
    {
        if (take < 1) take = 1;

        using var context = _contextFactory.CreateDbContext();
        return context.Reports
            .OrderByDescending(r => r.Id)
            .Take(take)
            .ToList();
    }
}
