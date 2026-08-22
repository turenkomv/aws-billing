using Amazon.Runtime;
using AwsBilling.BackgroundJobs;
using AwsBilling.Collection;
using AwsBilling.Configuration;
using AwsBilling.Database;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Quartz;

namespace AwsBilling.Tests;

/// <summary>
/// Quartz-задача: любая ошибка коллекции — только в лог, без исключения наружу
/// (хост продолжает жить и отдавать прежний отчёт); при отмене на остановке
/// хоста — тоже не ошибка.
/// </summary>
public sealed class BillingCollectionJobTests
{
    private static IOptions<AppOptions> AppOptions()
        => Options.Create(new AppOptions
        {
            Region = "us-east-1",
            AccessKeyId = "AKIA...",
            SecretAccessKey = "secret",
        });

    [Fact]
    public async Task Execute_AwsAccessDenied_LogsHintAndDoesNotPropagate()
    {
        var logger = new CapturingLogger<BillingCollectionJob>();
        var job = new BillingCollectionJob(
            new ThrowingRunner(new AmazonServiceException("Access to the Cost Explorer service is denied.")
            {
                ErrorCode = "AccessDenied",
            }),
            AppOptions(),
            logger);

        await job.Execute(FakeContext(CancellationToken.None));

        var messages = string.Join("\n", logger.Messages);
        Assert.Contains("Access denied by AWS", messages);
        Assert.Contains("ce:GetCostAndUsage", messages);
        Assert.Contains("us-east-1", messages);
    }

    [Fact]
    public async Task Execute_GeneralFailure_LogsAndDoesNotPropagate()
    {
        var logger = new CapturingLogger<BillingCollectionJob>();
        var job = new BillingCollectionJob(
            new ThrowingRunner(new InvalidDataException("bad amount from AWS")),
            AppOptions(),
            logger);

        await job.Execute(FakeContext(CancellationToken.None));

        Assert.Contains("Billing collection failed", string.Join("\n", logger.Messages));
    }

    [Fact]
    public async Task Execute_CancelledOnShutdown_LogsInfoNotError()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var logger = new CapturingLogger<BillingCollectionJob>();
        var job = new BillingCollectionJob(
            new ThrowingRunner(new OperationCanceledException()),
            AppOptions(),
            logger);

        await job.Execute(FakeContext(cts.Token));

        var messages = string.Join("\n", logger.Messages);
        Assert.Contains("cancelled", messages);
        Assert.DoesNotContain("Billing collection failed", messages);
    }

    /// <summary>Бросает заданное исключение из RunAsync.</summary>
    private sealed class ThrowingRunner(Exception exception) : IBillingCollectionRunner
    {
        public Task<Report?> RunAsync(CancellationToken cancellationToken = default)
            => throw exception;
    }

    private static IJobExecutionContext FakeContext(CancellationToken token)
        => new FakeJobExecutionContext(token);

    /// <summary>Минимальный контекст: джоба использует только CancellationToken.</summary>
    private sealed class FakeJobExecutionContext(CancellationToken cancellationToken) : IJobExecutionContext
    {
        public IScheduler Scheduler { get; } = null!;
        public ITrigger Trigger { get; } = null!;
        public ICalendar Calendar { get; } = null!;
        public bool Recovering { get; }
        public TriggerKey RecoveringTriggerKey { get; } = null!;
        public int RefireCount { get; }
        public JobDataMap MergedJobDataMap { get; } = new();
        public IJobDetail JobDetail { get; } = null!;
        public IJob JobInstance { get; } = null!;
        public DateTimeOffset FireTimeUtc { get; } = DateTimeOffset.UnixEpoch;
        public DateTimeOffset? ScheduledFireTimeUtc { get; }
        public DateTimeOffset? PreviousFireTimeUtc { get; }
        public DateTimeOffset? NextFireTimeUtc { get; }
        public string FireInstanceId { get; } = "test";
        public object? Result { get; set; }
        public TimeSpan JobRunTime { get; }
        public CancellationToken CancellationToken { get; } = cancellationToken;

        public object Get(object key) => throw new NotSupportedException();
        public void Put(object key, object value) { }
    }
}
