using Amazon;
using Microsoft.Extensions.Options;
using Quartz;
using System.Reflection;

namespace AwsBilling.Configuration;

/// <summary>
/// Единственный валидатор настроек приложения: проверяет секцию "App"
/// на старте хоста (fail-fast). Регион и оба ключа доступа обязательны
/// (клиент строится только с явными креденшиалами), cron обязан парситься
/// самим Quartz, список сервисов — быть непустым, путь к БД — задан.
/// </summary>
public sealed class AppOptionsValidator : IValidateOptions<AppOptions>
{
    private static readonly HashSet<string> KnownRegionSystemNames =
        typeof(RegionEndpoint)
            .GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(f => f.FieldType == typeof(RegionEndpoint))
            .Select(f => ((RegionEndpoint)f.GetValue(null)!).SystemName)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

    public ValidateOptionsResult Validate(string? name, AppOptions options)
    {
        if (string.IsNullOrWhiteSpace(options.Region))
            return ValidateOptionsResult.Fail("App:Region is required.");

        // GetBySystemName принимает любую RFC-метку без проверки — свой фильтр.
        if (!KnownRegionSystemNames.Contains(options.Region))
        {
            return ValidateOptionsResult.Fail(
                $"App:Region '{options.Region}' is not a known AWS region (e.g. us-east-1, eu-west-1).");
        }

        // Ключи обязательны: клиент строится только с BasicAWSCredentials,
        // дефолтная цепочка SDK не используется.
        if (string.IsNullOrWhiteSpace(options.AccessKeyId)
            || string.IsNullOrWhiteSpace(options.SecretAccessKey))
        {
            return ValidateOptionsResult.Fail(
                "App:AccessKeyId and App:SecretAccessKey are both required " +
                "(the Cost Explorer client is always built with explicit credentials).");
        }

        if (string.IsNullOrWhiteSpace(options.Cron))
            return ValidateOptionsResult.Fail("App:Cron is required.");

        try
        {
            _ = new CronExpression(options.Cron);
        }
        catch (Exception ex)
        {
            return ValidateOptionsResult.Fail(
                $"App:Cron '{options.Cron}' is not a valid Quartz cron expression: {ex.Message}");
        }

        if (options.ServiceNames is not { Count: > 0 }
            || options.ServiceNames.Any(string.IsNullOrWhiteSpace))
        {
            return ValidateOptionsResult.Fail(
                "App:ServiceNames must contain at least one non-empty service name (e.g. \"Amazon Lightsail\").");
        }

        if (string.IsNullOrWhiteSpace(options.DatabasePath))
            return ValidateOptionsResult.Fail("App:DatabasePath is required.");

        return ValidateOptionsResult.Success;
    }
}
