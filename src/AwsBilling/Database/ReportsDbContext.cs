using Microsoft.EntityFrameworkCore;

namespace AwsBilling.Database;

/// <summary>
/// EF-контекст: reports (метаданные, append-only история отчётов)
/// и report_contents (JSON-тело отчёта, один-к-одному по id отчёта).
/// Имена таблиц и колонок сохраняют прежний контракт схемы.
/// </summary>
public sealed class ReportsDbContext : DbContext
{
    public ReportsDbContext(DbContextOptions<ReportsDbContext> options)
        : base(options)
    {
    }

    public DbSet<Report> Reports => Set<Report>();

    public DbSet<ReportContent> ReportContents => Set<ReportContent>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        var report = modelBuilder.Entity<Report>();

        report.ToTable("reports");

        report.Property(r => r.Id).HasColumnName("id");
        report.Property(r => r.CollectedAtUtc).HasColumnName("collected_at_utc");
        report.Property(r => r.PeriodStart).HasColumnName("period_start");
        report.Property(r => r.PeriodEnd).HasColumnName("period_end");
        report.Property(r => r.ServiceNames).HasColumnName("service_names");
        report.Property(r => r.PageCount).HasColumnName("page_count");
        report.Property(r => r.ByteSize).HasColumnName("byte_size");

        var content = modelBuilder.Entity<ReportContent>();

        content.ToTable("report_contents");

        content.HasKey(c => c.ReportId);
        content.Property(c => c.ReportId).HasColumnName("report_id");
        content.Property(c => c.RawJson).HasColumnName("raw_json");

        report
            .HasOne(r => r.Content)
            .WithOne(c => c.Report)
            .HasForeignKey<ReportContent>(c => c.ReportId);
    }
}
