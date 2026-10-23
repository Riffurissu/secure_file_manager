# FileManager

HTTP-сервис файлового менеджера на ASP.NET Core (.NET 8) с SQLite. Каждый пользователь работает только в своей папке `storage/<username>/`. Все действия записываются в журнал.

Приложение запускается локально или в Docker-контейнере. CI/CD — пайплайн для GitHub Actions (`.github/workflows/ci.yml`), запускаемый в том числе локально через `act` (раздел «CI/CD»).

## Запуск в Docker

Требуется Docker Desktop (Windows) или Docker Engine (Linux).

Из папки `FileManager`:

```text
docker build -t filemanager:local .
docker run --rm -p 8080:8080 -v fm-data:/data filemanager:local
```

- `-p 8080:8080` открывает API на `http://localhost:8080`.
- `-v fm-data:/data` сохраняет базу и файлы между перезапусками контейнера. Том создаётся автоматически.
- Внутри контейнера приложение работает от пользователя `app`, не root.

Остановка: `Ctrl+C` (контейнер создан с `--rm`, после остановки удаляется; данные остаются в томе `fm-data`).

Удаление данных:

```text
docker volume rm fm-data
```

## Запуск без Docker

Требуется .NET 8 SDK.

```text
cd FileManager.Api
dotnet run
```

По умолчанию данные хранятся в `storage/` и `filemanager.db` рядом с приложением. Порт задаётся переменной `ASPNETCORE_URLS`, например `http://localhost:8080`.

## Настройки

| Переменная | По умолчанию (локально) | В контейнере |
|---|---|---|
| `Storage__Root` | `storage` | `/data/storage` |
| `Database__Path` | `filemanager.db` | `/data/filemanager.db` |
| `ASPNETCORE_URLS` | `http://localhost:5000` | `http://+:8080` |

## Порядок тестирования

Примеры ниже расположены в порядке, в котором их удобно проверять вручную:

1. Проверка работы сервиса (`/health`).
2. Регистрация, вход, проверка токена.
3. Диски (`/api/system/drives`).
4. Корневая директория пользователя: листинг.
5. Файл в корневой директории: запись, проверка наличия в листинге, чтение, удаление.
6. В той же корневой директории: создание и чтение JSON и XML.
7. Каталоги: создание и удаление.
8. Архивы ZIP, журнал операций.

Ниже `BASE=http://localhost:8080`. Для Windows PowerShell используйте `curl.exe`, а не `curl`, иначе команда попадёт в `Invoke-WebRequest`.

### 1. Проверка работы

```text
curl http://localhost:8080/health
```

Ответ: `{"status":"ok"}`.

### 2. Регистрация, вход, токен

```text
curl -X POST http://localhost:8080/api/auth/register -H "Content-Type: application/json" -d '{"username":"alice","password":"Passw0rdlong"}'
```

```text
curl -X POST http://localhost:8080/api/auth/login -H "Content-Type: application/json" -d '{"username":"alice","password":"Passw0rdlong"}'
```

Ответ содержит `token`. Токен действует один час. Дальше он передаётся в заголовке `Authorization: Bearer <token>`.

Сохранить токен в переменную (bash):

```text
TOKEN=$(curl -s -X POST http://localhost:8080/api/auth/login -H "Content-Type: application/json" -d '{"username":"alice","password":"Passw0rdlong"}' | sed -E 's/.*"token":"([^"]+)".*/\1/')
```

Проверка токена:

```text
curl http://localhost:8080/api/auth/me -H "Authorization: Bearer $TOKEN"
```

### 3. Диски

Диски того окружения, где запущено приложение. В контейнере это файловые системы контейнера, не диски хоста:

```text
curl http://localhost:8080/api/system/drives -H "Authorization: Bearer $TOKEN"
```

### 4. Корневая директория пользователя

Листинг домашней папки (параметр `path` не указывается):

```text
curl http://localhost:8080/api/files -H "Authorization: Bearer $TOKEN"
```

Сразу после регистрации список пуст: `[]`.

### 5. Файл в корневой директории

Записать файл (до 10 МБ):

```text
curl -i -X PUT "http://localhost:8080/api/files/content?path=hello.txt" -H "Authorization: Bearer $TOKEN" --data-binary "Hello, world"
```

Проверить наличие файла: в листинге корневой папки должен появиться `hello.txt`:

```text
curl http://localhost:8080/api/files -H "Authorization: Bearer $TOKEN"
```

Прочитать файл:

```text
curl "http://localhost:8080/api/files/content?path=hello.txt" -H "Authorization: Bearer $TOKEN"
```

Удалить файл (мягкое удаление, запись в журнале сохраняется):

```text
curl -X DELETE "http://localhost:8080/api/files?path=hello.txt" -H "Authorization: Bearer $TOKEN"
```

Повторное чтение после удаления возвращает `404 Not Found`:

```text
curl -i "http://localhost:8080/api/files/content?path=hello.txt" -H "Authorization: Bearer $TOKEN"
```

Попытка выйти за пределы домашней папки отклоняется (`400 Bad Request`):

```text
curl -i "http://localhost:8080/api/files/content?path=../bob/secret.txt" -H "Authorization: Bearer $TOKEN"
```

### 6. JSON и XML в корневой директории

Проверить и нормализовать JSON без сохранения:

```text
curl -X POST "http://localhost:8080/api/structured/validate?format=json" -H "Authorization: Bearer $TOKEN" -H "Content-Type: application/json" -d '{"name":"test","items":[1,2]}'
```

Создать JSON-файл в корневой папке:

```text
curl -X POST "http://localhost:8080/api/structured/save?path=config.json&format=json" -H "Authorization: Bearer $TOKEN" -H "Content-Type: application/json" -d '{"name":"demo","items":[1,2]}'
```

Прочитать JSON-файл:

```text
curl "http://localhost:8080/api/structured/read?path=config.json&format=json" -H "Authorization: Bearer $TOKEN"
```

Создать XML-файл в той же папке (нормализация, DTD запрещены):

```text
curl -X POST "http://localhost:8080/api/structured/save?path=config.xml&format=xml" -H "Authorization: Bearer $TOKEN" -H "Content-Type: application/xml" -d "<config><name>demo</name></config>"
```

Прочитать XML-файл:

```text
curl "http://localhost:8080/api/structured/read?path=config.xml&format=xml" -H "Authorization: Bearer $TOKEN"
```

Комментарии, trailing comma и повторяющиеся поля в JSON отклоняются (`400`):

```text
curl -i -X POST "http://localhost:8080/api/structured/validate?format=json" -H "Authorization: Bearer $TOKEN" -d '{"a":1,}'
```

XML с внешней сущностью (XXE) отклоняется (`400`):

```text
curl -i -X POST "http://localhost:8080/api/structured/validate?format=xml" -H "Authorization: Bearer $TOKEN" -d '<!DOCTYPE r [<!ENTITY x SYSTEM "file:///etc/passwd">]><r>&x;</r>'
```

### 7. Каталоги: создание и удаление

Создать каталог:

```text
curl -X POST "http://localhost:8080/api/folders?path=reports/2026" -H "Authorization: Bearer $TOKEN"
```

Создать каталог в корне и проверить его в листинге корневой папки:

```text
curl -X POST "http://localhost:8080/api/folders?path=projects" -H "Authorization: Bearer $TOKEN"
```

```text
curl http://localhost:8080/api/files -H "Authorization: Bearer $TOKEN"
```

Удалить пустой каталог (`recursive=false`, по умолчанию):

```text
curl -X DELETE "http://localhost:8080/api/folders?path=projects" -H "Authorization: Bearer $TOKEN"
```

Непустой каталог без `recursive=true` не удаляется (`409 Conflict`):

```text
curl -i -X DELETE "http://localhost:8080/api/folders?path=reports" -H "Authorization: Bearer $TOKEN"
```

Удалить каталог вместе с содержимым (файлы внутри помечаются удалёнными в журнале):

```text
curl -X DELETE "http://localhost:8080/api/folders?path=reports&recursive=true" -H "Authorization: Bearer $TOKEN"
```

Удалить домашнюю папку нельзя: пустой `path` отклоняется (`400 Bad Request`).

### 8. ZIP-архивы

Упаковать каталог в архив:

```text
curl -X POST "http://localhost:8080/api/archive/create?source=docs&archive=backup/docs.zip" -H "Authorization: Bearer $TOKEN"
```

Упаковать всю домашнюю папку (пустой `source`):

```text
curl -X POST "http://localhost:8080/api/archive/create?source=&archive=all.zip" -H "Authorization: Bearer $TOKEN"
```

Распаковать архив в каталог внутри домашней папки:

```text
curl -X POST "http://localhost:8080/api/archive/extract?archive=backup/docs.zip&destination=restored" -H "Authorization: Bearer $TOKEN"
```

Распаковка атомарна: если какая-либо запись не может быть извлечена (например, конфликт имени), уже записанные файлы откатываются, журнал не меняется.

### 9. Журнал операций

Журнал текущего пользователя:

```text
curl http://localhost:8080/api/operations -H "Authorization: Bearer $TOKEN"
```

## Лимиты

| Что | Лимит |
|---|---|
| Размер одного файла (запись, чтение, JSON/XML, архив) | 10 МБ |
| Суммарный распакованный размер при создании или распаковке архива | 50 МБ |
| Записей в архиве | 1000 |
| Глубина вложенности каталогов и JSON/XML | 32 |
| Узлов в JSON/XML | 50 000 |
| Коэффициент сжатия записи больше 1 МБ | не более 100 |
| Попыток входа с одного IP | 10 в минуту |
| Неудачных входов до блокировки аккаунта | 5 (блокировка 1 мин, затем 5 мин) |

## Тесты

```text
cd FileManager
dotnet test FileManager.Tests --logger "trx;LogFileName=test-results.trx" --collect:"XPlat Code Coverage"
```

Отчёты: `FileManager.Tests/TestResults/`.

## CI/CD

Пайплайн — `.github/workflows/ci.yml` (GitHub Actions), аналог `.gitlab-ci.yml`. Этапы выполняются последовательно, как стадии GitLab:

| Этап | Джоба | Что делает |
|---|---|---|
| Build | `build` | `dotnet restore/build/publish` в контейнере `mcr.microsoft.com/dotnet/sdk:8.0`, результат — артефакт `publish/` |
| Test | `test` | xUnit-тесты; отчёты JUnit (`test-results.xml`) и Cobertura (`coverage.cobertura.xml`) |
| SAST | `sast` | Trivy: уязвимости зависимостей, секреты, ошибки конфигурации Dockerfile. При находках HIGH/CRITICAL джоба завершается с ошибкой |
| Local Deploy | `local_deploy` | сборка образа из Dockerfile, запуск контейнера с пробросом `8080`, smoke-проверка `/health`, остановка контейнера |

На GitHub пайплайн запускается на push и PR: JUnit-отчёт публикуется как check run, SARIF-отчёт Trivy — в Code Scanning. Эти шаги помечены `if: !env.ACT` — при локальном запуске через act они пропускаются.

### Локальный раннер (act)

Требуется Docker (Docker Desktop на Windows/macOS, Docker Engine на Linux).

1. Установить act:

   ```text
   winget install nektos.act
   ```

   Linux: `curl -s https://raw.githubusercontent.com/nektos/act/master/install.sh | sudo bash`, macOS: `brew install act`.

2. Запустить пайплайн из корня репозитория:

   ```text
   act push
   ```

   Одна джоба: `act push -j local_deploy`. Секреты, если понадобятся: `act push -s NAME=value` (сейчас передавать ничего не требуется).

3. Настройки act лежат в `.actrc`:
   - образ для джоб с `runs-on: ubuntu-latest` — `catthehacker/ubuntu:act-latest` (есть Docker CLI, node, git, curl);
   - проброс Docker-сокета хоста в контейнеры джоб — нужен стадии `local_deploy`, где вызываются `docker build` / `docker run`;
   - `--container-architecture linux/amd64` — для хостов с ARM.

## Структура

```text
FileManager/
├── .github/workflows/ci.yml CI/CD-пайплайн (GitHub Actions)
├── .actrc                   настройки локального раннера act
├── Dockerfile
├── .dockerignore
├── FileManager.sln
├── FileManager.Api/       ASP.NET Core API
│   ├── Auth/              вход, токены, блокировки
│   ├── Archive/           ZIP
│   ├── Data/              SQLite и репозитории
│   ├── Files/             пути, файлы, каталоги, лимиты
│   ├── Security/          хеширование паролей
│   ├── Structured/        JSON и XML
│   └── System/            диски
└── FileManager.Tests/     xUnit-тесты
```
