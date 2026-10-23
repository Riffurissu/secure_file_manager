# Этап сборки: SDK, restore отдельным слоем для кэширования зависимостей
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src

COPY FileManager.Api/FileManager.Api.csproj FileManager.Api/
RUN dotnet restore FileManager.Api/FileManager.Api.csproj

COPY FileManager.Api/ FileManager.Api/
RUN dotnet publish FileManager.Api/FileManager.Api.csproj -c Release -o /out --no-restore

# Этап запуска: только runtime и опубликованные файлы
FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS runtime
WORKDIR /app

ENV ASPNETCORE_URLS=http://+:8080 \
    Storage__Root=/data/storage \
    Database__Path=/data/filemanager.db

# Каталог данных принадлежит непривилегированному пользователю образа (app, UID 1654)
RUN mkdir -p /data && chown -R app:app /data

COPY --from=build /out .

EXPOSE 8080
VOLUME ["/data"]

USER app
ENTRYPOINT ["dotnet", "FileManager.Api.dll"]
