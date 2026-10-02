# Infisical configuration for ASP.NET Core

- `MonixOne.Infisical.Configuration` — переиспользуемая библиотека для .NET 9/10;

Библиотека один раз загружает секреты из Infisical во время старта приложения и:

1. добавляет их в `IConfiguration`;
2. копирует их в `EnvironmentVariableTarget.Process`;
3. выполняет обязательное периодическое обновление с повторным Universal Auth login.

Библиотека читает настройки из секции `Infisical` в `IConfiguration`; если значение в секции отсутствует, используется прежний fallback к переменной окружения `INFISICAL_*`. Делегат `configure` в `AddInfisical` применяется последним и может переопределить оба источника. Копирование полученных секретов выполняется только в окружение текущего процесса приложения.

```csharp
builder.Services.AddInfisical(builder.Configuration);
builder.Services.Configure<DemoOptions>(builder.Configuration.GetSection("Demo"));

// Для получения обновлений используйте IOptionsMonitor.
public sealed class SomeService(IOptionsMonitor<DemoOptions> options)
{
    public string? ApiUrl => options.CurrentValue.ApiUrl;
}
```

`AddInfisical` нужно вызвать до `Configure<T>`, `BindConfiguration` и других регистраций, которые читают секретные настройки.

## appsettings.json

Помимо переменных окружения можно задать настройки в `appsettings.json`:

```json
{
  "Infisical": {
    "ClientId": "replace-with-machine-identity-client-id",
    "ClientSecret": "replace-with-machine-identity-client-secret",
    "ProjectId": "replace-with-project-id",
    "EnvironmentSlug": "dev",
    "SecretPath": "/",
    "RefreshIntervalSeconds": 3600,
    "RefreshTimeout": "00:00:30",
    "Url": "http://infisical01.infra.home.arpa:8888",
    "Recursive": false
  }
}
```

`EnvironmentSlug` остаётся обязательным параметром Infisical API. Не добавляйте рабочий `ClientSecret` в репозиторий: для production предпочтительнее передать его через secret store хоста или переменную окружения.

Чтобы полностью отключить библиотеку для конкретного запуска, передайте `Enabled = false` при подключении:

```csharp
builder.Services.AddInfisical(builder.Configuration, options => options.Enabled = false);
```

В этом режиме библиотека не читает переменные `INFISICAL_*`, не валидирует credentials, не обращается к Infisical, не добавляет provider в `IConfiguration` и не регистрирует background refresh.

## Доступ Infisical

В Infisical создайте Machine Identity с Universal Auth, создайте для неё Client Secret и добавьте identity в проект с ролью `read`. `EnvironmentSlug` — точный slug окружения из `Project Settings → Environments` (`dev`, `staging`, `prod` или другой slug проекта).

Скопируйте шаблон:

```bash
cp example.env .env
```

Обязательные значения:

```dotenv
INFISICAL_CLIENT_ID=<Machine Identity Client ID>
INFISICAL_CLIENT_SECRET=<Machine Identity Client Secret>
INFISICAL_PROJECT_ID=<Project ID>
INFISICAL_ENVIRONMENT=dev
INFISICAL_SECRET_PATH=/
INFISICAL_REFRESH_INTERVAL_SECONDS=3600
```

`INFISICAL_REFRESH_INTERVAL_SECONDS` — необязательный параметр: положительное целое количество секунд. По умолчанию используется `3600` — один час. Явное значение в секции `Infisical` имеет приоритет над этой переменной, а значение из делегата `configure` — над обоими источниками. Это относится к обеим формам интервала: `RefreshInterval` и `RefreshIntervalSeconds`.

`INFISICAL_URL` нужен для self-hosted Infisical; для Cloud его можно не указывать. До вызова `AddInfisical` эти переменные должны уже находиться в process environment.

## Обновление

Access Token является короткоживущим. `AddInfisical` не сохраняет его на диске: при каждом `RefreshAsync` выполняется login по постоянным `CLIENT_ID` и `CLIENT_SECRET`.

При включённой библиотеке background refresh регистрируется всегда. По умолчанию он выполняется каждый час; первая фоновая попытка происходит через один интервал после запуска hosted service. Для другого интервала задайте настройку секции `Infisical`, делегат или переменную окружения хоста:

```dotenv
INFISICAL_REFRESH_INTERVAL_SECONDS=7200
```

Каждый refresh получает новый Access Token через Universal Auth. Поэтому даже редко используемый сервис не зависит от токена, который мог истечь в памяти.

После загрузки и проверки полного ответа provider заменяет свои значения и уведомляет подписчиков через reload token. `IOptionsMonitor<T>` увидит новые значения, `IOptionsSnapshot<T>` — в новом scope, а уже созданный `IOptions<T>` остаётся снимком, как и в стандартной модели ASP.NET Core. Объекты, созданные из конфигурации при старте приложения, нужно обновлять отдельно, если они поддерживают смену настроек во время работы.

Если Infisical вернул ошибку, некорректный ответ или пустой список секретов, обновление считается неуспешным: библиотека сохраняет последнее успешно загруженное значение конфигурации, записывает ошибку в лог и повторит попытку на следующем интервале. Ошибка первоначальной загрузки прерывает запуск приложения.

Весь refresh, включая login, получение секретов, чтение ответа и retries, ограничен `RefreshTimeout` (по умолчанию 30 секунд). Для временных ошибок чтения (HTTP 408, 429, 5xx и сетевые ошибки) выполняются до трёх повторов с задержками 2, 4 и 8 секунд в пределах этого таймаута. Ошибки credentials и другие постоянные HTTP-ошибки не повторяются. Остановка приложения отменяет HTTP-запросы и задержки; отменённое обновление не публикует новые значения.

При удалении секрета из непустого ответа его ключ исчезает из provider. Если библиотека копировала его в process environment, восстанавливается исходное значение переменной (либо переменная удаляется, если её раньше не было). Переменные, изменённые другим кодом после последней записи provider, при удалении секрета сохраняются. Пустой ответ намеренно не очищает всю конфигурацию и environment: он считается ошибкой.
