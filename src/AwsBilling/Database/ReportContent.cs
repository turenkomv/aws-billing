using System.Text.Json.Serialization;

namespace AwsBilling.Database;

/// <summary>
/// EF-сущность тела отчёта (таблица report_contents), один-к-одному с reports.
/// Готовый JSON-отчёт (собственный camelCase-документ, а не wire-ответ AWS);
/// отдаётся API байт-в-байт, без повторной сериализации.
/// Значения не имеют дефолтов: <c>required</c> обязывает задать их при создании.
/// </summary>
public sealed class ReportContent
{
    /// <summary>Идентификатор отчёта: первичный ключ и внешний ключ на reports.id (подставляется EF).</summary>
    public long ReportId { get; set; }

    /// <summary>Обратная навигация; null до привязки к отчёту; не сериализуется.</summary>
    [JsonIgnore]
    public Report? Report { get; set; }

    /// <summary>Сохранённый отчёт (JSON); имя колонки — от прежнего контракта «сырой ответ».</summary>
    public required string RawJson { get; set; }
}
