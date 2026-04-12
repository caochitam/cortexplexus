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
RUN dotnet restore src/CortexPlexus.App/CortexPlexus.App.csproj

# Copy source and publish
COPY src/ src/
RUN dotnet publish src/CortexPlexus.App/CortexPlexus.App.csproj -c Release -o /app/publish

# Runtime: slim image — Roslyn C# parsing handled by Local Agent on dev machine
# Tree-sitter (TS/JS/Py) + Markdown still work here without SDK
FROM mcr.microsoft.com/dotnet/aspnet:10.0-noble-chiseled AS runtime
WORKDIR /app

COPY --from=build /app/publish .

EXPOSE 8080

ENTRYPOINT ["dotnet", "CortexPlexus.App.dll"]
CMD ["serve", "--http", "--watch"]
