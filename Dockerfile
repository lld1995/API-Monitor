# ---------- 阶段 1：构建前端（产物输出到 /src/ApiMonitor.Api/wwwroot） ----------
FROM node:20-alpine AS web
WORKDIR /src
COPY web/package.json web/package-lock.json ./web/
RUN cd web && npm ci --no-audit --no-fund
COPY web/ ./web/
RUN cd web && npm run build

# ---------- 阶段 2：发布后端 ----------
FROM mcr.microsoft.com/dotnet/sdk:9.0 AS api
WORKDIR /src
COPY ApiMonitor.Api/ApiMonitor.Api.csproj ./ApiMonitor.Api/
RUN cd ApiMonitor.Api && dotnet restore
COPY ApiMonitor.Api/ ./ApiMonitor.Api/
RUN cd ApiMonitor.Api && dotnet publish -c Release -o /app --no-restore

# ---------- 阶段 3：运行时 ----------
FROM mcr.microsoft.com/dotnet/aspnet:9.0
WORKDIR /app
COPY --from=api /app ./
COPY --from=web /src/ApiMonitor.Api/wwwroot ./wwwroot
ENV ASPNETCORE_URLS=http://+:8080 TZ=Asia/Shanghai
EXPOSE 8080
VOLUME /data
ENTRYPOINT ["dotnet", "ApiMonitor.Api.dll"]
