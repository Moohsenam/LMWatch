# Build and run the orders service with nothing installed on the host but Docker.
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src
COPY server/ ./server/
RUN dotnet publish server/ClaudeWatch.Orders.csproj -c Release -o /app --nologo

FROM mcr.microsoft.com/dotnet/aspnet:8.0
WORKDIR /app
COPY --from=build /app ./

# The data folder is a volume, so a rebuild never touches orders or settings.
ENV CW_DATA=/data
ENV CW_URLS=http://0.0.0.0:5080
ENV DOTNET_EnableDiagnostics=0
VOLUME ["/data"]
EXPOSE 5080

RUN useradd --system --uid 10001 cworders && mkdir -p /data && chown cworders /data
USER cworders

ENTRYPOINT ["dotnet", "ClaudeWatch.Orders.dll"]
