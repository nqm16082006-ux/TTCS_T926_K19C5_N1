# Stage 1: Build
FROM mcr.microsoft.com/dotnet/sdk:9.0 AS build
WORKDIR /src

# Copy csproj and restore dependencies
COPY ["src/EventTicketBooking.Api/EventTicketBooking.Api.csproj", "src/EventTicketBooking.Api/"]
RUN dotnet restore "src/EventTicketBooking.Api/EventTicketBooking.Api.csproj"

# Copy full source code and publish
COPY . .
WORKDIR "/src/src/EventTicketBooking.Api"
RUN dotnet publish "EventTicketBooking.Api.csproj" -c Release -o /app/publish /p:UseAppHost=false

# Stage 2: Runtime
FROM mcr.microsoft.com/dotnet/aspnet:9.0 AS final
WORKDIR /app
EXPOSE 5000

ENV ASPNETCORE_URLS=http://+:5000
ENV ASPNETCORE_ENVIRONMENT=Staging

# Install curl for healthcheck in container
RUN apt-get update && apt-get install -y curl && rm -rf /var/lib/apt/lists/*

COPY --from=build /app/publish .

HEALTHCHECK --interval=10s --timeout=5s --start-period=15s --retries=3 \
  CMD curl -f http://localhost:5000/api/health || exit 1

ENTRYPOINT ["dotnet", "EventTicketBooking.Api.dll"]
