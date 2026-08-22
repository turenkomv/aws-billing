# AwsBilling — руководство для агентов

## Назначение

ASP.NET Core-сервис на .NET 10 по cron собирает расходы Amazon Lightsail из AWS
Cost Explorer за текущий месяц, строит упрощённый месячный отчёт и хранит его в
SQLite рядом с бинарником. API:

- `GET /api/reports/latest` и `GET /api/reports/{id}` — сохранённый JSON отчёта;
- `GET /api/reports?take=N` — метаданные без тел отчётов.

## Проверка изменений

```bash
dotnet build -c Release
dotnet test                 # 41 тест; AWS-креды не нужны
dotnet run --project src/AwsBilling   # http://localhost:80
```

Решение: `AwsBilling.slnx`, не `.sln`.

## Контракты, которые нельзя случайно менять

- Единственный исходный конфигурационный файл — `src/AwsBilling/appsettings.json`.
  Не добавлять environment-specific `appsettings.*.json` или переменную окружения
  в `launchSettings.json`. Перед завершением такой правки проверить файлы на диске.
- Настройки — единый класс `AppOptions` в одной секции `App` (AWS-ключи и регион,
  cron, сервисы, путь к БД). Все ключи секции обязательны: nullable-свойства без
  значений по умолчанию, валидатор `IValidateOptions<T>`, `ValidateOnStart()` и
  явный доступ к `.Value` при старте хоста.
- HTTP endpoint задаётся в `Kestrel:Endpoints:Http:Url`; текущий контракт —
  `http://*:80`.
- AWS-ключи `App:AccessKeyId`/`App:SecretAccessKey` обязательны и не пусты:
  клиент строится только с `BasicAWSCredentials` (default credential chain
  не используется). Отсутствие или половина пары останавливает старт.
- Отчёт — собственный camelCase JSON-документ, не wire-ответ AWS. Его контракт
  строит публичный статический `CostExplorerCollector.BuildReport`; `total.cost`
  — сумма за месяц, `usageTypes` отсортированы по убыванию стоимости. Не менять
  контракт без обновления `MonthlyReportTests` и README.
- `GET .../latest` и `GET .../{id}` возвращают строку из БД без повторной
  сериализации. Сохранение append-only; последний отчёт — с максимальным `id`.
- Отчёт лежит в двух таблицах: `reports` (метаданные) и `report_contents`
  (JSON-тело `raw_json`, один-к-одному, первичный ключ — id отчёта).
  Список метаданных читает только `reports` и не должен подтягивать `report_contents`.
  `serviceNames` хранится в `reports.service_names` CSV-строкой (разделитель «,»)
  и API отдаёт его как есть (CSV-строка, не JSON-массив); запятые внутри имён не
  поддерживаются (имя будет распознано как несколько имён).
  Сущности `Report`/`ReportContent` — и EF-модель, и модель API (DTO нет):
  навигации не сериализуются ([JsonIgnore]); в .NET 10 `required` имплицитно
  означает [JsonRequired], поэтому у [JsonIgnore]-свойства `required` быть не может.
- Метрики отчёта фиксированы: `UnblendedCost` и `UsageQuantity`; запрос использует
  `DAILY` и `GroupBy USAGE_TYPE`, а агрегация выполняется приложением.

## Архитектура

`Program.cs` — composition root и fail-fast старт (клиент AWS
`IAmazonCostExplorer` регистрируется там же синглтоном) →
`CostExplorerCollector` — запрос, пагинация, отчёт →
`BillingCollectionRunner` — гейт, сохранение и логирование → Quartz job /
`CollectOnStartupService` → `ReportsRepository` (EF Core) → `ReportsController`.

Зависимости между слоями задаются интерфейсами `IAmazonCostExplorer`
(клиент AWS SDK; коллектор работает с ним напрямую — отдельного адаптера
нет), `ICostExplorerCollector` и `IBillingCollectionRunner`. В тестах
`IAmazonCostExplorer` мокится через Moq. Для времени используется `TimeProvider`
(границы месяца у коллектора, `collected_at_utc` в репозитории), чтобы тесты
были детерминированными.

## Важные особенности SDK и SQLite

- AWS SDK 4.x: `RegionEndpoint` находится в `Amazon`; `GetBySystemName` принимает
  неизвестные RFC-1123 имена, поэтому регион проверяет `AppOptionsValidator`.
  `NextPageToken` — токен пагинации, `DateInterval.End` эксклюзивен.
- В фильтре Cost Explorer использовать `Dimension.SERVICE` и человеческие имена
  сервисов: `SERVICE_CODE` допустим только для group-by.
- Не включать SQLite shared cache: в connection string задано `Cache=Private`
  (EF-провайдер не фиксирует режим сам — указываем явно). Журнал — WAL:
  его включает сам EF Sqlite-провайдер при создании базы (режим персистентен,
  хранится в заголовке файла); в WAL читатели не блокируются записью,
  `busy_timeout=5000` (`Default Timeout=5` в connection string) — страховка.
  Рядом с БД лежат штатные `-wal`/`-shm` файлы (при чистом останове хоста
  SQLite чекапит WAL и удаляет их; после `kill` остаются — это нормально).
  В тестах файловая БД не используется: in-memory SQLite (`Data Source=:memory:`) с
  одним общим открытым соединением в `TestRepository` — все контексты из фабрики идут
  через него (`UseSqlite(connection)` на открытое соединение, которое EF не закрывает;
  закрывает его `TestRepository.Dispose()`), cleanup файлов не нужен.
- Схема — EF Core миграции (`src/AwsBilling/Database/Migrations/`), применяются
  на старте хоста: `ReportsRepository.Migrate()` (идемпотентно). Новые миграции:
  `dotnet ef migrations add <Имя> --project src/AwsBilling --output-dir Database/Migrations`
  (нужен global-инструмент dotnet-ef 10.x; `--output-dir` резолвится относительно
  каталога проекта, а не cwd). Для dotnet-ef есть design-time фабрика
  `ReportsDbContextFactory` (`IDesignTimeDbContextFactory`, плейсхолдер-соединение);
  она не участвует в рантайме — не удалять и не менять её роль.
- DbContext создаётся на каждую операцию через стандартную EF-фабрику:
  `AddDbContextFactory<ReportsDbContext>` в Program.cs (строка соединения строится
  там же, лениво при первом создании контекста), репозиторий получает
  `IDbContextFactory<ReportsDbContext>`. В EF 10 порядок аргументов двухаргументной
  лямбды — `(IServiceProvider, DbContextOptionsBuilder)`, провайдер первым.
- В тестах сервис-провайдер с фабрикой живёт, пока жив репозиторий
  (`TestRepository` — IDisposable): EF лениво резолвит из провайдера свои
  внутренние сервисы при каждом `CreateDbContext()`, выброшенный провайдер
  роняет контекст с `ObjectDisposedException`.
- Схема — одна миграция `Initial`: создаёт `reports` + `report_contents`
  как для новой БД (прежние `InitialCreate`, `SplitReportIntoMetadataAndJson`,
  `ServiceNamesJsonToCsv` слиты в неё; перенос данных из старых схем не
  выполняется — БД считается новой).
- БД считается новой (решение): поддержка «принятия» файлов, созданных до EF
  (ручное заполнение `__EFMigrationsHistory`, перенос данных) не требуется —
  не добавлять такой код.
- `BillingCollectionRunner` использует `SemaphoreSlim` с семантикой skip-if-busy:
  параллельный запуск возвращает `null`, а не ждёт в очереди.

## Границы продукта

- Ошибки сети/AWS логируются; хост не падает и продолжает отдавать прежний отчёт.
- Не добавлять retention-политику без отдельного требования.
- Логи — только Console, параметры берутся из `appsettings.json`.
- Комментарии в коде — на русском.
