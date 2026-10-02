# Build stage
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY global.json Directory.Build.props Directory.Packages.props Bytex.slnx ./
COPY src/ src/
COPY examples/ examples/
COPY README.md ./
RUN dotnet restore Bytex.slnx
RUN dotnet publish src/Bytex.Cli/Bytex.Cli.csproj -c Release -o /app/cli --no-restore
RUN dotnet publish examples/Bytex.Examples/Bytex.Examples.csproj -c Release -o /app/examples --no-restore

# Runtime stage
FROM mcr.microsoft.com/dotnet/runtime:10.0 AS runtime
WORKDIR /app
COPY --from=build /app/cli ./
COPY --from=build /app/examples/Bytex.Examples.dll ./plugins/examples/
COPY examples/configs/ ./configs/
VOLUME ["/data"]
ENV DOTNET_EnableDiagnostics=0
ENTRYPOINT ["dotnet", "bytex.dll", "--plugins", "/app/plugins"]
CMD ["--help"]
