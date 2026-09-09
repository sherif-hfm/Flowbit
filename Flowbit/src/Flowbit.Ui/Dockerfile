# Build from the repository root: docker build -f Flowbit/src/Flowbit.Ui/Dockerfile .
FROM mcr.microsoft.com/dotnet/sdk:10.0-noble AS build
WORKDIR /src
# Razor files must be present during restore so the SDK includes Blazor's framework assets.
COPY Flowbit/src/Flowbit.Shared/ Flowbit/src/Flowbit.Shared/
COPY Flowbit/src/Flowbit.Ui/ Flowbit/src/Flowbit.Ui/
RUN dotnet restore Flowbit/src/Flowbit.Ui/Flowbit.Ui.csproj -p:Configuration=Release
RUN dotnet publish Flowbit/src/Flowbit.Ui/Flowbit.Ui.csproj -c Release --no-restore -o /app/publish /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:10.0-noble AS final
WORKDIR /app
RUN apt-get update \
    && apt-get install -y --no-install-recommends curl \
    && rm -rf /var/lib/apt/lists/* \
    && mkdir -p /app/Logs \
    && chown app:app /app/Logs
COPY --from=build /app/publish .
ENV ASPNETCORE_HTTP_PORTS=8080
EXPOSE 8080
USER $APP_UID
ENTRYPOINT ["dotnet", "Flowbit.Ui.dll"]
