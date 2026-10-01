# Clash Compatibility Monitor

> 持续实测 ChatGPT/Gemini 的 Clash/Mihomo Windows 节点寻优程序

[![CI](https://github.com/tejamanilannya593-arch/clash-compat-monitor/actions/workflows/ci.yml/badge.svg)](https://github.com/tejamanilannya593-arch/clash-compat-monitor/actions/workflows/ci.yml)
[![Release](https://img.shields.io/github/v/release/tejamanilannya593-arch/clash-compat-monitor)](https://github.com/tejamanilannya593-arch/clash-compat-monitor/releases/latest)
[![License](https://img.shields.io/github/license/tejamanilannya593-arch/clash-compat-monitor)](LICENSE)
[![Windows](https://img.shields.io/badge/Windows-10%20%7C%2011-0078D4)](QUICKSTART.md)

[下载最新版](https://github.com/tejamanilannya593-arch/clash-compat-monitor/releases/latest) · [快速开始](QUICKSTART.md) · [English](README.en.md)

程序默认每 **60 秒**复检当前节点。ChatGPT 和 Gemini 实测延迟都不超过 **800 ms** 且核心检测通过时，保持当前节点；超标或不可用时，先按逐节点 ChatGPT、Gemini 网站延迟预排序，再实测核心服务和地区。网站测速失败只影响排序，不直接淘汰节点。找到首个双核心延迟都不超过 800 ms 的合格节点即可切换并复验。

本程序支持 Clash Verge Rev/Mihomo 的 `proxies`、`proxy-providers` 和混合订阅，不要求固定节点命名，不修改配置或订阅文件，不读取浏览历史，不上传遥测，也不提供代理节点。

## 三步开始

1. 从 [Releases](https://github.com/tejamanilannya593-arch/clash-compat-monitor/releases/latest) 下载并解压完整发布包。
2. 保持 Clash 现有主选择组正常工作并运行 `Diagnose.cmd`；需要隔离探测时，可启用包内 `clash/enhancement.js` 并应用配置。
3. 运行 `Install.cmd`，检查“持续全节点寻优设置”，关闭窗口让程序在托盘运行。

首次使用、升级和卸载详见 [QUICKSTART.md](QUICKSTART.md)。诊断只检查本机配置标记，不读取或输出订阅内容。

## 当前持续寻优规则

- 默认每 **60 秒**复检当前节点；两项核心服务都通过、地区合格且较慢的一项不超过 **800 ms** 时不扫描其他节点。
- 当前节点超标或不可用时，先并行获取全部节点的 Clash 延迟，再按该顺序逐个实测基础网络、ChatGPT、Gemini 和实际出口地区。
- 基础网络和两项核心服务必须全部通过，实际出口必须同时符合 ChatGPT 与 Gemini 的地区限制。
- 首个合格且 `max(ChatGPT 延迟, Gemini 延迟) ≤ 800 ms` 的候选直接切换；复验失败则回滚并继续寻找。
- 无 800 ms 内的候选时，扫描完后按双核心较慢的一项、总延迟、Clash 延迟和节点名排名。手动强制寻优也使用完整排名。
- 切换写入现有普通选择组，不需要“统一稳定节点”分组。
- 切换后立即复验；失败则尝试下一名，全部失败时只在用户未改选的前提下回滚。检测和切换串行执行。
- 扫描期间发现用户手动改选会取消本轮自动写入。本机网络断开时暂停扫描，以后的周期会重试。

设置可调整复检间隔；超标后自动寻优固定启用。旧偏好、统计和历史文件继续保留。

## 日常使用与故障分析

双击托盘查看实际节点、最近决策、下一次检测和各服务结果。“立即开始一轮全节点寻优”可以不等下一个周期。“恢复上一个节点”是明确手动操作，同样要求切换前复检、切换后复验并保护用户改选。

“候选网站延迟排行”可另外手动刷新前 10 个候选的诊断数据。持续后台寻优会测量全部节点，Steam API、Google 等历史指标不参与最终排名。“切换记录与历史数据”只作参考。

首次启动建立基线，不补造历史；两轮检测间的多次外部切换无法全部追溯。日志重点记录失败计数、确认、候选淘汰、切换、复验、回滚及冷却起止。“运行统计”可导出本次启动的检测次数、资源占用及有限口径的探测正文量，不是网站可用率或完整流量账单。

记忆保存在本机 `state/experience.json`，偏好在 `state/preferences.state`。这些是后台探测数据，不是浏览记录。关闭窗口不退出；使用“退出节点守护”停止程序。二次启动 EXE 会唤起已有窗口。程序使用 Clash 现有普通选择组（例如“🚀 节点选择”），无需创建或选中“统一稳定节点”专用组，手动选择仍由 Clash 管理。

## 检测边界与节点身份

ChatGPT 与 Gemini 检查应用入口和官方认证基础设施；单独看到入口、登录页或验证码页只是部分可达，不能证明完整登录链路或真实对话。实际出口国家用于判断服务地区资格，不按节点名称过滤。后台探测不操作浏览器账号，也不能证明账号状态、模型生成质量或持续对话一定正常。

程序使用本机密钥，根据订阅来源、协议、服务器、端口和必要连接参数生成不可逆稳定身份。节点改名仍可关联可信历史；同名节点的连接材料变化后作为新节点处理。原始服务器、端口、UUID、密码和 SNI 不写入日志或状态；原始出口 IP 不保存、记录或显示。身份不明时不复用无关节点的长期信任，恢复仍需本轮实测。

旧版公共服务事故、备用池、故障计数与观察策略的记录可能仍存在于历史数据中；它们不参与当前全节点排名。当前行为以上述持续寻优规则为准。

## 长期运行

登录快捷方式启动内置监督模式，不依赖独立 `launcher.vbs`。异常退出最多重试 3 次，正常从托盘退出不会重启。工作进程连续 3 分钟无检测进度时监督进程可重启它；暂停和正常等待仍报告存活。协调线程未预期异常记录完整堆栈、显示降级状态并在 5 秒后恢复。

安装器使用实际用户目录，并支持迁移旧版被重定向到 `LocalCache` 的自启动实例。Mihomo 命名管道的连接、写入、读取均有期限。恢复扫描受本轮预算约束，取消或超时后仍尝试安全回滚。

## 要求

- Windows 10/11
- Clash Verge Rev 2.4.5，Mihomo 内核
- 已有可用订阅；本项目不提供代理节点
- 构建源码需要系统自带 .NET Framework C# 编译器和 Node.js

## 配置 Clash

默认监控现有主选择组，不要求专用“统一稳定节点”组。设置中的代理组名留空时自动识别，也可填写现有组名。没有独立探测设施时使用普通代理端口；每轮仍会按顺序临时选择节点完成实测，并在最终排名后写入第一名。候选排行的手动刷新只作诊断，不触发切换。

可选在 Clash Verge Rev 中启用 `clash/enhancement.js` 以提供隔离的 HTTP 探测。脚本保留现有普通组的类型、候选和过滤配置，优先使用已有“🚀 节点选择”，其次 `PROXY`／`Proxy`／“代理”，再选 `MATCH`／`FINAL` 指向的选择组或首个非内部选择组；不强造新的普通选择组。它只创建或更新：

- `🧪 兼容性探测`：隐藏的内部探测组，用于隔离测试，不需要用户选择。
- `127.0.0.1:7896`：仅本机可访问的探测监听器。

旧版“统一稳定节点”布局会移除并迁移到已有普通组，避免悬空引用和循环引用；没有普通选择组时不新增普通组，仅保留探测设施。已有显式路由规则优先保留，新增服务规则指向选中的普通组。安装脚本不会自动修改订阅或重启 Clash。

## 构建与测试

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\build.ps1
node .\clash\enhancement.test.js
node .\tests\Enhancement.Tests.js
node .\browser-extension\tests\run.js
powershell -NoProfile -ExecutionPolicy Bypass -File .\tests\Release.Tests.ps1
```

## 安装与升级

解压发布包后执行：

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\install.ps1
```

后续版本使用 `scripts/upgrade.ps1`。脚本先备份旧程序、浏览器宿主、扩展文件、状态和日志，再更新本项目文件及当前用户的 Chrome/Edge 本机消息注册，不重启 Clash；Clash 关键配置哈希发生变化时会回滚。

运行状态同时写入 `%LOCALAPPDATA%\ClashCompatibilityMonitor\current-status.txt`。在 Clash 原有主选择组中查看实际节点，无需改用专用分组。

## 卸载

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\uninstall.ps1
```

卸载会停止本项目进程并删除本项目在 `%LOCALAPPDATA%` 下的目录、启动快捷方式及本项目专属的 Chrome/Edge 当前用户本机消息注册，不修改 Clash 配置或订阅。浏览器中已加载的开发版扩展仍需手动移除。

## 限制

匿名后台探测不能证明账号状态、模型生成质量或持续对话一定正常。网站自身故障、账号风控或代理供应商拥堵仍可能在之后发生。界面中的响应时间来自轻量 HTTP 探测，不代表网页渲染、AI 实际生成或游戏服务器延迟。Steam 游戏和下载仍服从 Clash 现有直连规则。不上传遥测数据。

## License

MIT
