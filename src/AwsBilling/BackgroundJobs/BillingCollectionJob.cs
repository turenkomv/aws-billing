using Amazon.Runtime;
using AwsBilling.Collection;
using AwsBilling.Configuration;
using Microsoft.Extensions.Options;
using Quartz;

namespace AwsBilling.BackgroundJobs;

/// <summary>
/// Quartz-задача одного цикла коллекции. Ошибка сбора — только в лог:
/// хост продолжает работать и дальше отдаёт предыдущий отчёт.
/// </summary>
public sealed class BillingCollectionJob : IJob
{
    private readonly IBillingCollectionRunner _runner;
    private readonly string _region;
    private readonly ILogger<BillingCollectionJob> _logger;

    public BillingCollectionJob(
        IBillingCollectionRunner runner,
        IOptions<AppOptions> appOptions,
        ILogger<BillingCollectionJob> logger)
    {
        _runner = runner;
        _region = appOptions.Value.Region!;
        _logger = logger;
    }

    public async Task Execute(IJobExecutionContext context)
    {
        try
        {
            await _runner.RunAsync(context.CancellationToken);
        }
        catch (OperationCanceledException) when (context.CancellationToken.IsCancellationRequested)
        {
            _logger.LogInformation("Billing collection cancelled (host is shutting down).");
        }
        catch (AmazonServiceException aws)
        {
            _logger.LogError(
                "Billing collection failed (AWS error {ErrorCode}: {Error}); host keeps running, previous report still available.",
                aws.ErrorCode, aws.Message);

            if (aws.ErrorCode is not null
                && aws.ErrorCode.Contains("AccessDenied", StringComparison.OrdinalIgnoreCase))
            {
                _logger.LogWarning(
                    "Access denied by AWS: check App:AccessKeyId/App:SecretAccessKey and the " +
                    "ce:GetCostAndUsage IAM action (e.g. managed policy AWSCostExplorerReadOnlyAccess) " +
                    "for this account in region {_Region}.",
                    _region);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(
                "Billing collection failed: {Error}; host keeps running, previous report still available.",
                ex.Message);
        }
    }
}
