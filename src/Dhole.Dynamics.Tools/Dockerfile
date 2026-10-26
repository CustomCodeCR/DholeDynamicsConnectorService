FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY Directory.Build.props ./
COPY src/ ./src/
COPY sql/ ./sql/
RUN dotnet publish src/Dhole.Dynamics.Tools/Dhole.Dynamics.Tools.csproj -c Release -o /out /p:UseAppHost=false \
    && set -eu; for migration in /src/sql/[0-9][0-9][0-9]_*.sql; do \
         file="$(basename "$migration")"; \
         if [ ! -f "/out/migrations/$file" ]; then \
           echo "Missing published SQL migration: $file" >&2; exit 1; \
         fi; \
       done
FROM mcr.microsoft.com/dotnet/runtime:10.0
WORKDIR /app
COPY --from=build /out .
USER $APP_UID
ENTRYPOINT ["dotnet", "Dhole.Dynamics.Tools.dll"]
