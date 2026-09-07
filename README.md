# Clash Compatibility Monitor

一个面向 Clash Verge Rev 的轻量后台节点择优器。v0.1.1 以稳定性为第一目标，在不接管订阅的前提下，综合平台可用性、响应、抖动、吞吐和倍率选择统一节点。

## 初版能力

- 必测 Google、GitHub、ChatGPT、Gemini、Steam 商店、社区和 Web API。
- Discord、Spotify、Epic 客户端运行时按需加入检测。
- 微信、B站等国内流量沿用 Clash 直连规则；Steam 游戏内容下载直连。
- 地区名称不决定兼容性。台湾、香港、日本、新加坡等标签均不自动加分或排除。
- `Compatible` 表示后台可可靠验证的项目通过；`BasicCompatible` 表示 AI 页面、登录或验证链路可达但账号内功能未验证；`Unknown` 表示证据不足。
- 仅在目标本轮验证、分数至少提升 20%、当前节点已持有 10 分钟时切换；完全断网除外。
- Clash Verge 自动更新订阅并应用配置后，程序会恢复 30 分钟内验证成功且仍存在的最近稳定节点；普通手动切换不会被覆盖。
- 当前节点连续失败时，完整兼容和基础兼容节点都可以在本轮重新验证后参与接管，未知或处于冷却期的节点不会被采用。
- 单实例运行，日志最多 1 MiB，状态历史最多 256 KiB；一次吞吐刷新最多 5 MiB。

## 要求

- Windows 10/11
- Clash Verge Rev 2.4.5，Mihomo 内核
- 已有可用订阅；本项目不提供代理节点
- 构建源码需要系统自带 .NET Framework C# 编译器和 Node.js

## 配置 Clash

在 Clash Verge Rev 中把 `clash/enhancement.js` 作为 JavaScript 增强脚本启用并重新生成配置。脚本会创建：

- `🌐 统一稳定节点`：实际使用的统一节点组
- `🧪 兼容性探测`：独立探测组
- `127.0.0.1:7896`：仅本机可访问的探测监听器

确认代理页面能看到以上两个组后再安装监控器。安装脚本不会自动修改订阅或重启 Clash。

## 构建与测试

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\build.ps1
node .\clash\enhancement.test.js
powershell -NoProfile -ExecutionPolicy Bypass -File .\tests\Release.Tests.ps1
```

## 安装与升级

解压发布包后执行：

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\install.ps1
```

后续版本使用 `scripts/upgrade.ps1`。脚本先备份旧程序、状态和日志，只替换监控器，不重启 Clash；Clash 关键配置哈希发生变化时会回滚。

运行状态位于 `%LOCALAPPDATA%\ClashCompatibilityMonitor\current-status.txt`。Clash 首页选择“统一稳定节点”代理组，即可直接看到实际节点名称。

## 卸载

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\uninstall.ps1
```

卸载会停止本项目进程并删除本项目在 `%LOCALAPPDATA%` 下的目录和启动快捷方式，不修改 Clash 配置或订阅。

## 限制

无登录后台检测无法证明 ChatGPT 对话或 Gemini 生成等账号级功能一定可用，也无法消除网站自身故障、账号风控或代理供应商拥堵。v0.1.1 是本地优先版本，不上传遥测数据。配置重载恢复依赖运行时 IPv6 覆盖重新出现这一信号；若用户的 Clash 配置始终强制关闭 IPv6，程序会保留常规故障检测，但可能无法识别该次重载。

## License

MIT
