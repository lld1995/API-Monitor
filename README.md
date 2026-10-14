# API Monitor

自部署的 API 监控平台：快速验证 LLM / 搜索 / HTTP / 代理 / 数据库服务是否可用，由后端定时调度检测，状态变更时通过邮箱或万记通推送通知。

## 功能特性

**快速检测**（无需登录，密钥不落库）
- **LLM**：真实发起对话验证模型可用性，支持 OpenAI、Claude、ChatGPT、Anthropic、方舟、Mimo、GLM、自定义（OpenAI / Anthropic 协议兼容端点）
- **搜索 API**：Bocha、Tavily、Brave、百度千帆、Serper、Exa
- **HTTP**：URL 健康检查（返回 200 判定正常）
- **代理**：HTTP / SOCKS5 链路验证（TCP CONNECT / UDP ASSOCIATE）
- **数据库**：MySQL（含 OceanBase / Manticore）、TDengine、Milvus、TuGraph 连通性检测

**监控中心**（需登录）
- 定时调度（1 分钟 ~ 1 天间隔），失败自动告警、恢复自动通知
- 执行记录可点开查看**完整的请求与响应内容**（密钥自动脱敏，仅显示末 4 位）
- 任务按用户隔离

**通知**
- 独立的「通知配置」页按用户维护渠道：**邮箱**（SMTP）与**万记通**（REST API，通知以备忘形式写入账号）
- 监控任务中勾选渠道即可，仅在**状态翻转**（正常→异常、异常→正常）时通知，持续失败不重复打扰

**其他**
- 用户注册登录（用户名 + 密码，PBKDF2 散列，Cookie 会话）
- API Key / 数据库密码 / 代理密码 AES-GCM 加密存储
- SQLite 与 MySQL 双数据库支持，MySQL 下自动建库

## 技术栈

- 后端：ASP.NET Core 9（`ApiMonitor.Api`）
- 前端：Vue 3 + TypeScript + Vite（`web`）

## 快速开始

环境要求：.NET 9 SDK、Node.js 18+。

```bash
# 1. 后端（先复制配置，见下节）
cd ApiMonitor.Api
cp appsettings.example.json appsettings.json   # 按需修改
dotnet run
# 后端监听 http://localhost:5240

# 2. 前端
cd web
npm install
npm run dev
# 前端 http://localhost:5173，/api 自动代理到 5240
```

首次启动会自动创建默认管理员 **`admin` / `admin123`**，并认领升级前的存量监控任务，请登录后妥善保管（暂不支持在线改密）。快速检测页无需登录即可使用。

## 配置说明（appsettings.json）

| 配置节 | 必填 | 说明 |
| --- | --- | --- |
| `ConnectionStrings:Default` | 是 | SQLite：`Data Source=api-monitor.db`；MySQL：`Server=...;Port=3306;User ID=...;Password=...;Database=api_monitor` |
| `Database:Provider` | 否 | `sqlite`（默认）或 `mysql`，MySQL 下目标库不存在会自动创建 |
| `Security:KeyEncryptionSecret` | 是 | API Key 加密密钥，任意长随机串；生产环境建议用环境变量 `Security__KeyEncryptionSecret` 注入 |
| `Smtp:*` | 邮箱通知需要 | 发件账号，见下方说明 |
| `Wanjitong:Endpoint` | 否 | 万记通 REST 端点，默认 `https://www.wanjitong.cn/api/memos` |
| `Proxy:Url` | 否 | 「使用服务端代理」开关所用的出站代理 |

> `appsettings.json` 含密钥，已在 `.gitignore` 中排除，请勿提交到仓库。

**邮箱通知**需要配置发件账号（收件邮箱在界面「通知配置」页添加即可）：

```json
"Smtp": {
  "Host": "smtp.qq.com",
  "Port": 587,
  "EnableSsl": true,
  "From": "you@example.com",
  "Username": "you@example.com",
  "Password": "SMTP 授权码（不是登录密码）"
}
```

端口请使用 **587（STARTTLS）**，.NET SmtpClient 不支持 465 隐式 SSL。QQ / 163 等邮箱需先开启 SMTP 服务并生成授权码。

**万记通通知**无需服务端配置：在 [wanjitong.cn](https://www.wanjitong.cn) 个人设置生成 API Key，添加为通知渠道即可。

## 生产部署

```bash
# 前端构建并放入后端静态目录
cd web && npm run build
rm -rf ../ApiMonitor.Api/wwwroot && cp -r dist ../ApiMonitor.Api/wwwroot

# 后端发布
cd ../ApiMonitor.Api && dotnet publish -c Release -o publish
cd publish && dotnet ApiMonitor.Api.dll
```

单服务即可同时提供 API 与页面。反向代理时注意转发 `Cookie` 头。

## 安全说明

- 用户密码 PBKDF2-SHA256（10 万次迭代 + 随机盐）散列存储
- 各类密钥 AES-GCM 加密后入库，界面与执行记录中一律脱敏（`****` + 末 4 位）
- 快速检测接口按 IP 限流（5 次/分钟），登录注册接口 20 次/分钟
- 数据库中不保存快速检测使用的密钥

## API 概览

| 路由 | 说明 |
| --- | --- |
| `POST /api/auth/{login,register,logout}`、`GET /api/auth/me` | 用户认证（Cookie 会话） |
| `GET/POST/PUT/DELETE /api/monitoring[/{id}]` | 监控任务 CRUD（按用户隔离） |
| `POST /api/monitoring/{id}/run`、`GET /api/monitoring/{id}/runs`、`GET /api/monitoring/runs/{runId}` | 执行与记录详情 |
| `GET/POST/PUT/DELETE /api/notifications[/{id}]` | 通知渠道 CRUD |
| `POST /api/public-check`、`/api/http-check`、`/api/models` | 快速检测（免登录，限流） |
