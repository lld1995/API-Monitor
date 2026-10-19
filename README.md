<p align="center"><img src="docs/images/banner.svg" alt="API Monitor" width="100%"></p>

<h3 align="center">自部署 API 监控平台</h3>

<p align="center">五类检测 · 定时调度 · 状态变更通知 · 多用户隔离</p>

<p align="center">
  <a href="https://dotnet.microsoft.com"><img src="https://img.shields.io/badge/.NET-9.0-512BD4?logo=.net&logoColor=white" alt=".NET 9"></a>
  <a href="https://vuejs.org"><img src="https://img.shields.io/badge/Vue-3-4FC08D?logo=vuedotjs&logoColor=white" alt="Vue 3"></a>
  <a href="https://www.typescriptlang.org"><img src="https://img.shields.io/badge/TypeScript-3178C6?logo=typescript&logoColor=white" alt="TypeScript"></a>
  <img src="https://img.shields.io/badge/Docker-2496ED?logo=docker&logoColor=white" alt="Docker">
</p>

<br>

<p align="center"><img src="docs/images/monitor.png" alt="监控中心" width="100%"></p>
<p align="center"><sub>监控中心 —— 任务状态总览，异常实时可见</sub></p>

<br>

## 特性

| | |
| :--- | :--- |
| **检测能力** | LLM 真实对话 · 搜索 API · HTTP 探活 · 代理链路（TCP / UDP）· 数据库连通 |
| **供应商** | OpenAI / Claude / ChatGPT / Anthropic / 方舟 / Mimo / GLM / 自定义端点；Bocha / Tavily / Brave / 百度千帆 / Serper / Exa |
| **数据库** | MySQL（含 OceanBase / Manticore）· TDengine · Milvus · TuGraph |
| **执行留痕** | 每次检测保存完整请求与响应内容，密钥自动脱敏 |
| **通知** | 仅状态翻转（异常 / 恢复）时提醒；邮箱与万记通双渠道按用户管理，任务中勾选启用，持续失败不打扰 |
| **多用户** | 用户名密码注册登录，任务与渠道按账号隔离 |
| **安全** | 凭据 AES-GCM 加密存储 · 密码 PBKDF2 散列 · 公开接口限流 |
| **存储与部署** | SQLite 开箱即用，MySQL 自动建库；Docker 单容器同时提供页面与 API |

## 界面

<table>
  <tr>
    <td width="50%"><img src="docs/images/run-detail.png" alt="执行记录详情" width="100%"></td>
    <td width="50%"><img src="docs/images/notify.png" alt="通知配置" width="100%"></td>
  </tr>
  <tr>
    <td align="center"><sub>执行记录详情 —— 完整请求与响应，密钥脱敏</sub></td>
    <td align="center"><sub>通知配置 —— 渠道集中管理，任务勾选启用</sub></td>
  </tr>
  <tr>
    <td><img src="docs/images/quick-check.png" alt="快速检测" width="100%"></td>
    <td><img src="docs/images/login.png" alt="登录" width="100%"></td>
  </tr>
  <tr>
    <td align="center"><sub>快速检测 —— 免登录即测即走</sub></td>
    <td align="center"><sub>登录注册 —— 多用户数据隔离</sub></td>
  </tr>
</table>

## 快速开始

**Docker（推荐）**

```bash
git clone https://github.com/lld1995/API-Monitor.git && cd API-Monitor
bash run.sh        # 默认 5240 端口，自定义端口：bash run.sh 8080
```

多阶段构建（Node 编译前端 → .NET 发布 → aspnet 运行时），数据（配置、SQLite 数据库、Cookie 加密密钥）持久化在宿主机 `/root/api-monitor-data/`，容器常驻自启。首次运行自动生成配置，默认管理员 `admin / admin123`。

<details>
<summary>源码运行</summary>

环境要求 .NET 9 SDK 与 Node.js 18+：

```bash
# 后端
cd ApiMonitor.Api
cp appsettings.example.json appsettings.json
dotnet run                                # http://localhost:5240

# 前端（开发模式，/api 自动代理到 5240）
cd web
npm install && npm run dev                # http://localhost:5173
```

生产发布：`cd web && npm run build`（产物自动输出到 `ApiMonitor.Api/wwwroot`），再 `dotnet publish -c Release` 单服务运行。

</details>

## 配置

<details>
<summary>appsettings.json</summary>

| 配置节 | 必填 | 说明 |
| :--- | :--- | :--- |
| `ConnectionStrings:Default` | 是 | SQLite：`Data Source=api-monitor.db`；MySQL：`Server=...;User ID=...;Password=...;Database=api_monitor` |
| `Database:Provider` | 否 | `sqlite`（默认）或 `mysql`，MySQL 下库不存在会自动创建 |
| `Security:KeyEncryptionSecret` | 是 | 凭据加密密钥；生产环境建议环境变量 `Security__KeyEncryptionSecret` 注入，生成后勿改 |
| `Smtp:*` | 邮件通知需要 | `Host` / `Port`（用 587，不支持 465）/ `EnableSsl` / `From` / `Username` / `Password`（SMTP 授权码） |
| `Wanjitong:Endpoint` | 否 | 默认 `https://www.wanjitong.cn/api/memos` |
| `Proxy:Url` | 否 | 「使用服务端代理」出站地址 |

万记通通知无需服务端配置：在 [wanjitong.cn](https://www.wanjitong.cn) 生成 API Key，添加为通知渠道即可。

</details>

<details>
<summary>接口概览</summary>

| 路由 | 说明 |
| :--- | :--- |
| `POST /api/auth/{login,register,logout}` · `GET /api/auth/me` | 认证（Cookie 会话） |
| `GET/POST/PUT/DELETE /api/monitoring[/{id}]` | 任务 CRUD，按用户隔离 |
| `POST /api/monitoring/{id}/run` · `GET /api/monitoring/{id}/runs` · `GET /api/monitoring/runs/{runId}` | 手动执行 / 记录列表 / 记录详情 |
| `GET/POST/PUT/DELETE /api/notifications[/{id}]` | 通知渠道 CRUD |
| `POST /api/public-check` · `/api/http-check` · `/api/models` | 快速检测（免登录，限流） |

</details>

<br>

---

<p align="center"><sub>监控 · 通知 · 一处掌控</sub></p>
