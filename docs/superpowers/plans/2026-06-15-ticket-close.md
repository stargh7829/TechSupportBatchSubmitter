# 技术支持单关闭功能 Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 在待办查询页实现全选、单条关闭和每 5 秒一条的批量关闭。

**Architecture:** 在 Core 中增加可测试的关闭状态模型和 `TicketCloseQueue`，由
`ITicketPlatformClient` 提供单条受理总结与核验能力。WPF 只负责选择、确认、进度展示
和取消；WebView2 适配器负责读取平台表单、提交固定字段并核验结果。

**Tech Stack:** .NET 8、WPF、WebView2、xUnit

---

### Task 1: 关闭模型和队列

**Files:**
- Modify: `src/TechSupportBatchSubmitter.Core/Models/PlatformModels.cs`
- Modify: `src/TechSupportBatchSubmitter.Core/Interfaces/ITicketPlatformClient.cs`
- Create: `src/TechSupportBatchSubmitter.Core/Services/TicketCloseQueue.cs`
- Create: `tests/TechSupportBatchSubmitter.Tests/TicketCloseQueueTests.cs`

- [ ] 增加关闭状态、结果和进度事件模型。
- [ ] 先编写虚拟时钟测试，覆盖 5 秒间隔、普通失败继续、会话失效停止和结果不明确停止。
- [ ] 实现 `TicketCloseQueue`，使上述测试通过。

### Task 2: WebView2 平台关闭适配器

**Files:**
- Modify: `src/TechSupportBatchSubmitter.Wpf/Services/WebViewTicketPlatformClient.cs`

- [ ] 增加 `CloseTicketAsync`。
- [ ] 从 `toSolution.do` 表单提取并保留原字段。
- [ ] 设置 `cause_type=21`、`cause_description=已处理`、`solve_type=2`、
  `solution_description=已处理`，且不覆盖事件描述。
- [ ] 调用 `saveSolutionCase.do` 并核验工单离开未受理列表。
- [ ] 将会话失效、确定失败和不明确结果映射为不同结果/异常。

### Task 3: 待办查询界面

**Files:**
- Modify: `src/TechSupportBatchSubmitter.Wpf/MainWindow.xaml`
- Modify: `src/TechSupportBatchSubmitter.Wpf/MainWindow.xaml.cs`

- [ ] 在选择列表头增加全选复选框。
- [ ] 启用单行关闭和批量关闭按钮。
- [ ] 增加关闭进度、暂停和停止控件。
- [ ] 增加真实操作确认框和固定字段提示。
- [ ] 执行完成后刷新当前查询结果。

### Task 4: 文档、版本和发布

**Files:**
- Modify: `README.md`
- Modify: `docs/便携版使用说明.txt`
- Modify: `src/TechSupportBatchSubmitter.Wpf/TechSupportBatchSubmitter.Wpf.csproj`

- [ ] 将版本提升为 `1.2.0` 并更新使用说明。
- [ ] 运行 `dotnet test` 和 Release 编译。
- [ ] 运行 `scripts/build-portable.ps1` 生成便携目录及 ZIP。
- [ ] 核对 EXE、图标、使用说明和 ZIP 的更新时间及文件大小。
