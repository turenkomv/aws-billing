using AwsBilling.Database;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace AwsBilling.Tests;

/// <summary>
/// Тестовая сборка репозитория: стандартная EF-фабрика контекстов
/// (AddDbContextFactory → IDbContextFactory) и сам репозиторий — так же,
/// как в продакшен-DI (Program.cs).
/// БД — in-memory SQLite (Data Source=:memory:), без файлов и cleanup:
/// одно открытое соединение, которым пользуются все контексты фабрики;
/// in-memory БД живёт, пока живёт это соединение.
/// Сервис-провайдер живёт, пока жива фабрика: EF лениво резолвит из него
/// свои внутренние сервисы при каждом создании контекста, поэтому
/// провайдер не выбрасывается до Dispose этого объекта.
/// Соединение открыто до передачи в UseSqlite — EF его не открывает и не
/// закрывает; собственник — этот класс (Dispose закрывает его последним).
/// </summary>
public sealed class TestRepository : IDisposable
{
    private readonly ServiceProvider _provider;

    public TestRepository(TimeProvider timeProvider)
    {
        var connection = new SqliteConnection("Data Source=:memory:");
        connection.Open();
        Connection = connection;

        var services = new ServiceCollection();
        services.AddSingleton(timeProvider);
        services.AddDbContextFactory<ReportsDbContext>(options =>
            options.UseSqlite(connection));
        services.AddSingleton<ReportsRepository>();

        _provider = services.BuildServiceProvider();
        Repository = _provider.GetRequiredService<ReportsRepository>();
    }

    /// <summary>Репозиторий из тестовой DI-контейнера.</summary>
    public ReportsRepository Repository { get; }

    /// <summary>
    /// Единственное открытое SQLite-соединение in-memory БД —
    /// для прямых SQL-проверок таблиц (например, CSV в service_names).
    /// </summary>
    public SqliteConnection Connection { get; }

    public void Dispose()
    {
        _provider.Dispose();
        Connection.Dispose();
    }
}
