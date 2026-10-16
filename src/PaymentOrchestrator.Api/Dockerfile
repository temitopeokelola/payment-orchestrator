FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

COPY global.json Directory.Build.props .editorconfig ./
COPY src/PaymentOrchestrator.Domain/PaymentOrchestrator.Domain.csproj src/PaymentOrchestrator.Domain/
COPY src/PaymentOrchestrator.Application/PaymentOrchestrator.Application.csproj src/PaymentOrchestrator.Application/
COPY src/PaymentOrchestrator.Infrastructure/PaymentOrchestrator.Infrastructure.csproj src/PaymentOrchestrator.Infrastructure/
COPY src/PaymentOrchestrator.Api/PaymentOrchestrator.Api.csproj src/PaymentOrchestrator.Api/
RUN dotnet restore src/PaymentOrchestrator.Api/PaymentOrchestrator.Api.csproj

COPY src/ src/
RUN dotnet publish src/PaymentOrchestrator.Api/PaymentOrchestrator.Api.csproj -c Release -o /app --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app
COPY --from=build /app .
USER $APP_UID
EXPOSE 8080
ENTRYPOINT ["dotnet", "PaymentOrchestrator.Api.dll"]
