FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY Directory.Build.props ./
COPY src/ ./src/
COPY sql/ ./sql/
RUN dotnet publish src/Dhole.Dynamics.Tools/Dhole.Dynamics.Tools.csproj -c Release -o /out /p:UseAppHost=false
FROM mcr.microsoft.com/dotnet/runtime:10.0
WORKDIR /app
COPY --from=build /out .
USER $APP_UID
ENTRYPOINT ["dotnet", "Dhole.Dynamics.Tools.dll"]
