using AwsBilling.Configuration;
using Microsoft.Extensions.Options;

namespace AwsBilling.Tests;

/// <summary>
/// Валидатор — единственный «fail-fast» рубеж: некорректная конфигурация
/// должна ронять хост на старте с понятным сообщением.
/// </summary>
public sealed class OptionsValidatorsTests
{
    /// <summary>Полностью валидные опции; каждый тест ломает ровно один ключ.</summary>
    private static AppOptions Valid() => new()
    {
        Region = "us-east-1",
        AccessKeyId = "AKIA...",
        SecretAccessKey = "secret",
        Cron = "0 30 4 1-15/3,16-24/2,25-31 * ?",
        ServiceNames = [ "Amazon Lightsail" ],
        DatabasePath = "aws-billing.db",
    };

    private static ValidateOptionsResult Validate(AppOptions options)
        => new AppOptionsValidator().Validate(name: null, options);

    [Fact]
    public void Valid_Passes()
    {
        Assert.True(Validate(Valid()).Succeeded);
    }

    [Fact]
    public void MissingCron_Fails()
    {
        var options = Valid();
        options.Cron = null;

        var result = Validate(options);

        Assert.False(result.Succeeded);
        Assert.Contains("App:Cron", result.FailureMessage);
    }

    [Fact]
    public void UnparsableCron_Fails()
    {
        var options = Valid();
        options.Cron = "not-a-cron";

        var result = Validate(options);

        Assert.False(result.Succeeded);
        Assert.Contains("App:Cron", result.FailureMessage);
    }

    [Fact]
    public void EmptyServiceNames_Fails()
    {
        var options = Valid();
        options.ServiceNames = Array.Empty<string>();

        var result = Validate(options);

        Assert.False(result.Succeeded);
        Assert.Contains("ServiceNames", result.FailureMessage);
    }

    [Fact]
    public void NullServiceNames_Fails()
    {
        var options = Valid();
        options.ServiceNames = null;

        var result = Validate(options);

        Assert.False(result.Succeeded);
        Assert.Contains("ServiceNames", result.FailureMessage);
    }

    [Fact]
    public void MissingRegion_Fails()
    {
        var options = Valid();
        options.Region = null;

        var result = Validate(options);

        Assert.False(result.Succeeded);
        Assert.Contains("App:Region", result.FailureMessage);
    }

    [Fact]
    public void UnknownRegion_Fails()
    {
        // Опечатка в регионе должна ронять хост на старте, а не молча ломать
        // все коллекции: SDK сам проверяет только формат метки, не существование.
        var options = Valid();
        options.Region = "eu-west-22";

        var result = Validate(options);

        Assert.False(result.Succeeded);
        Assert.Contains("App:Region", result.FailureMessage);
    }

    [Fact]
    public void EmptyCredentials_Fails()
    {
        var options = Valid();
        options.AccessKeyId = string.Empty;
        options.SecretAccessKey = string.Empty;

        var result = Validate(options);

        Assert.False(result.Succeeded);
        Assert.Contains("both required", result.FailureMessage);
    }

    [Fact]
    public void HalfSetCredentials_Fails()
    {
        var options = Valid();
        options.SecretAccessKey = string.Empty;

        var result = Validate(options);

        Assert.False(result.Succeeded);
        Assert.Contains("both required", result.FailureMessage);
    }

    [Fact]
    public void MissingDatabasePath_Fails()
    {
        var options = Valid();
        options.DatabasePath = null;

        var result = Validate(options);

        Assert.False(result.Succeeded);
        Assert.Contains("App:DatabasePath", result.FailureMessage);
    }

    [Fact]
    public void AbsoluteDatabasePath_Passes()
    {
        var options = Valid();
        options.DatabasePath = "/var/lib/aws-billing/aws-billing.db";

        Assert.True(Validate(options).Succeeded);
    }
}
