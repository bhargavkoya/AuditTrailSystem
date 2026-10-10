# Shared Dockerfile for every .NET service. Build context is the repo root.
#   docker build -f infra/docker/service.Dockerfile --build-arg PROJECT=services/auth-service/AuditFlow.Auth.Api --build-arg DLL=AuditFlow.Auth.Api.dll .
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
ARG PROJECT
WORKDIR /src
COPY global.json Directory.Build.props ./
COPY contracts/ contracts/
COPY services/ services/
RUN dotnet publish ${PROJECT} -c Release -o /app /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:8.0
ARG DLL
ENV DLL=${DLL}
WORKDIR /app
COPY --from=build /app .
ENV ASPNETCORE_URLS=http://+:8080
EXPOSE 8080
ENTRYPOINT ["sh", "-c", "exec dotnet $DLL"]
