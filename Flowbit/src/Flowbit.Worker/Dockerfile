# Build from the repository root: docker build -f Flowbit/src/Flowbit.Worker/Dockerfile .
FROM mcr.microsoft.com/dotnet/sdk:10.0-noble AS build
WORKDIR /src
COPY Flowbit/src/Flowbit.Shared/Flowbit.Shared.csproj Flowbit/src/Flowbit.Shared/
COPY Flowbit/src/Flowbit.Service/Flowbit.Service.csproj Flowbit/src/Flowbit.Service/
COPY Flowbit/src/Flowbit.Infrastructure/Flowbit.Infrastructure.csproj Flowbit/src/Flowbit.Infrastructure/
COPY Flowbit/src/Flowbit.Worker/Flowbit.Worker.csproj Flowbit/src/Flowbit.Worker/
RUN dotnet restore Flowbit/src/Flowbit.Worker/Flowbit.Worker.csproj
COPY Flowbit/src/Flowbit.Shared/ Flowbit/src/Flowbit.Shared/
COPY Flowbit/src/Flowbit.Service/ Flowbit/src/Flowbit.Service/
COPY Flowbit/src/Flowbit.Infrastructure/ Flowbit/src/Flowbit.Infrastructure/
COPY Flowbit/src/Flowbit.Worker/ Flowbit/src/Flowbit.Worker/
RUN dotnet publish Flowbit/src/Flowbit.Worker/Flowbit.Worker.csproj -c Release --no-restore -o /app/publish /p:UseAppHost=false

# The Worker also hosts ASP.NET Core health and metrics endpoints.
FROM mcr.microsoft.com/dotnet/aspnet:10.0-noble AS final
WORKDIR /app
RUN apt-get update \
    && apt-get install -y --no-install-recommends curl \
    && rm -rf /var/lib/apt/lists/*
COPY --from=build /app/publish .
ENV FlowbitWorker__HealthListenUrl=http://0.0.0.0:8081
EXPOSE 8081
USER $APP_UID
ENTRYPOINT ["dotnet", "Flowbit.Worker.dll"]
