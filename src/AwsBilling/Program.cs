using Amazon;
using Amazon.CostExplorer;
using Amazon.Runtime;
using AwsBilling.Collection;
using AwsBilling.Configuration;
using AwsBilling.Database;
using AwsBilling.BackgroundJobs;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Quartz;

var builder = WebApplication.CreateBuilder(args);

builder.Logging.ClearProviders();
builder.Logging.AddConsole();

builder.Services.AddSingleton<IValidateOptions<AppOptions>, AppOptionsValidator>();

void BindOptions<T>(string section)
    where T : class
    => builder.Services.AddOptions<T>()
        .Bind(builder.Configuration.GetSection(section))
        .ValidateOnStart();

BindOptions<AppOptions>(AppOptions.SectionName);

// --- Синглтоны: AWS-клиент, коллектор, хранилище, пайплайн коллекции ----------
builder.Services.AddSingleton<IAmazonCostExplorer>(sp =>
{
    var options = sp.GetRequiredService<IOptions<AppOptions>>().Value;
    var clientConfig = new AmazonCostExplorerConfig
    {
        RegionEndpoint = RegionEndpoint.GetBySystemName(options.Region),
    };
    // Ключи не пусты: AppOptionsValidator (ValidateOnStart + форс. .Value выше)
    // уже остановил хост при их отсутствии.
    return new AmazonCostExplorerClient(
        new BasicAWSCredentials(options.AccessKeyId!, options.SecretAccessKey!),
        clientConfig);
});
builder.Services.AddSingleton<ICostExplorerCollector, CostExplorerCollector>();
builder.Services.AddSingleton(TimeProvider.System);

builder.Services.AddDbContextFactory<ReportsDbContext>((serviceProvider, options) =>
{
    // Shared-кэш не включаем: он меняет поведение table-локов
    // (читатель блокировал бы INSERT). EF SQLite-провайдер сам
    // не фиксирует Cache, поэтому указываем Private явно.
    options.UseSqlite(new SqliteConnectionStringBuilder
    {
        DataSource = serviceProvider
            .GetRequiredService<IOptions<AppOptions>>()
            .Value.ResolveDatabaseFullPath(),
        Mode = SqliteOpenMode.ReadWriteCreate,
        Cache = SqliteCacheMode.Private,
        // busy_timeout=5000: запись отчёта — короткий INSERT, читатели API
        // ждут её окончания, а не падают с "database is locked".
        DefaultTimeout = 5,
    }.ToString());
});

builder.Services.AddSingleton<ReportsRepository>();
builder.Services.AddSingleton<IBillingCollectionRunner, BillingCollectionRunner>();

// --- Quartz: одна cron-задача, расписание из App:Cron -------------------------
// Cron читаем здесь явно: отсутствие ключа падает понятной ошибкой на композиции,
// а не сырым исключением Quartz позже (корректность выражения дополнительно
// проверяет AppOptionsValidator на старте хоста — форс. .Value ниже).
var cronExpression = builder.Configuration
    .GetSection(AppOptions.SectionName)
    .GetValue<string>("Cron")
    ?? throw new InvalidOperationException(
        "App:Cron is required in appsettings.json (Quartz cron expression, 6 fields).");
builder.Services.AddQuartz(q =>
{
    var jobKey = new JobKey("billing-collection");
    q.AddJob<BillingCollectionJob>(jobKey);
    q.AddTrigger(t => t
        .ForJob(jobKey)
        .WithIdentity("billing-collection-trigger")
        // TimeZone не фиксируем: Quartz по умолчанию использует местное время,
        // расписание App:Cron интерпретируется в локальной зоне.
        .WithCronSchedule(cronExpression));
});
builder.Services.AddQuartzHostedService(o => o.WaitForJobsToComplete = true);

// Первый цикл сразу после старта: сервис сам решает в StartAsync, нужен ли он
// (пустая БД — да, отчёты уже есть — нет), поэтому регистрация безусловная.
builder.Services.AddHostedService<CollectOnStartupService>();

builder.Services.AddControllers();

var app = builder.Build();

// Форсируем загрузку опций на старте (fail-fast при некорректной конфигурации).
_ = app.Services.GetRequiredService<IOptions<AppOptions>>().Value;

// Клиент AWS строим на старте (а не лениво при первой коллекции): некорректный
// регион/конфиг клиента виден сразу, а не через 5 дней в первом cron-срабатывании.
_ = app.Services.GetRequiredService<ICostExplorerCollector>();

app.Services.GetRequiredService<ReportsRepository>().Migrate();

app.MapControllers();

app.Run();
