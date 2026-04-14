FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# Copy build props and project files for restore (layer cache)
COPY Directory.Build.props .
COPY src/CortexPlexus.Core/*.csproj src/CortexPlexus.Core/
COPY src/CortexPlexus.Parsing/*.csproj src/CortexPlexus.Parsing/
COPY src/CortexPlexus.Graph/*.csproj src/CortexPlexus.Graph/
COPY src/CortexPlexus.Search/*.csproj src/CortexPlexus.Search/
COPY src/CortexPlexus.Embedding/*.csproj src/CortexPlexus.Embedding/
COPY src/CortexPlexus.App/*.csproj src/CortexPlexus.App/
COPY src/CortexPlexus.Agent/*.csproj src/CortexPlexus.Agent/
RUN dotnet restore src/CortexPlexus.App/CortexPlexus.App.csproj \
 && dotnet restore src/CortexPlexus.Agent/CortexPlexus.Agent.csproj

# Copy source and publish
COPY src/ src/
RUN dotnet publish src/CortexPlexus.App/CortexPlexus.App.csproj -c Release -o /app/publish

# Publish the Local Agent for all 3 supported RIDs and bundle each as a
# tar.gz in /app/_agent. The server endpoint /api/agent/download serves
# whichever RID the client asks for, so fresh deploys ship the matching
# agent binary automatically — no more manual SCP on every version bump.
RUN set -eux; \
    for rid in linux-x64 win-x64 osx-x64; do \
        dotnet publish src/CortexPlexus.Agent/CortexPlexus.Agent.csproj \
            -c Release -r "$rid" --self-contained false \
            -o "/tmp/agent/$rid" --nologo; \
        mkdir -p /app/_agent; \
        tar -czf "/app/_agent/cortexplexus-agent-$rid.tar.gz" -C "/tmp/agent/$rid" .; \
    done; \
    rm -rf /tmp/agent; \
    ls -lh /app/_agent

# Runtime: slim image — Roslyn C# parsing handled by Local Agent on dev machine
# Tree-sitter (TS/JS/Py) + Markdown still work here without SDK
FROM mcr.microsoft.com/dotnet/aspnet:10.0-noble-chiseled AS runtime
WORKDIR /app

COPY --from=build /app/publish .
COPY --from=build /app/_agent /app/_agent

EXPOSE 8080

ENTRYPOINT ["dotnet", "CortexPlexus.App.dll"]
CMD ["serve", "--http", "--watch"]
