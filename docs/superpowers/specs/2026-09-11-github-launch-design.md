# Clash Compatibility Monitor GitHub 正式上线设计

## 目标

把本地已验证的 v0.6.0 整理为可信、易理解、易安装的公开项目，并让 GitHub 访客能够在 20 秒内判断项目用途、适用环境、安全边界和下载入口。上线工作同时保留已有 Git 历史，不强推远端分支，不通过购买、交换或自动化方式获取 Star。

## 发布路径

采用现有堆叠 PR 的渐进整理方式：

1. 先复核并合并以 `main` 为基线的 PR #1。
2. 将 PR #2 的基线调整为 `main`，保留其已有提交。
3. 把当前未提交成果按“核心监控与容灾”“Windows 使用体验”“文档、社区与发布”分组提交到 PR #2。
4. 将 PR #2 重命名为 v0.6.0 正式发布 PR，补充功能、风险、测试和升级说明。
5. CI 全部通过后合并 PR #2；禁止绕过失败检查。
6. 从合并后的 `main` 创建带说明的 Git Tag `v0.6.0` 和 GitHub Release，上传发布 ZIP 与 SHA-256 校验文件。

若 PR 基线调整后产生冲突，停止合并操作，在本地保留现有工作树并先解决冲突；不使用强制推送或重写 `main`。

## 仓库首页

保留项目名 `Clash Compatibility Monitor`，副标题使用“稳定优先的 Clash/Mihomo Windows 节点守护程序”。README 首屏按以下顺序组织：

1. 名称、副标题、CI/版本/许可证/平台徽章。
2. 一句话说明解决的问题，以及“下载最新版”入口。
3. 一张真实界面图或功能示意图。
4. 三项核心价值：避免误切、适配不同订阅、不修改 Clash 配置。
5. 三步快速开始。
6. 节点故障与服务公共异常的判断流程。

详细检测能力、延迟标准、安装说明、隐私边界、构建方式和限制放在首屏之后。中文 README 为主，提供简短英文入口，避免首屏成为版本更新清单。

## 视觉设计

沿用现有蓝色节点盾牌图标，使用深蓝到青色的稳定网络视觉。创建 1280×640 的 Social Preview：左侧为项目名和副标题，右侧为“当前节点—两个备用—服务检测”的简化连接图，底部展示 `Windows · Clash Verge Rev · Mihomo`。图片不使用机场、节点销售或夸张速度用语。

README 优先使用真实程序截图；若当前环境无法可靠捕获原生窗口，则先使用结构化功能示意图，并在发布后补充用户可复现的真实截图，不伪造运行数据。

## 信任与社区

公开仓库补齐并检查：

- `LICENSE`、`CONTRIBUTING.md`、`CODE_OF_CONDUCT.md`、`SECURITY.md`。
- Bug、兼容性和功能建议 Issue 表单，以及 PR 模板。
- CI 构建、测试和发布资产校验。
- 隐私说明：不上传订阅、节点名称、浏览记录、日志或遥测。
- 安全报告渠道与支持范围。

启用 GitHub Discussions，设置“公告、问答、功能建议、兼容性反馈”分类。仓库 Topics 使用 `clash`、`mihomo`、`clash-verge-rev`、`windows`、`proxy`、`failover`、`network-monitoring` 和 `tray-application`。启用适用于公开仓库的 Secret Scanning、Push Protection、Dependabot 与 Code Scanning；若某项受账户权限限制，在交付记录中明确标注。

## Release 内容

v0.6.0 Release 包含：

- `ClashCompatibilityMonitor-v0.6.0.zip`
- `ClashCompatibilityMonitor-v0.6.0.zip.sha256`
- 中文发布说明和简短英文摘要
- 支持环境、升级步骤、已知限制和隐私说明
- 从 v0.3.1 到 v0.6.0 的重要变化摘要

发布说明必须强调 ChatGPT、Gemini 与 JMComic 检测的证据边界，不能宣称后台 HTTP 探测证明账号内功能完整可用。

## 验证与完成标准

公开前必须重新验证：

- C# 构建和全部单元测试通过。
- Clash 增强脚本测试通过。
- Release 文件检查和敏感信息扫描通过。
- ZIP 与 SHA-256 文件匹配，发布目录与本地最终成品一致。
- README 中所有相对链接有效，Release 下载入口指向公开版本。
- PR #1、PR #2 的合并状态和 CI 结论可见。
- GitHub 首页显示 README、许可证、Topics、最新 Release 和 Social Preview。

首轮上线不包含付费代码签名、跨平台重写、Winget/Chocolatey/Scoop 收录或遥测系统。这些项目在 v0.6.0 稳定发布并积累真实反馈后再评估。
