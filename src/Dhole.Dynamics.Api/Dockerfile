FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY Directory.Build.props DholeDynamicsConnectorService.slnx ./
COPY src/ ./src/
COPY sql/ ./sql/
COPY legacy/ ./legacy/
RUN dotnet restore src/Dhole.Dynamics.Api/Dhole.Dynamics.Api.csproj
RUN dotnet publish src/Dhole.Dynamics.Api/Dhole.Dynamics.Api.csproj -c Release --no-restore -o /out /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
COPY --from=build /out .
ENV ASPNETCORE_URLS=http://+:8080
EXPOSE 8080
USER $APP_UID
ENTRYPOINT ["dotnet","Dhole.Dynamics.Api.dll"]
