namespace AwsBilling.Configuration;

/// <summary>
/// Все настройки приложения в одном классе — единая секция "App" в appsettings.json:
/// доступ к AWS (регион, ключи), параметры сбора биллинга (cron, сервисы)
/// и путь к файлу SQLite.
/// Дефолтов в коде нет: все ключи секции обязательны в конфигурации
/// (проверяется AppOptionsValidator на старте хоста).
/// Ключи доступа обязательны: клиент Cost Explorer строится только
/// с явными креденшиалами (BasicAWSCredentials).
/// </summary>
public sealed class AppOptions
{
    public const string SectionName = "App";

    // --- AWS -------------------------------------------------------------------

    /// <summary>Регион для клиента Cost Explorer, например "us-east-1". Обязателен.</summary>
    public string? Region { get; set; }

    /// <summary>Access Key ID. Обязателен вместе с SecretAccessKey.</summary>
    public string? AccessKeyId { get; set; }

    /// <summary>Secret Access Key. Обязателен вместе с AccessKeyId.</summary>
    public string? SecretAccessKey { get; set; }

    // --- Сбор биллинга -----------------------------------------------------------

    /// <summary>
    /// Cron-выражение Quartz (6 полей, с секундами). Обязателен.
    /// </summary>
    public string? Cron { get; set; }

    /// <summary>
    /// Значения измерения SERVICE Cost Explorer (OR-связаны): человеческие имена
    /// сервисов, напр. "Amazon Lightsail". (SERVICE_CODE в Filter не допущен
    /// API Cost Explorer — только group-by.) Основное назначение — Amazon Lightsail.
    /// </summary>
    public IReadOnlyList<string>? ServiceNames { get; set; }

    // --- Хранилище -----------------------------------------------------------------

    /// <summary>
    /// Путь к файлу БД (обязателен). Относительный путь разрешается от каталога
    /// бинарника приложения (требование: файл рядом с приложением);
    /// абсолютный — принимается как есть.
    /// </summary>
    public string? DatabasePath { get; set; }

    public string ResolveDatabaseFullPath()
    {
        var path = DatabasePath
            ?? throw new InvalidOperationException(
                "App:DatabasePath is not configured (required in appsettings.json).");

        return System.IO.Path.IsPathRooted(path)
            ? path
            : System.IO.Path.Combine(AppContext.BaseDirectory, path);
    }
}
