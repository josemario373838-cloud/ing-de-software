# Build stage
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src

# Copy csproj and restore (works when Docker build context is backend folder)
COPY . ./
RUN dotnet restore "DatamarketApi.csproj"
RUN dotnet publish -c Release -o /app --no-restore

# Runtime stage
FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS runtime
WORKDIR /app

# Allow runtime to bind to dynamic PORT provided by Railway
ENV ASPNETCORE_URLS=http://+:80
EXPOSE 80

COPY --from=build /app .

# Use shell form so we can fallback to PORT env var at runtime if provided
ENTRYPOINT ["/bin/sh", "-c", "export ASPNETCORE_URLS=\"http://+:${PORT:-80}\" && dotnet DatamarketApi.dll"]
