# Build from the repository root:  docker compose build   (or: docker build .)

# 1. Build the web app.
FROM node:24-alpine AS web
WORKDIR /src
COPY package.json package-lock.json .npmrc ./
COPY packages/web/package.json packages/web/
COPY tools/package.json tools/
RUN npm ci --ignore-scripts
COPY tsconfig.base.json ./
COPY packages ./packages
RUN npm run build -w @greenglow/web

# 2. Build and test the server.
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS server
WORKDIR /src/server
COPY server/src/ ./src/
RUN dotnet restore src/GreenGlow.Server/GreenGlow.Server.csproj
RUN dotnet publish src/GreenGlow.Server/GreenGlow.Server.csproj -c Release --no-restore -o /app

# The fake IBM i, for the public demo only (compose.demo.yaml):  docker build --target fakehost .
# Placeholder screens and one placeholder print user; never part of the server image.
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS fakehost-build
WORKDIR /src/server
COPY server/src/GreenGlow.Engine/ ./src/GreenGlow.Engine/
COPY server/tests/GreenGlow.FakeHost/ ./tests/GreenGlow.FakeHost/
RUN dotnet publish tests/GreenGlow.FakeHost/GreenGlow.FakeHost.csproj -c Release -o /fakehost

FROM mcr.microsoft.com/dotnet/runtime:10.0 AS fakehost
WORKDIR /fakehost
COPY --from=fakehost-build /fakehost ./
USER app
# 5250 Telnet, and the Network Print server on IBM's own port (the server always uses it in Production).
EXPOSE 2323 8474
ENTRYPOINT ["dotnet", "GreenGlow.FakeHost.dll", "--bind", "0.0.0.0", "--port", "2323", "--print-port", "8474"]

# 3. Runtime (the default target: the server image).
FROM mcr.microsoft.com/dotnet/aspnet:10.0
ENV ASPNETCORE_ENVIRONMENT=Production \
    ASPNETCORE_HTTP_PORTS=8080 \
    Server__StaticDir=/app/wwwroot \
    Relay__CaFile=/app/certs/ibmi-ca.pem \
    Data__DbPath=/app/data/greenglow.db
WORKDIR /app
COPY --from=server /app ./
COPY --from=web /src/packages/web/dist ./wwwroot
RUN mkdir -p /app/data && chown app:app /app/data
VOLUME ["/app/data"]
USER app
EXPOSE 8080
HEALTHCHECK --interval=30s --timeout=3s \
  CMD ["dotnet", "GreenGlow.Server.dll", "--healthcheck"]
ENTRYPOINT ["dotnet", "GreenGlow.Server.dll"]
