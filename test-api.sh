#!/usr/bin/env bash
# Ручная проверка FileManager API. Примеры из README, по порядку.
# Перед каждым запросом выводится команда, после него - ответ сервера.
# Использование: BASE=http://localhost:8080 ./test-api.sh

BASE="${BASE:-http://localhost:8080}"
LAST_OUT=""
TOKEN=""

step() {
  echo
  echo "=================================================="
  echo "=== $1"
  echo "=================================================="
}

# format <текст> - если текст это JSON, выводит его через jq, иначе как есть.
format() {
  if command -v jq >/dev/null 2>&1 && printf '%s' "$1" | jq -e . >/dev/null 2>&1; then
    printf '%s' "$1" | jq .
  else
    printf '%s\n' "$1"
  fi
}

# pretty <сырой ответ curl> - для ответов с заголовками (curl -i) выводит
# статус и заголовки как есть, а тело форматирует через jq.
pretty() {
  if [[ "$1" == HTTP/* ]]; then
    local headers body
    headers=$(printf '%s\n' "$1" | sed -n '1,/^\r\?$/p')
    body=$(printf '%s\n' "$1" | sed '1,/^\r\?$/d')
    printf '%s\n' "$headers"
    [ -n "$body" ] && format "$body"
  else
    format "$1"
  fi
}

# run "<команда>" - печатает команду, выполняет её и печатает ответ (с jq, если JSON).
# В выводе $BASE и $TOKEN показаны как переменные, в выполнении подставляются значения.
run() {
  echo
  echo "\$ $1"
  LAST_OUT=$(eval "$1" 2>&1)
  echo "--- ответ ---"
  pretty "$LAST_OUT"
}

# 1. Проверка работы
step "1. Проверка работы"
run "curl -s \$BASE/health"

# 2. Регистрация, вход, токен
step "2. Регистрация"
run "curl -s -X POST \"\$BASE/api/auth/register\" -H \"Content-Type: application/json\" -d '{\"username\":\"alice\",\"password\":\"Passw0rdlong\"}'"

step "2. Вход и получение токена"
run "curl -s -X POST \"\$BASE/api/auth/login\" -H \"Content-Type: application/json\" -d '{\"username\":\"alice\",\"password\":\"Passw0rdlong\"}'"
TOKEN=$(echo "$LAST_OUT" | sed -E 's/.*"token":"([^"]+)".*/\1/')
if [ -z "$TOKEN" ] || [ "$TOKEN" = "$LAST_OUT" ]; then
  echo "Не удалось получить токен" >&2
  exit 1
fi
echo
echo "Токен сохранён в \$TOKEN"

step "2. Проверка токена"
run "curl -s \$BASE/api/auth/me -H \"Authorization: Bearer \$TOKEN\""

# 3. Диски
step "3. Диски"
run "curl -s \$BASE/api/system/drives -H \"Authorization: Bearer \$TOKEN\""

# 4. Корневая директория пользователя
step "4. Листинг корневой директории"
run "curl -s \$BASE/api/files -H \"Authorization: Bearer \$TOKEN\""

# 5. Файл в корневой директории
step "5. Запись файла hello.txt"
run "curl -i -s -X PUT \"\$BASE/api/files/content?path=hello.txt\" -H \"Authorization: Bearer \$TOKEN\" --data-binary \"Hello, world\""

step "5. Проверка наличия hello.txt в листинге"
run "curl -s \$BASE/api/files -H \"Authorization: Bearer \$TOKEN\""

step "5. Чтение hello.txt"
run "curl -s \"\$BASE/api/files/content?path=hello.txt\" -H \"Authorization: Bearer \$TOKEN\""

step "5. Удаление hello.txt"
run "curl -s -X DELETE \"\$BASE/api/files?path=hello.txt\" -H \"Authorization: Bearer \$TOKEN\""

step "5. Чтение после удаления (ожидается 404)"
run "curl -i -s \"\$BASE/api/files/content?path=hello.txt\" -H \"Authorization: Bearer \$TOKEN\""

step "5. Выход за пределы домашней папки (ожидается 400)"
run "curl -i -s \"\$BASE/api/files/content?path=../bob/secret.txt\" -H \"Authorization: Bearer \$TOKEN\""

# 6. JSON и XML в корневой директории
step "6. Валидация JSON без сохранения"
run "curl -s -X POST \"\$BASE/api/structured/validate?format=json\" -H \"Authorization: Bearer \$TOKEN\" -H \"Content-Type: application/json\" -d '{\"name\":\"test\",\"items\":[1,2]}'"

step "6. Сохранение config.json"
run "curl -s -X POST \"\$BASE/api/structured/save?path=config.json&format=json\" -H \"Authorization: Bearer \$TOKEN\" -H \"Content-Type: application/json\" -d '{\"name\":\"demo\",\"items\":[1,2]}'"

step "6. Чтение config.json"
run "curl -s \"\$BASE/api/structured/read?path=config.json&format=json\" -H \"Authorization: Bearer \$TOKEN\""

step "6. Сохранение config.xml"
run "curl -s -X POST \"\$BASE/api/structured/save?path=config.xml&format=xml\" -H \"Authorization: Bearer \$TOKEN\" -H \"Content-Type: application/xml\" -d '<config><name>demo</name></config>'"

step "6. Чтение config.xml"
run "curl -s \"\$BASE/api/structured/read?path=config.xml&format=xml\" -H \"Authorization: Bearer \$TOKEN\""

step "6. JSON с trailing comma (ожидается 400)"
run "curl -i -s -X POST \"\$BASE/api/structured/validate?format=json\" -H \"Authorization: Bearer \$TOKEN\" -d '{\"a\":1,}'"

step "6. XML с внешней сущностью XXE (ожидается 400)"
run "curl -i -s -X POST \"\$BASE/api/structured/validate?format=xml\" -H \"Authorization: Bearer \$TOKEN\" -d '<!DOCTYPE r [<!ENTITY x SYSTEM \"file:///etc/passwd\">]><r>&x;</r>'"

# 7. Каталоги
step "7. Создание каталога reports/2026"
run "curl -s -X POST \"\$BASE/api/folders?path=reports/2026\" -H \"Authorization: Bearer \$TOKEN\""

step "7. Создание каталога projects"
run "curl -s -X POST \"\$BASE/api/folders?path=projects\" -H \"Authorization: Bearer \$TOKEN\""

step "7. Листинг корневой директории"
run "curl -s \$BASE/api/files -H \"Authorization: Bearer \$TOKEN\""

step "7. Удаление пустого каталога projects"
run "curl -s -X DELETE \"\$BASE/api/folders?path=projects\" -H \"Authorization: Bearer \$TOKEN\""

step "7. Удаление непустого reports без recursive (ожидается 409)"
run "curl -i -s -X DELETE \"\$BASE/api/folders?path=reports\" -H \"Authorization: Bearer \$TOKEN\""

step "7. Рекурсивное удаление reports"
run "curl -s -X DELETE \"\$BASE/api/folders?path=reports&recursive=true\" -H \"Authorization: Bearer \$TOKEN\""

# 8. ZIP-архивы
step "8. Создание каталога backups"
run "curl -s -X POST \"\$BASE/api/folders?path=backups\" -H \"Authorization: Bearer \$TOKEN\""

step "8. Архивация всей домашней папки"
run "curl -s -X POST \"\$BASE/api/archive/create?source=&archive=backups/all.zip\" -H \"Authorization: Bearer \$TOKEN\""

step "8. Листинг корневой директории"
run "curl -s \$BASE/api/files -H \"Authorization: Bearer \$TOKEN\""

step "8. Распаковка архива в restored"
run "curl -s -X POST \"\$BASE/api/archive/extract?archive=backups/all.zip&destination=restored\" -H \"Authorization: Bearer \$TOKEN\""

step "8. Листинг корневой директории"
run "curl -s \$BASE/api/files -H \"Authorization: Bearer \$TOKEN\""

# 9. Журнал операций
step "9. Журнал операций"
run "curl -s \$BASE/api/operations -H \"Authorization: Bearer \$TOKEN\""

echo
echo "Готово."
