# Build from the repository root: docker build -f Flowbit/src/Flowbit.Api/Dockerfile .
FROM mcr.microsoft.com/dotnet/sdk:10.0-noble AS build
WORKDIR /src
COPY Flowbit/src/Flowbit.Shared/Flowbit.Shared.csproj Flowbit/src/Flowbit.Shared/
COPY Flowbit/src/Flowbit.Service/Flowbit.Service.csproj Flowbit/src/Flowbit.Service/
COPY Flowbit/src/Flowbit.Infrastructure/Flowbit.Infrastructure.csproj Flowbit/src/Flowbit.Infrastructure/
COPY Flowbit/src/Flowbit.Api/Flowbit.Api.csproj Flowbit/src/Flowbit.Api/
RUN dotnet restore Flowbit/src/Flowbit.Api/Flowbit.Api.csproj
COPY Flowbit/src/Flowbit.Shared/ Flowbit/src/Flowbit.Shared/
COPY Flowbit/src/Flowbit.Service/ Flowbit/src/Flowbit.Service/
COPY Flowbit/src/Flowbit.Infrastructure/ Flowbit/src/Flowbit.Infrastructure/
COPY Flowbit/src/Flowbit.Api/ Flowbit/src/Flowbit.Api/
RUN dotnet publish Flowbit/src/Flowbit.Api/Flowbit.Api.csproj -c Release --no-restore -o /app/publish /p:UseAppHost=false

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
ENTRYPOINT ["dotnet", "Flowbit.Api.dll"]
