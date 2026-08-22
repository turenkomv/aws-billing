using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace AwsBilling.Database;

/// <summary>
/// Design-time фабрика для инструментов dotnet-ef (генерация миграций).
/// String-соединения в миграциях не попадает, поэтому DataSource — любой
/// плейсхолдер; реальная строка строится в ReportsRepository.
/// Не участвует в рантайме приложения.
/// </summary>
public sealed class ReportsDbContextFactory : IDesignTimeDbContextFactory<ReportsDbContext>
{
    public ReportsDbContext CreateDbContext(string[] args)
    {
        var builder = new DbContextOptionsBuilder<ReportsDbContext>();
        builder.UseSqlite("Data Source=:memory:");
        return new ReportsDbContext(builder.Options);
    }
}
