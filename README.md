# AwsBilling

ASP.NET Core-сервис (.NET 10), который по cron-расписанию запрашивает в AWS Cost Explorer
биллинг **Amazon Lightsail** за текущий месяц (с 1-го числа по текущий день), агрегирует
**упрощённый месячный отчёт** (суммарная стоимость за месяц и разбивка по типам
использования — стоимость + объёмы, напр. байты переданного трафика) и сохраняет его в SQLite (файл рядом с бинарником приложения),
возвращая сохранённые отчёты (последний или по id) **байт-в-байт** через HTTP API.

## Что делает

- Раз в N дней (cron из конфигурации) делает `GetCostAndUsage` в Cost Explorer:
  период — с 1-го числа текущего месяца по сегодняшний день, фильтр
  `SERVICE ∈ App:ServiceNames` (по умолчанию `"Amazon Lightsail"`;
  ограничение API Cost Explorer: `SERVICE_CODE` допущен только в group-by,
  а не в Filter), `Granularity: DAILY`, группировка по измерению `USAGE_TYPE`.
- Агрегирует все дни и страницы пагинации (`NextPageToken`) в один упрощённый
  месячный отчёт: сумма за месяц и разбивка по usage type (стоимость + объёмы,
  напр. `EUN1-TotalDataXfer-Out-Bytes` — байты переданного трафика).
- Хранит отчёт в SQLite (append-only: каждая коллекция
  — новая строка метаданных `reports` и связанная строка тела `report_contents`, история копится).
- `GET /api/reports/latest` и `GET /api/reports/{id}` отдают именно эту сохранённую
  строку — без повторной сериализации, байт-в-байт как в БД.
- Некорректная конфигурация **роняет хост на старте** (fail-fast) с понятным сообщением.

## Стек

| Что | Библиотека |
|---|---|
| API | ASP.NET Core Web API, `net10.0` (LTS) |
| AWS | `AWSSDK.CostExplorer` 4.0.100.9 (SDK 4.x) |
| Расписание | `Quartz` + `Quartz.Extensions.Hosting` 3.19.1 |
| Хранилище | `Microsoft.EntityFrameworkCore.Sqlite` 10.0.11 (EF Core, миграции) |
| Тесты | xunit 2.9.3 |

## Быстрый старт

Требование: **.NET 10 SDK**.

```bash
dotnet build -c Release   # 0 предупреждений (TreatWarningsAsErrors)
dotnet test               # 41 тест, AWS-креды не нужны
dotnet run --project src/AwsBilling
```

HTTP endpoint задан в `appsettings.json`: `http://*:80`. Локальный профиль запуска
использует `http://localhost:80`.

Перед первым запуском заполнить `src/AwsBilling/appsettings.json`
(секция `App` — см. «Конфигурация»).

```bash
curl -i http://localhost:80/api/reports/latest
# 200 + месячный отчёт (JSON)   (или 404, если коллекция ещё не была)
curl http://localhost:80/api/reports/1
# 200 + отчёт с этим id   (или 404, если отчёта с таким id нет)
curl "http://localhost:80/api/reports?take=10"
# 200 + массив метаданных
```

## Конфигурация

**Один файл — `src/AwsBilling/appsettings.json`**. Per-environment файлов
(`appsettings.Development.json` и т.п.) нет — сознательное решение.
**Все ключи секции `App` обязательны; дефолтов в коде нет** — отсутствующий ключ
роняет хост на старте (валидатор `IValidateOptions<AppOptions>` + `ValidateOnStart()`).

```json
{
  "App": {
    "Region": "us-east-1",
    "AccessKeyId": "",
    "SecretAccessKey": "",
    "Cron": "0 30 4 1-15/3,16-24/2,25-31 * ?",
    "ServiceNames": [ "Amazon Lightsail" ],
    "DatabasePath": "aws-billing.db"
  },
  "Kestrel": {
    "Endpoints": {
      "Http": { "Url": "http://*:80" }
    }
  },
  "Logging": {
    "LogLevel": { "Default": "Information", "Microsoft.AspNetCore": "Warning", "Quartz": "Warning" },
    "Console": { "FormatterName": "simple", "FormatterOptions": { "SingleLine": true }, "LogToStandardErrorThreshold": "Error" }
  },
  "AllowedHosts": "*"
}
```

| Ключ | Обязателен | Описание |
|---|---|---|
| `App:Region` | да | Region AWS (`us-east-1`, `eu-west-1`…). Проверяется валидатором на членство в списке регионов SDK — опечатка роняет старт, а не молча ломает коллекции. |
| `App:AccessKeyId` / `App:SecretAccessKey` | да | Оба обязательны и не пусты: клиент Cost Explorer строится только с явными креденшиалами (`BasicAWSCredentials`), дефолтная цепочка SDK не используется. Отсутствует или задана половина пары → сбой на старте с сообщением валидатора. |
| `App:Cron` | да | Cron **Quartz** (6 полей, с секундами), время — **местное**. `0 30 4 1-15/3,16-24/2,25-31 * ?` = 04:30 дней 1, 4, 7, 10, 13; 16, 18, 20, 22, 24; и ежедневно с 25-го по 31-е число месяца. Парсится самим `CronExpression`. |
| `App:ServiceNames` | да | Значения фильтра `SERVICE` (OR-связаны): человеческие имена сервисов, напр. `Amazon Lightsail`. Ограничение API Cost Explorer: `SERVICE_CODE` нельзя использовать в Filter (только group-by) — поэтому фильтр по имени, а не по коду. |
| `App:DatabasePath` | да | Путь к файлу SQLite. Относительный разрешается от каталога бинарника (требование: файл рядом с приложением); абсолютный — как есть. |
| `Kestrel:Endpoints:Http:Url` | да | Адрес HTTP endpoint. По умолчанию в проекте: `http://*:80`. |

## API

### `GET /api/reports/latest`

Последний сохранённый отчёт — та же строка, что лежит в SQLite.

`200`, `Content-Type: application/json; charset=utf-8`:

```json
{
  "period": { "start": "2026-08-01", "end": "2026-08-25" },
  "generatedAtUtc": "2026-08-25T10:25:53.9445265Z",
  "source": "aws-cost-explorer:GetCostAndUsage",
  "currency": "USD",
  "total": { "cost": 3.9030486656 },
  "usageTypes": [
    { "name": "EUN1-BundleUsage:0.5GB", "cost": 3.9030486656, "quantity": 580.81081334, "unit": "Hrs" },
    { "name": "EUN1-TotalDataXfer-In-Bytes", "cost": 0, "quantity": 278.5346244505, "unit": "GB" },
    { "name": "EUN1-TotalDataXfer-Out-Bytes", "cost": 0, "quantity": 280.3759292113, "unit": "GB" },
    { "name": "EUN1-UnusedStaticIP", "cost": 0, "quantity": 0.00545833, "unit": "Hrs" }
  ]
}
```

Поля: `total.cost` — сумма за месяц (1-е число → сегодня, все usage type);
`usageTypes` — разбивка по типам использования, сначала самые дорогие;
`quantity`/`unit` — объём и его единица в единицах, которые вернул AWS
(напр. `GB` — трафик, `Hrs` — часы; у позиций без объёма — `0`/`null`).

`404` (отчётов ещё нет):

```json
{
  "error": "No billing report has been collected yet.",
  "hint": "Run the collection (it happens automatically on the cron schedule, and at startup when the database is empty), then retry."
}
```

### `GET /api/reports/{id}`

Сохранённый отчёт по id (первичный ключ; список id — `GET /api/reports`).
Как и `latest`, отдаёт сохранённую строку **байт-в-байт** из SQLite.

`200`, `Content-Type: application/json; charset=utf-8` — тело, как у `latest`.

`404` (отчёта с таким id нет):

```json
{
  "error": "Billing report #999 does not exist.",
  "hint": "List available reports at GET /api/reports (metadata only)."
}
```

### `GET /api/reports?take=N`

Метаданные по сохранённым отчётам (новые первые), **без тел**.
`take` по умолчанию 20, климпится в `[1, 100]`:

```json
[
  { "id": 1, "collectedAtUtc": "2026-08-25T10:25:54.7657994Z", "periodStart": "2026-08-01",
    "periodEnd": "2026-08-25", "serviceNames": "Amazon Lightsail",
    "pageCount": 1, "byteSize": 546 }
]
```

`serviceNames` — CSV-строка ровно так, как хранится в БД (несколько имён — через «,»);
тело отчёта в список не попадает.

## Как это работает

**Период.** `TimePeriod.Start` — 1-е число текущего месяца, `TimePeriod.End` —
день после текущего: по спецификации Cost Explorer `Start` включителен, а
`End` — нет, поэтому `+1 день`, чтобы сегодняшний день вошёл в отчёт.
Даты — `yyyy-MM-dd`, UTC.

**Пагинация.** Ответ AWS может прийти несколькими страницами (`NextPageToken`);
коллектор добирает все и агрегирует все дни и usage type в один месячный
отчёт. В сохранённый документ `nextPageToken` не попадает.

**Отчёт.** `CostExplorerCollector.BuildReport` (публичный статический метод —
контракт закреплён тестами `MonthlyReportTests`) агрегирует все дни и страницы:
`total.cost` — сумма за месяц по всем usage type; `usageTypes` — разбивка
(стоимость + объёмы), сначала самые дорогие. Отчёт — наш собственный документ:
camelCase и числа (`JsonSerializerDefaults.Web`) — ограничений wire-формата
Cost Explorer здесь нет, т.к. POCO SDK не сериализуются. Тело API-ответа ==
`raw_json` из `report_contents` байт-в-байт (тест `ReportsControllerTests`:
`Assert.Equal(reportJson, content.Content)`).

**Отказоустойчивость.** Ошибка сбора (сеть, `AccessDeniedException`, квоты)
логируется, хост не падает, предыдущий отчёт продолжает отдаваться.
`AccessDeniedException` — отдельная подсказка в логе (проверить креды и
политику `ce:GetCostAndUsage`).

## Хранилище

SQLite, файл `App:DatabasePath` (по умолчанию `aws-billing.db` рядом с бинарником).
Схема управляется EF Core миграциями (`src/AwsBilling/Database/Migrations/`) и применяется
на старте хоста — `ReportsRepository.Migrate()`.

Журнал — WAL: его включает сам EF Core Sqlite-провайдер при создании базы, режим
персистентен (хранится в заголовке файла). В WAL читатели не блокируются записью,
поэтому «столкновение» джоба и API-запроса на практике редко; страховка —
`busy_timeout=5000` (`Default Timeout=5` в connection string). Shared cache
не включаем: в connection string задано `Cache=Private` (явно — провайдер
не фиксирует его сам).

### Файлы БД

Рядом с `aws-billing.db` лежат два служебных файла WAL-режима:

| Файл | Назначение |
|---|---|
| `aws-billing.db` | сама база: схема, данные, история миграций |
| `aws-billing.db-wal` | write-ahead log: новые записи сначала сюда, читатели учитывают его |
| `aws-billing.db-shm` | shared-memory индекс кадров WAL — координация нескольких соединений |

`-wal`/`-shm` — штатные спутники, а не мусор: при корректном останове хоста
SQLite чекапит WAL в основной файл и удаляет оба; после `kill` процесса они
остаются и подхватываются при следующем старте. Резервную копию делать либо
при остановленном хосте (хватит `aws-billing.db`), либо всех трёх файлов
вместе, если процесс ещё работает (свежие записи могут быть ещё в WAL).

```sql
-- схема после миграции Initial (для справки)
CREATE TABLE reports (
  id INTEGER PRIMARY KEY AUTOINCREMENT,
  collected_at_utc TEXT NOT NULL,   -- ISO-8601 UTC
  period_start     TEXT NOT NULL,   -- yyyy-MM-dd
  period_end       TEXT NOT NULL,   -- yyyy-MM-dd (включительно, «по текущий день»)
  service_names    TEXT NOT NULL,   -- имена сервисов, разделённые запятой (CSV); запятые в именах не поддерживаются
  page_count       INTEGER NOT NULL,
  byte_size        INTEGER NOT NULL
);

CREATE TABLE report_contents (
  report_id INTEGER NOT NULL PRIMARY KEY
             REFERENCES reports(id) ON DELETE CASCADE,
  raw_json  TEXT NOT NULL           -- сохранённый отчёт (JSON); имя колонки — от прежнего контракта «сырой ответ»
);
```

Метаданные отчёта — в `reports`, JSON-тело — в `report_contents` (один-к-одному
по `report_id`); список метаданных читает только `reports`.
Каждая коллекция — новая строка `reports` и связанная строка `report_contents`
(append-only, одна транзакция); «последний» = `ORDER BY id DESC LIMIT 1`.

## Расписание

Quartz-джоб `billing-collection`, trigger — `.WithCronSchedule(App:Cron)`.
TimeZone в триггере не фиксируется → Quartz использует **местное время** машины
(проверено). `WaitForJobsToComplete = true` — при остановке хоста джоб доделает
текущий цикл. Стартовый цикл (запускается, если в БД ещё нет ни одного отчёта) и
cron-джоб сходятся в
одном пайплайне (`BillingCollectionRunner`), у которого есть
`SemaphoreSlim(1,1)`-гейт: цикл, пришедший во время другого, **пропускается**
(лог), а не ставится в очередь — нет параллельных AWS-вызовов и дубля в истории;
последующие циклы выполняются как обычно.

## Логи

Один провайдер — Console, параметры — в `appsettings.json` (секция `Logging:Console`),
вывод single-line, потоки разделены порогом `LogToStandardErrorThreshold = Error`:

- `Error` и выше → **stderr** (ошибки AWS, `AccessDeniedException`, сбои коллекции);
- `Information`–`Warning` → **stdout** (старт хоста, ход коллекции, 404 по API).

Фильтры уровней — секция `Logging:LogLevel`.

## AWS-креды

Как сервис получает креды:

- Пара ключей в `appsettings.json` → `BasicAWSCredentials` из конфигурации.
  Ключи обязательны: отсутствие или половина пары — сбой на старте
  с сообщением валидатора (дефолтная цепочка SDK не используется).

### Как получить ключи

Сервису нужна **одна** операция Cost Explorer — `ce:GetCostAndUsage`.
Рекомендуемый путь — отдельный IAM-пользователь с минимальными правами.

1. **Создать политику** (IAM → Policies → Create policy → JSON editor):

   ```json
   {
     "Version": "2012-10-17",
     "Statement": [
       {
         "Effect": "Allow",
         "Action": "ce:GetCostAndUsage",
         "Resource": "*"
       }
     ]
   }
   ```

2. **Создать пользователя** (IAM → Users → Add user): тип доступа
   *Programmatic access only* (без AWS CLI / Console), привязать политику из шага 1.

3. **Сгенерировать пару ключей** (вкладка пользователя → Security credentials →
   Create access key). AWS один раз покажет `Secret access key` и предложит
   скачать CSV — сохранить, восстановить его потом нельзя.
   - `Access key ID` (форма `AKIA…`) → `App:AccessKeyId`;
   - `Secret access key` → `App:SecretAccessKey`.

4. Вписать оба значения в `src/AwsBilling/appsettings.json` (секция `App`)
   и перезапустить сервис.

⚠️ Ключ — полный доступ владельца учётной записи, если создан у root-пользователя:
создавайте отдельного IAM-пользователя (шаги выше), а не ключ от основной учётки.

## Разработка

```bash
dotnet build -c Release   # 0 предупреждений: TreatWarningsAsErrors = true
dotnet test               # 41 тест (валидаторы, репозиторий, контроллер, отчёт, гейт, джоба, остановка); AWS не нужен
```

Структура:

```
AwsBilling.slnx                      # решение (XML-формат .NET 10)
src/AwsBilling/
  Program.cs                         # композиционный корень
  appsettings.json                   # ЕДИНСТВЕННЫЙ файл конфигурации (содержит секреты)
  Configuration/ AppOptions (единственный класс настроек — секция "App"),
               AppOptionsValidator (OptionsValidators.cs)
  Collection/ CostExplorerCollector (запросы к IAmazonCostExplorer,
              пагинация + BuildReport месячного отчёта), BillingCollectionRunner
              (пайплайн + гейт), интерфейсы границ
  BackgroundJobs/ BillingCollectionJob (Quartz IJob), CollectOnStartupService
  Database/  ReportsRepository (EF Core, append-only), ReportsDbContext,
              ReportsDbContextFactory (design-time фабрика для dotnet-ef),
              Report, ReportContent (сущности; Report — модель API списка), Migrations/
  Controllers/ ReportsController
  tests/AwsBilling.Tests/              # xunit, 41 тест
```

## Развёртывание на Alpine Linux (OpenRC)

### Публикация

```sh
dotnet publish src/AwsBilling -c Release -r linux-musl-x64 --self-contained -p:PublishSingleFile=true -o out
```

### Необходимые пакеты ICU

Для корректной работы .NET 10 на Alpine установите библиотеки локализации:

```sh
apk add libstdc++ libgcc icu-libs
```

Без `icu-libs` сервис может завершиться с ошибкой при обработке Unicode.

### Служба OpenRC

Скопируйте содержимое `out` в `/app` и создайте `/etc/init.d/aws-billing`:

```sh
#!/sbin/openrc-run

description="AWS Billing Collector"
command="/app/AwsBilling"
pidfile="/var/run/aws-billing.pid"
directory="/app"
supervisor=supervise-daemon
output_logger="logger -p user.info -t aws-billing"
error_logger="logger -p user.error -t aws-billing"
respawn_delay=2
respawn_max=10
respawn_period=60

depend() {
    need net
}
```

Включите и запустите службу:

```sh
chmod +x /etc/init.d/aws-billing
rc-update add aws-billing default
service aws-billing start
```

OpenRC обычно запускает службу от root, поэтому она может привязаться к порту 80.
Если службу запускать от непривилегированного пользователя, назначьте бинарнику
возможность `CAP_NET_BIND_SERVICE` или смените порт в `Kestrel:Endpoints:Http:Url`.

## Ограничения (осознанные)

- **Cost Explorer имеет задержку 24–48 ч** для свежих начислений: последние дни
  месяца в отчёте могут быть неполными — особенность источника, а не баг.
- Нет retention-политики: история копится без удаления (добавляется отдельным
  решением, если понадобится).
- Нет retry/backoff на транзиентные ошибки AWS: ошибка логируется, цикл пропускается,
  следующий cron-срабатывание повторит попытку.
- Нет аутентификации/HTTPS на API — предполагается локальная/внутренняя сеть.

## Безопасность

`appsettings.json` содержит секреты (AWS-ключи). Если каталог станет
git-репозиторием — файл (или сами секреты) исключить из коммитов; следить,
чтобы файл не попадал в бэкапы и артефакты сборки/публикации.
