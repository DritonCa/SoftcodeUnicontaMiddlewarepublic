# Build
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src
COPY SoftcodeUnicontaMiddleware.csproj ./
RUN dotnet restore SoftcodeUnicontaMiddleware.csproj
COPY . .
RUN dotnet publish SoftcodeUnicontaMiddleware.csproj -c Release -o /out --no-restore

# Run as the image's non-root "app" user. State lives in three volumes:
#   /data                     SQLite database (clients, admin users)
#   /app/dataprotection-keys  keys for cookies and encrypted secrets (losing them logs everyone out)
#   /app/logs                 order log
FROM mcr.microsoft.com/dotnet/aspnet:8.0
WORKDIR /app
COPY --from=build /out ./
RUN mkdir -p /data /app/dataprotection-keys /app/logs && chown -R app:app /data /app/dataprotection-keys /app/logs
USER app
ENV ASPNETCORE_URLS=http://+:8080 \
    ASPNETCORE_ENVIRONMENT=Production \
    ConnectionStrings__AppDb="Data Source=/data/softcode_api.db"
EXPOSE 8080
ENTRYPOINT ["dotnet", "SoftcodeUnicontaMiddleware.dll"]
