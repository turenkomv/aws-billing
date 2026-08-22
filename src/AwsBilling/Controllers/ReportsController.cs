using AwsBilling.Database;
using Microsoft.AspNetCore.Mvc;

namespace AwsBilling.Controllers;

/// <summary>
/// GET /api/reports/latest — последний сохранённый JSON-отчёт (байт-в-байт из БД);
/// GET /api/reports/{id} — сохранённый JSON-отчёт по id (байт-в-байт из БД);
/// GET /api/reports?take=N — список метаданных по сохранённым отчётам (без тел).
/// </summary>
[ApiController]
[Route("api/reports")]
public sealed class ReportsController : ControllerBase
{
    private const int MaxTake = 100;

    private readonly ReportsRepository _repository;
    private readonly ILogger<ReportsController> _logger;

    public ReportsController(ReportsRepository repository, ILogger<ReportsController> logger)
    {
        _repository = repository;
        _logger = logger;
    }

    [HttpGet("latest")]
    public IActionResult GetLatest()
    {
        var content = _repository.GetLatestContent();
        if (content is null)
            _logger.LogWarning("GET /api/reports/latest → 404: no report collected yet.");

        return content is null
            ? NotFound(new { error = "No billing report has been collected yet.", hint = "Run the collection (it happens automatically on the cron schedule, and at startup when the database is empty), then retry." })
            : new ContentResult
            {
                Content = content.RawJson,
                ContentType = "application/json; charset=utf-8",
            };
    }

    [HttpGet("{id:long}")]
    public IActionResult GetById(long id)
    {
        var content = _repository.GetContentById(id);
        if (content is null)
            _logger.LogWarning("GET /api/reports/{Id} → 404: report not found.", id);

        return content is null
            ? NotFound(new { error = $"Billing report #{id} does not exist.", hint = "List available reports at GET /api/reports (metadata only)." })
            : new ContentResult
            {
                Content = content.RawJson,
                ContentType = "application/json; charset=utf-8",
            };
    }

    [HttpGet]
    public IActionResult List([FromQuery] int take = 20)
    {
        // Защита от случайного/злонамеренного take=1000000 (и отрицательных):
        // метаданные дешёвые, но тел всё равно не отдаём.
        take = Math.Clamp(take, 1, MaxTake);

        return Ok(_repository.List(take));
    }
}
