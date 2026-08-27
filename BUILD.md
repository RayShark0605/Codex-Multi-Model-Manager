# Build & Release

## 工具链

- Windows 10/11 x64
- .NET SDK `9.0.316`（由 `global.json` 固定）；所有产品项目目标框架均为 .NET 8。
- PowerShell 7 或 Windows PowerShell 5.1。
- NuGet 网络访问仅在首次 restore/publish 缺少 runtime pack 时需要。

运行时依赖集中在 `Directory.Packages.props`：Tomlyn `2.10.1`；测试使用 xUnit v3、Microsoft.NET.Test.Sdk 与 Coverlet。Restore 包目录固定在工程内 `.packages`。

## Restore / Build

```powershell
# 在仓库根目录（包含 CodexModelManager.sln 的目录）执行
dotnet restore .\CodexModelManager.sln
dotnet build .\CodexModelManager.sln -c Debug --no-restore
dotnet build .\CodexModelManager.sln -c Release --no-restore
```

Debug 主程序（相对仓库根目录）：

`src\CodexModelManager.App\bin\Debug\net8.0-windows\CodexModelManager.exe`

不要在开发/自动测试期间直接启动 Debug 程序指向真实 `CODEX_HOME`；首次 GUI 启动按设计会创建真实 Initial Snapshot。需要 GUI 调试时，应在启动进程中同时设置临时 `CODEX_HOME` 和 `CMM_LOCALAPPDATA_OVERRIDE`。

## 单元测试

```powershell
dotnet test .\tests\CodexModelManager.Tests\CodexModelManager.Tests.csproj -c Release
dotnet test .\tests\CodexModelManager.App.Tests\CodexModelManager.App.Tests.csproj -c Release
```

Core 普通测试只使用 `%TEMP%\CodexModelManager.Tests\<guid>`，`TestCodexHomeProvider` 对真实 `%USERPROFILE%\.codex` 有硬守卫。App 测试目标为 `net8.0-windows`，在专用 STA 线程中构造控件并使用临时 composition；不会显示窗口、写真实 Codex 配置或访问真实 Credential Manager。

### 只读 LM Studio Live Level 1/2

要求 LM Studio Server 已启动且 1234 可访问：

```powershell
$env:CMM_RUN_LIVE_LM='1'
dotnet test .\tests\CodexModelManager.Tests\CodexModelManager.Tests.csproj `
  --filter 'Category=LiveLmStudio'
Remove-Item Env:CMM_RUN_LIVE_LM
```

这会先发送 `instructions + user` 与 `instructions + developer + user` 差分请求。只有指令层级通过才继续发送带同样结构的 SSE 与 harmless dummy function schema；不会改变模型生命周期。若 native `/api/v1/models` 当前没有报告 `loaded_instances`，测试会明确 Skip，而不会把 `lms ps` 或理论模型列表猜作 Server loaded context。

同一分类还会用 native loaded snapshot 作为权威身份，同时查询 `lms ls --json --variants` 与 endpoint-aware `lms ps --json --host <host> --port <port>`。`lms ps` 只提供 loaded-instance 文件位置证据；候选必须匹配 source/identifier/publisher/type/architecture/quantization/context，且唯一落在 `~/.lmstudio/settings.json` 配置的 downloads 根或传统 models 根。现场测试会断言最终 provenance 为 `lms ps --json`，再只读核对 GGUF 模板。

### 显式 opt-in 的事务式 LM Studio 生命周期测试

该测试会真实 unload/load 当前 LLM，必须先完全关闭 Codex，并只在准备接受一次模型重载时显式启用。测试捕获当前 native load config、注入运行时模板、要求 hierarchy PASS，随后在 `finally` 中卸载补丁实例并不带模板恢复原配置：

```powershell
$env:CMM_RUN_LIVE_LM_MUTATION='1'
dotnet test .\tests\CodexModelManager.Tests\CodexModelManager.Tests.csproj `
  --filter 'Category=LiveLmStudioMutation'
Remove-Item Env:CMM_RUN_LIVE_LM_MUTATION
```

恢复事务记录写入 `%LOCALAPPDATA%\CodexModelManager\transactions`。若测试进程在重载窗口异常终止，先启动管理器并完成恢复对话框，不要直接重复运行。

恢复与正常 load 都把 `/api/v1/models` 的源 `key` 发送为 `model`；`selected_variant` 只用于 Q8/Q6 等精确变体的前后验证。LM Studio 页的 **检查/恢复未完成事务** 会先执行只读评估并显示是否真的需要 unload/load，legacy journal 已经处于原始状态时可以零重载关闭。

### 只读 GGUF Prompt Template Live Test

指定一个实际 GGUF 后，只读取 metadata header、校验 `tokenizer.chat_template` SHA、在 `%TEMP%` 导出修补工件并立即清理；不会读取 tensor、修改 GGUF 或修改 LM Studio：

```powershell
$env:CMM_LIVE_GGUF_PATH='J:\path\to\model.gguf'
$env:CMM_LIVE_GGUF_TEMPLATE_SHA='optional-expected-template-sha256'
dotnet test .\tests\CodexModelManager.Tests\CodexModelManager.Tests.csproj `
  --filter 'Category=LiveGguf'
Remove-Item Env:CMM_LIVE_GGUF_PATH
Remove-Item Env:CMM_LIVE_GGUF_TEMPLATE_SHA -ErrorAction SilentlyContinue
```

`CMM_LIVE_GGUF_TEMPLATE_SHA` 只是让测试确认“本次读取的实物仍是预期版本”，不会进入修补器的业务放行逻辑。最终审计已分别对本机 Qwen3.8 Q6_K、Q8_0 和 Qwen3.6 Q4_K_M 执行此只读测试；三个实物都能按 `qwen-interleaved-instructions-v3` 精确结构规则导出到临时目录并通过 manifest/hash 校验。旧 `qwen-leading-instructions-v2` 仅作为已完成事务的精确 provenance、v2→v3 预览和确定性回滚格式保留。

### 隔离 Codex Agent Level 3

```powershell
$env:CMM_RUN_LIVE_CODEX='1'
dotnet test .\tests\CodexModelManager.Tests\CodexModelManager.Tests.csproj `
  --filter 'Category=LiveCodexSmoke'
Remove-Item Env:CMM_RUN_LIVE_CODEX
```

该测试使用临时 `CODEX_HOME`/workspace，默认最长 5 分钟。失败会保留临时目录用于诊断，但不会输出 HTTP body 或 Token。必须先让新的 Codex instruction hierarchy preflight PASS；未修补的已知 Qwen system-order 模板会在进入 L3 前被阻止。

## 格式与静态检查

```powershell
dotnet format .\CodexModelManager.sln --verify-no-changes --no-restore
git diff --check
git status --short --untracked-files=all
```

项目启用 nullable、latest recommended analyzers 和 warnings-as-errors。

## Self-contained win-x64 发布

```powershell
.\publish.ps1
```

跳过普通单元测试（仅用于已单独验证后的快速重发）：

```powershell
.\publish.ps1 -SkipTests
```

脚本会：

1. restore `win-x64` runtime assets；
2. Release build；
3. 运行 Core 与 App 两个测试项目的非 live 单元测试；
4. 分别发布主 WinForms、Credential Helper 与 Test MCP Helper；
5. 将 Helper 放入主发布目录的 `helpers\`；
6. 生成 self-contained、single-file、`PublishTrimmed=false` 产物。

最终入口（相对仓库根目录）：

`artifacts\publish\win-x64\CodexModelManager.exe`

发布脚本只允许递归清理工程 `artifacts` 下已验证的 staging/目标路径。
