using System.Text.Json.Serialization;

namespace AwsBilling.Database;

/// <summary>
/// EF-сущность одного сохранённого отчёта (таблица reports) — только метаданные.
/// Тело отчёта (JSON) лежит в таблице report_contents (сущность ReportContent,
/// связь один-к-одному по id отчёта).
/// Даты — ISO-8601 UTC-текст: лексикографический порядок = хронологический.
/// Значения не имеют дефолтов: <c>required</c> обязывает задать их при создании.
/// Сущность сама по себе — модель API списка метаданных:
/// навигация <c>Content</c> в JSON не сериализуется.
/// </summary>
public sealed class Report
{
    public long Id { get; set; }

    public required string CollectedAtUtc { get; set; }

    public required string PeriodStart { get; set; }

    public required string PeriodEnd { get; set; }

    /// <summary>Имена сервисов, разделённые запятой (CSV); запятые в именах не поддерживаются.</summary>
    public required string ServiceNames { get; set; }

    public int PageCount { get; set; }

    public int ByteSize { get; set; }

    /// <summary>
    /// Тело отчёта (таблица report_contents, один-к-одному); не сериализуется.
    /// Nullable-навигация (в метаданных списка не загружена); обязательность связи
    /// задаёт первичный ключ report_contents, а не nullability этого свойства.
    /// (required + [JsonIgnore] в .NET 10 несовместимы: required
    /// имплицитно означает [JsonRequired], а STJ запрещает required для ignored.)
    /// </summary>
    [JsonIgnore]
    public ReportContent? Content { get; set; }
}
