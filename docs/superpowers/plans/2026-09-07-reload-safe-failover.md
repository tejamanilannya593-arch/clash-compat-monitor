# Clash 重载安全故障接管 v0.1.1 Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 在保留订阅自动更新的前提下恢复最近验证节点，并让基础兼容节点能够安全接管。

**Architecture:** 在现有 `HealthState` 中持久化最近验证成功的实际节点，用纯函数 `ReloadRecovery.ChooseTarget` 决定重载后的恢复目标；`MonitorWorker` 只在确认发生配置重载时应用该目标。故障控制器统一使用可用等级谓词，接受完整兼容与基础兼容。

**Tech Stack:** C#/.NET Framework、PowerShell、Mihomo named-pipe API、现有单文件测试运行器。

---

### Task 1: 持久化最近验证成功的实际节点

**Files:**
- Modify: `src/StateStore.cs`
- Modify: `tests/Tests.cs`

- [ ] **Step 1: 写失败测试**

在状态往返测试中调用 `state.RememberPreferred("稳定节点", CandidateHealth.BasicCompatible, clock.UtcNow)`，保存并重载后断言节点名和时间一致；另断言失败等级不会覆盖已有稳定记录。

```csharp
state.RememberPreferred("稳定节点", CandidateHealth.BasicCompatible, clock.UtcNow);
store.Save(state, new[] { "稳定节点" });
HealthState loaded = store.Load();
Equal("稳定节点", loaded.PreferredNode, "preferred node roundtrip");
Equal(clock.UtcNow, loaded.PreferredNodeVerifiedUtc, "preferred time roundtrip");
loaded.RememberPreferred("失败节点", CandidateHealth.Transient, clock.UtcNow.AddMinutes(1));
Equal("稳定节点", loaded.PreferredNode, "failure does not replace preferred node");
```

- [ ] **Step 2: 运行测试确认编译失败**

Run: `powershell -NoProfile -ExecutionPolicy Bypass -File .\build.ps1`

Expected: `HealthState` 缺少 `RememberPreferred` 或属性而编译失败。

- [ ] **Step 3: 最小实现**

为 `HealthState` 增加 `PreferredNode`、`PreferredNodeVerifiedUtc` 和只接受两种可用等级的 `RememberPreferred`。`StateStore` 用可选 `P` 行保存和读取，旧的仅含 `N` 行状态继续可读。

```csharp
public void RememberPreferred(string name, CandidateHealth health, DateTime verifiedUtc)
{
    if (health != CandidateHealth.Compatible && health != CandidateHealth.BasicCompatible) return;
    PreferredNode = name ?? "";
    PreferredNodeVerifiedUtc = verifiedUtc.ToUniversalTime();
}
```

- [ ] **Step 4: 运行完整测试并提交**

Run: `powershell -NoProfile -ExecutionPolicy Bypass -File .\build.ps1`

Commit: `feat: persist the last verified node`

### Task 2: 选择安全的重载恢复目标

**Files:**
- Create: `src/ReloadRecovery.cs`
- Modify: `tests/Tests.cs`

- [ ] **Step 1: 写失败测试**

覆盖以下输入：重载且 30 分钟内的 `BasicCompatible` 节点返回名称；没有重载、超过 30 分钟、节点已移除、健康等级为 `Transient` 时返回 `null`。

```csharp
state.Records["稳定节点"] = new NodeHealthRecord("稳定节点", CandidateHealth.BasicCompatible,
    clock.UtcNow, clock.UtcNow, false);
state.RememberPreferred("稳定节点", CandidateHealth.BasicCompatible, clock.UtcNow);
Equal("稳定节点", ReloadRecovery.ChooseTarget(true, state, candidates, clock.UtcNow,
    TimeSpan.FromMinutes(30)), "recent basic node restored");
Equal(null, ReloadRecovery.ChooseTarget(false, state, candidates, clock.UtcNow,
    TimeSpan.FromMinutes(30)), "manual selector change is not overridden");
```

- [ ] **Step 2: 运行测试确认缺少类型**

Run: `powershell -NoProfile -ExecutionPolicy Bypass -File .\build.ps1`

Expected: `ReloadRecovery` 不存在而编译失败。

- [ ] **Step 3: 实现纯选择器**

实现 `ChooseTarget(bool reloadDetected, HealthState state, IEnumerable<CandidateNode> candidates, DateTime nowUtc, TimeSpan freshness)`，不访问文件或 Mihomo。

```csharp
public static string ChooseTarget(bool reloadDetected, HealthState state,
    IEnumerable<CandidateNode> candidates, DateTime nowUtc, TimeSpan freshness)
```

- [ ] **Step 4: 运行完整测试并提交**

Run: `powershell -NoProfile -ExecutionPolicy Bypass -File .\build.ps1`

Commit: `feat: choose a safe post-reload target`

### Task 3: 允许基础兼容节点进行故障接管

**Files:**
- Modify: `src/FailoverController.cs`
- Modify: `tests/Tests.cs`

- [ ] **Step 1: 写失败测试**

用一个 `BasicCompatible` 替代记录连续调用两次 `Decide`，断言第一次保持、第二次切换；继续断言 `Unknown`、`Transient`、`RegionBlocked` 和 `ServiceFailed` 不会被选中。

```csharp
var basic = new[] { new NodeHealthRecord("basic", CandidateHealth.BasicCompatible,
    clock.UtcNow, clock.UtcNow, false) };
Equal(false, controller.Decide(false, false, "current", basic).ShouldSwitch,
    "first basic-compatible failure stays");
Equal(true, controller.Decide(false, false, "current", basic).ShouldSwitch,
    "basic-compatible replacement switches");
```

- [ ] **Step 2: 运行测试确认第二次仍不切换**

Run: `powershell -NoProfile -ExecutionPolicy Bypass -File .\build.ps1`

Expected: `basic compatible replacement switches` 失败。

- [ ] **Step 3: 修改可用等级谓词**

将替代记录条件改为 `Compatible || BasicCompatible`，其他确认和冷却规则保持不变。

```csharp
.Where(x => x.Name != current &&
    (x.Health == CandidateHealth.Compatible || x.Health == CandidateHealth.BasicCompatible) &&
    x.CooldownUntilUtc <= clock.UtcNow)
```

- [ ] **Step 4: 运行完整测试并提交**

Run: `powershell -NoProfile -ExecutionPolicy Bypass -File .\build.ps1`

Commit: `fix: allow verified basic-compatible failover`

### Task 4: 接入重载恢复流程

**Files:**
- Modify: `src/MonitorWorker.cs`
- Modify: `src/Program.cs`
- Modify: `tests/Tests.cs`

- [ ] **Step 1: 写行为测试**

增加对恢复决定文本和 30 分钟配置默认值的测试；断言版本为 `0.1.1`。

```csharp
Equal("0.1.1", MonitorIdentity.Version, "release version");
Equal(TimeSpan.FromMinutes(30), MonitorConfiguration.CreateDefault().ReloadRecoveryFreshness,
    "reload recovery freshness");
```

- [ ] **Step 2: 运行测试确认失败**

Run: `powershell -NoProfile -ExecutionPolicy Bypass -File .\build.ps1`

Expected: 版本与默认恢复窗口断言失败。

- [ ] **Step 3: 调整运行顺序**

在 `RunOnce` 中保存 `reloadDetected`，恢复 IPv4 后加载候选和状态，再调用 `ReloadRecovery.ChooseTarget`；目标与当前值不同时调用共享选择器并记录短哈希。当前扫描成功后调用 `RememberPreferred`。

```csharp
string recoveryTarget = ReloadRecovery.ChooseTarget(reloadDetected, state, candidates,
    clock.UtcNow, config.ReloadRecoveryFreshness);
if (!String.IsNullOrEmpty(recoveryTarget) && recoveryTarget != current)
{
    mihomo.Select(config.SharedGroup, recoveryTarget);
    current = recoveryTarget;
}
```

- [ ] **Step 4: 更新版本和中文状态**

把版本更新为 `0.1.1`，增加“配置重载后恢复最近稳定节点”的中文决定映射，不输出原始节点名到日志。

- [ ] **Step 5: 运行完整测试并提交**

Run: `powershell -NoProfile -ExecutionPolicy Bypass -File .\build.ps1`

Commit: `feat: restore verified selection after config reload`

### Task 5: 文档、发布与实机部署

**Files:**
- Modify: `README.md`
- Modify: `package-release.ps1`
- Modify: `tests/Release.Tests.ps1`

- [ ] **Step 1: 更新发布说明和包版本**

说明自动订阅更新会应用配置、v0.1.1 的受控恢复条件，以及不会覆盖普通手动切换。发布目录和 ZIP 改为 `ClashCompatibilityMonitor-v0.1.1`。

- [ ] **Step 2: 运行全部验证并生成发布包**

Run: `powershell -NoProfile -ExecutionPolicy Bypass -File .\package-release.ps1`

Expected: C# 测试、增强脚本测试、发布安全检查全部通过并输出 v0.1.1 ZIP SHA-256。

- [ ] **Step 3: 只升级监控器并验证**

记录 `profiles.yaml` 与 `clash-verge.yaml` 哈希，运行 `scripts\upgrade.ps1 -SourceExe .\bin\ClashCompatibilityMonitor.exe`。确认 `ClashFilesUnchanged=true`、单一监控进程、状态版本 0.1.1、闲置工作集低于 15 MiB。

- [ ] **Step 4: 推送并创建堆叠 PR**

Push: `git push -u origin feature/reload-safe-v0.1.1`

PR base: `feature/balanced-v0.1`

PR 说明包含实机根因、恢复边界、测试结果和“未主动触发 Clash 重载”的安全说明。
