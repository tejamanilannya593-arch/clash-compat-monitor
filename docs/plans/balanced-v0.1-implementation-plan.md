# Clash 均衡节点监控 v0.1.0 Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 交付一个低内存、稳定优先、按实测而非地区名选择节点的本机可用 v0.1.0。

**Architecture:** 保留现有 C# 单进程监控器、Mihomo 命名管道和独立探测监听器。候选过滤只做结构校验；兼容性是排名门槛，统一口径的响应、稳定性、吞吐和倍率组成质量分；部署只替换监控器 EXE，Clash 配置由哈希保护。

**Tech Stack:** C#/.NET Framework、PowerShell、Clash Verge Rev/Mihomo REST over named pipe、Node.js 增强脚本测试。

---

源码目录 `work/clash-compat-monitor` 已按用户要求初始化为独立 Git 仓库；实现提交保留在 `feature/balanced-v0.1` 分支，并同时记录测试输出、SHA-256 和升级备份。

### Task 1: 删除基于节点名称的地区封禁

**Files:**
- Modify: `work/clash-compat-monitor/tests/Tests.cs`
- Modify: `work/clash-compat-monitor/src/CandidateCatalog.cs`

- [ ] **Step 1: 写失败测试**

将候选测试期望改为台湾、香港及包含地区词的普通代理均可进入结构候选，同时保留倍率、非节点文本和重复项过滤：

```csharp
Equal(8, candidates.Count, "region names do not filter candidates");
Equal(true, candidates.Any(x => x.Name.Contains("台湾")), "taiwan remains eligible");
Equal(true, candidates.Any(x => x.Name.Contains("香港")), "region label is not compatibility evidence");
```

- [ ] **Step 2: 运行测试确认失败**

Run: `powershell -NoProfile -ExecutionPolicy Bypass -File work/clash-compat-monitor/build.ps1`

Expected: `candidate count`、`taiwan remains eligible` 或 `region label is not compatibility evidence` 失败。

- [ ] **Step 3: 实现最小修复**

从 `CandidateCatalog.Filter` 删除对“中国大陆、香港、澳门、台湾、Taiwan”文字的判断，只保留真实区域旗帜开头、倍率不高于 3 和去重逻辑。

- [ ] **Step 4: 运行完整测试**

Run: `powershell -NoProfile -ExecutionPolicy Bypass -File work/clash-compat-monitor/build.ps1`

Expected: 退出码 0，所有候选过滤测试通过。

### Task 2: 为所有节点统一响应测量口径

**Files:**
- Modify: `work/clash-compat-monitor/src/Models.cs`
- Modify: `work/clash-compat-monitor/src/CompatibilityScanner.cs`
- Modify: `work/clash-compat-monitor/src/QualityScoring.cs`
- Modify: `work/clash-compat-monitor/src/MonitorWorker.cs`
- Modify: `work/clash-compat-monitor/tests/Tests.cs`

- [ ] **Step 1: 写失败测试**

为扫描结果增加探测数，并规定质量响应来自多平台扫描平均值：

```csharp
Equal(7, result.ProbeCount, "mandatory probe count retained");
Equal(125.0, QualityMeasurement.ResponseMilliseconds(result, 5000), "response uses service average");
Equal(5000.0, QualityMeasurement.ResponseMilliseconds(
    new CandidateScanResult("none", CandidateHealth.Transient, null, "none", 0, 0), 5000),
    "empty scan uses fallback");
```

- [ ] **Step 2: 运行测试确认缺少 API**

Run: `powershell -NoProfile -ExecutionPolicy Bypass -File work/clash-compat-monitor/build.ps1`

Expected: 因 `ProbeCount` 或 `QualityMeasurement` 尚不存在而编译失败。

- [ ] **Step 3: 保留探测数量**

给 `CandidateScanResult` 构造器增加可选 `probeCount`，扫描器每调用一次 `probe.Probe` 就递增，并在成功、基础可用、待验证和失败结果中传递该值。

- [ ] **Step 4: 增加统一换算函数**

在 `QualityScoring.cs` 新增：

```csharp
public static class QualityMeasurement
{
    public static double ResponseMilliseconds(CandidateScanResult scan, double fallback)
    {
        return scan != null && scan.ProbeCount > 0 && scan.TotalMilliseconds > 0
            ? (double)scan.TotalMilliseconds / scan.ProbeCount : fallback;
    }
}
```

- [ ] **Step 5: 替换不一致比较**

`MonitorWorker` 中当前节点和刷新候选均使用 `QualityMeasurement.ResponseMilliseconds(scan, 5000)`；`GetDelay` 结果仅供 `CandidatePreselector` 选择前 8 个，不进入最终响应分。

- [ ] **Step 6: 运行完整测试**

Run: `powershell -NoProfile -ExecutionPolicy Bypass -File work/clash-compat-monitor/build.ps1`

Expected: 退出码 0，扫描计数和统一响应测试通过。

### Task 3: 对响应头和正文读取实施同一个真实超时

**Files:**
- Modify: `work/clash-compat-monitor/src/CompatibilityScanner.cs`
- Modify: `work/clash-compat-monitor/tests/Tests.cs`

- [ ] **Step 1: 写失败测试**

添加一个测试流，其 `ReadAsync` 等待取消令牌；调用带 50ms 超时的读取函数应抛出或转换为超时结果，且耗时低于 1 秒：

```csharp
var timer = Stopwatch.StartNew();
Equal(true, HttpServiceProbe.ReadTimesOut(new CancellationAwareStream(), TimeSpan.FromMilliseconds(50)),
    "body read obeys timeout");
Equal(true, timer.ElapsedMilliseconds < 1000, "body timeout is bounded");
```

测试流只实现读取所需成员，不访问网络。

- [ ] **Step 2: 运行测试确认缺少超时实现**

Run: `powershell -NoProfile -ExecutionPolicy Bypass -File work/clash-compat-monitor/build.ps1`

Expected: 因 `ReadTimesOut` 或新读取 API 不存在而失败。

- [ ] **Step 3: 实现请求级取消**

在 `Probe` 内创建 `CancellationTokenSource(timeout)`，将令牌传给 `SendAsync` 和 `Stream.ReadAsync`；正文仍最多读取 4096 字节。取消统一转换为 `TransientFailure("timeout")`，并释放请求、响应、流和令牌源。

- [ ] **Step 4: 运行超时和完整测试**

Run: `powershell -NoProfile -ExecutionPolicy Bypass -File work/clash-compat-monitor/build.ps1`

Expected: 退出码 0；正文超时、4096 字节上限和现有 HTTP 判定测试全部通过。

### Task 4: 产出可解释的 v0.1.0 状态

**Files:**
- Create: `work/clash-compat-monitor/src/StatusReport.cs`
- Modify: `work/clash-compat-monitor/src/Program.cs`
- Modify: `work/clash-compat-monitor/src/MonitorWorker.cs`
- Modify: `work/clash-compat-monitor/tests/Tests.cs`

- [ ] **Step 1: 写失败测试**

规定版本和状态文本至少包含实际节点、状态、分数与原因，且不包含控制器密钥：

```csharp
Equal("0.1.0", MonitorIdentity.Version, "release version");
string report = StatusReport.Format(now, "台湾 T1", CandidateHealth.BasicCompatible, 82.3, "保持当前节点", "AI 登录待确认");
Equal(true, report.Contains("实际节点：台湾 T1"), "status shows leaf node");
Equal(true, report.Contains("综合分：82.3"), "status shows score");
Equal(false, report.Contains("secret"), "status omits credentials");
```

- [ ] **Step 2: 运行测试确认缺少版本和报告器**

Run: `powershell -NoProfile -ExecutionPolicy Bypass -File work/clash-compat-monitor/build.ps1`

Expected: 因 `Version` 或 `StatusReport` 不存在而编译失败。

- [ ] **Step 3: 实现纯格式化报告器**

`StatusReport.Format` 只接收已脱敏字段，使用中文输出版本、UTC 时间、实际节点、状态、综合分、决定与说明；分数未知时显示“本轮未排名”。

- [ ] **Step 4: 接入运行状态**

`MonitorWorker` 保存本轮分数和切换决定，选择器变化后重新读取实际叶节点，再原子写入 `current-status.txt.tmp` 并替换正式文件。不要把订阅 URL、控制器 secret 或原始配置写入状态。

- [ ] **Step 5: 运行完整测试**

Run: `powershell -NoProfile -ExecutionPolicy Bypass -File work/clash-compat-monitor/build.ps1`

Expected: 退出码 0，版本与报告测试通过。

### Task 5: 整理初版说明与发布检查

**Files:**
- Create: `work/clash-compat-monitor/README.md`
- Create: `outputs/ClashCompatibilityMonitor-v0.1.0/README.md`
- Copy at release time: `outputs/ClashCompatibilityMonitor-v0.1.0/ClashCompatibilityMonitor.exe`
- Copy at release time: `outputs/ClashCompatibilityMonitor-v0.1.0/uninstall.ps1`
- Copy at release time: `outputs/ClashCompatibilityMonitor-v0.1.0/LICENSE`

- [ ] **Step 1: 编写 README**

README 明确：适用 Clash Verge Rev 2.4.5、平台范围、三层可用性语义、稳定性权重、Steam 下载与国内直连、资源目标、状态文件位置、安装/升级/卸载和限制。明确“地区名不决定兼容性”与“AI 账号功能无法由无登录后台完整验证”。

- [ ] **Step 2: 加入开源许可证**

初版采用 MIT License；版权行使用项目名与年份，不写用户个人姓名或机器信息。

- [ ] **Step 3: 生成干净发布目录**

只复制构建产物、通用说明、许可证和卸载脚本。不得复制 `mihomo_pipe.ps1`、Clash YAML、状态、日志、订阅链接、控制器密钥或用户专属 profile UID。

- [ ] **Step 4: 扫描敏感内容**

Run: `rg -n -i "secret:|authorization: bearer|subscription|<真实订阅标识>|<真实订阅域名>" outputs/ClashCompatibilityMonitor-v0.1.0`

Expected: 无匹配；README 中若使用“subscription”通用术语，应改为中文或把扫描限定为 URL/凭据模式。

### Task 6: 安全部署与实机验收

**Files:**
- Use: `work/clash-compat-monitor/upgrade-monitor-only.ps1`
- Verify: `C:/Users/lenovo/AppData/Local/ClashCompatibilityMonitor/current-status.txt`

- [ ] **Step 1: 运行全部离线验证**

Run: `powershell -NoProfile -ExecutionPolicy Bypass -File work/clash-compat-monitor/build.ps1`

Run: `node work/clash-compat-monitor/clash/enhancement.test.js`

Expected: 两者退出码 0，没有 FAIL。

- [ ] **Step 2: 记录运行前保护信息**

读取但不输出敏感内容：监控器 PID、统一稳定节点实际选择、代理对象数量、候选数量，以及 `profiles.yaml`、`clash-verge.yaml`、活动增强脚本的 SHA-256。

- [ ] **Step 3: 仅升级监控器**

Run: `powershell -NoProfile -ExecutionPolicy Bypass -File work/clash-compat-monitor/upgrade-monitor-only.ps1`

Expected: 输出备份目录、一个新 PID、`ClashFilesUnchanged=true`；不得重启 Clash Verge。

- [ ] **Step 4: 等待首轮与下一轮复核**

轮询 `current-status.txt`，首轮若发生切换允许显示 `Unknown`；下一轮必须变为 `Compatible`、`BasicCompatible` 或有明确失败原因。等待采用文件更新时间条件，不使用固定长睡眠。

- [ ] **Step 5: 最终验收**

确认：单一监控进程、闲置工作集低于 15 MiB、Clash 关键哈希未变、代理对象和可见节点数量未减少、台湾节点仍在候选、首页统一稳定节点显示的叶节点与 API 一致。

## 自审结果

- 规格中的候选、状态、流量、评分、超时、资源、发布和回滚要求均对应到 Task 1–6。
- API 名称在各任务中保持一致：`ProbeCount`、`QualityMeasurement.ResponseMilliseconds`、`StatusReport.Format`、`MonitorIdentity.Version`。
- 计划不包含占位符；当前非 Git 目录的限制已明确，避免误提交到其他项目。
