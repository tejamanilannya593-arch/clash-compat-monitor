# Clash Compatibility Monitor

一个面向 Clash Verge Rev 的轻量托盘节点守护工具。v0.2.0 以稳定性为第一目标：用户只需选择一次常用服务，程序便会在不接管订阅的前提下，综合平台可用性、响应、抖动、吞吐和倍率自动选择统一节点。

## v0.2.0 能力

- 首次运行勾选常用服务；可选择 Google、GitHub、ChatGPT、Gemini、Steam、Discord、Spotify 和 Epic。
- 后台只对用户选择的服务建立兼容门槛，并显示各服务的 HTTP 响应时间；不读取浏览历史，也不扫描当前网页。
- 微信、B站等国内流量沿用 Clash 直连规则；Steam 游戏内容下载直连。
- 地区名称不决定兼容性。台湾、香港、日本、新加坡等标签均不自动加分或排除。
- 候选节点来自 Clash 的结构化叶节点对象，不要求旗帜、地区名或固定倍率格式；倍率无法解析时仍会参与实测，只是不做确定的成本加减分。
- `Compatible` 表示后台可可靠验证的项目通过；`BasicCompatible` 表示 AI 页面、登录或验证链路可达但账号内功能未验证；`Unknown` 表示证据不足。
- 仅在目标本轮验证、分数至少提升 20%、当前节点已持有 10 分钟时切换；完全断网除外。
- Clash Verge 自动更新订阅并应用配置后，程序会恢复 30 分钟内验证成功且仍存在的最近稳定节点；普通手动切换不会被覆盖。
- 当前节点连续失败时，完整兼容和基础兼容节点都可以在本轮重新验证后参与接管，未知或处于冷却期的节点不会被采用。
- 单实例运行，日志最多 1 MiB，状态历史最多 256 KiB；一次吞吐刷新最多 5 MiB。
- Mihomo 命名管道的连接、写入和读取都有期限，核心异常不会让一次检测永久挂起。

## 日常使用

首次启动会打开设置窗口。保存常用服务后，Monitor 缩到系统托盘并长期自动运行；正常状态不会弹窗。双击托盘图标可查看实际节点、最近决策、下一次检测时间和各平台实测结果，也可立即复检、暂停或恢复自动优化。

常用服务和自动模式设置保存在本机状态目录的 `state\preferences.state`，不会上传。

关闭详情窗口不会退出守护。需要退出时使用托盘菜单或详情页的“退出节点守护”。二次启动 EXE 会唤起已经运行的详情窗口，不再静默显示为“打不开”。Clash 的“🌐 统一稳定节点”组仍显示当前真实叶节点，节点清单和手动选择继续由 Clash 管理。

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

运行状态同时写入 `%LOCALAPPDATA%\ClashCompatibilityMonitor\current-status.txt`。Clash 首页选择“统一稳定节点”代理组，即可直接看到实际节点名称。

## 卸载

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\uninstall.ps1
```

卸载会停止本项目进程并删除本项目在 `%LOCALAPPDATA%` 下的目录和启动快捷方式，不修改 Clash 配置或订阅。

## 限制

无登录后台检测无法证明 ChatGPT 对话或 Gemini 生成等账号级功能一定可用，也无法消除网站自身故障、账号风控或代理供应商拥堵。界面中的响应时间来自轻量 HTTP 探测，不代表网页渲染、AI 生成或游戏服务器延迟。Steam 游戏和下载仍服从 Clash 现有直连规则。v0.2.0 是本地优先版本，不上传遥测数据。配置重载恢复依赖运行时 IPv6 覆盖重新出现这一信号；若用户的 Clash 配置始终强制关闭 IPv6，程序会保留常规故障检测，但可能无法识别该次重载。

## License

MIT
