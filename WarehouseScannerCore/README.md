# WarehouseScannerCore

ASP.NET Core MVC 仓库扫码系统（Epicor 集成）。

## 部署限制

- 本项目仅支持单实例部署。
- 进程内锁（业务锁、装箱单号锁、配置写锁等）仅在单实例部署下有效；多实例部署时各实例互不可见，需改用外置分布式锁（如 Redis）才能保证跨实例互斥。

## 上线检查单

1. **AES 密钥（必须）**：生产环境配置环境变量 `AES_ENCRYPTION_KEY` / `AES_ENCRYPTION_IV`，缺失或仍使用曾提交进仓库的旧密钥时应用将拒绝启动；启动后需用新密钥重新加密各站点 Epicor ApiKey。
2. **反向代理（部署在 Nginx/IIS ARR 后时必须）**：将 `appsettings.json` 中 `ReverseProxy:Enabled` 置为 `true` 并配置 `KnownProxies` 为代理服务器地址。否则限流（120 次/分钟）会按代理 IP 全员共享计数，导致批量 429 误伤；登录日志也只会记录代理 IP。
3. **无代理直连部署**：保持 `ReverseProxy:Enabled=false`（默认），不要开启，避免外部伪造 X-Forwarded-For 绕过限流。

