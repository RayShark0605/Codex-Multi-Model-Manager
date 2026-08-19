# Codex Multi-Model Manager

一个面向 Windows 的 .NET 8 / WinForms 小工具，用来**安全、可逆、可预览**地切换 Codex Desktop 实际使用的 OpenAI、DeepSeek 与 LM Studio 模型。

> 设计原则：真实 `config.toml` 始终是 Source of Truth；管理器只精确修改登记过的模型相关键，不接管整个 `.codex` 目录。

## 当前交付状态

- OpenAI：从 Codex App Server 动态获取账户可见模型；App Server 不可用时才读取并标记可能过期的 `models_cache.json`。
- DeepSeek：识别官方 `models.json`；否则下载并只解析官方 PowerShell setup script，离线时使用随发行版附带、带来源哈希的官方 catalog 快照。
- LM Studio：优先调用 `/api/v1/models`，回退 `/api/v0/models`、`/v1/models`；一条 loaded instance 对应一个可查看项。native `type` 会被保留，embedding 等已知非 LLM instance 不会进入 Codex 切换列表，核心层也会再次拒绝。
- 配置安全：TOML 精确文本补丁、语法/语义校验、预览指纹、命名同步锁、同目录临时文件、flush、原子替换、重读校验和自动回滚。
- 可逆备份：不可覆盖的 Initial Snapshot、显式修改外部 override 文件前的 supplemental baseline、每次切换/恢复前的 History、SHA-256 manifest。
- 凭据：新 Token 默认进入 Windows Credential Manager；Codex 通过 command-backed auth Helper 从 stdout 获取，不进入 TOML、日志或 manifest。
- 本地兼容性硬门槛：切换 LM Studio 前实时执行 `instructions + user` 与 `instructions + developer + user` 差分请求；不兼容时在备份和写盘前阻止切换。
- Prompt Template 修复：只读解析所选 GGUF 的 `tokenizer.chat_template`，仅对精确匹配的 Qwen system-order 结构导出最小修补；不修改 GGUF 或 LM Studio 内部配置。
- 测试：Codex-shaped Level 1/2 Responses/SSE/function calling；用户主动触发的 Level 3 真实 Codex CLI 临时工作区测试。

## 快速使用

1. **完全关闭 Codex Desktop/ChatGPT Desktop 及其 Codex 子进程。**管理器检测到相关进程时会禁止写入。
2. 运行（路径相对仓库根目录，由 `publish.ps1` 生成）：

   `artifacts\publish\win-x64\CodexModelManager.exe`

3. 首次启动会只创建一次 **Initial Snapshot**。
4. 若使用 DeepSeek，先在“设置与日志”页将 Token 保存到 Windows Credential Manager。LM Studio 返回 401 时同样保存 LM Token。
5. 选择 Provider 与 Model；LM Studio 页确认 `Loaded Context` 与 `Codex Configured Context` 一致。
6. 对 LM Studio 点击 **重新检测 Codex 指令层级**。只有普通 control 与 Codex-shaped 请求都 PASS 才能切换。
7. 若显示 **Template Fix Required**，选择当前 loaded instance 对应的 GGUF，点击 **分析 Prompt Template** 与 **导出兼容模板**，按生成的 `APPLY.md` 在 LM Studio 中手动应用、手动重载，再重新检测。
8. 点击 **Preview Changes**，检查 semantic diff、Secondary Overrides 和警告。Secondary 列表默认全部不勾选；只有明确勾选的项才会在 `FollowMain`/`RestoreOriginal` 策略下修改。
9. 点击 **Switch Model** 并确认。提交前会再次实时验证指令层级；完成后重新启动 Codex Desktop。

### 为什么必须先关闭 Codex

Codex 可能在运行期间缓存配置、更新模型 cache 或自行写回 `config.toml`。同时写入会造成“最后写入者覆盖”或让当前进程继续使用旧状态。管理器虽然有 SHA-256/长度/时间戳的外部修改门槛和原子替换，但关闭 Codex 能从源头消除竞争。因此首版不做热注入，也不自动杀进程。

## 三类 Provider

### OpenAI / Codex 原生

- 模型不是写死列表，优先通过 App Server `model/list` 与 provider capabilities 动态发现；因此以后新增可用模型通常不需要改 UI。
- 切回 OpenAI 会恢复管理器先前捕获的 OpenAI provider-specific state；若从未捕获过，则采用保守最小配置并明确警告。
- 不读取、不删除 `auth.json`，不登出 ChatGPT/Codex，不操作 Credential Manager 中的 OpenAI 登录项。
- 会清理由本工具拥有的 DeepSeek/LM Studio custom provider、catalog/context/compaction 冲突项；不会定义或覆盖保留的 `[model_providers.openai]`。若 DeepSeek table 含官方脚本的 `experimental_bearer_token`，切回 OpenAI/Local 时会把整段原文留作 dormant provider 配置，当前路由仍由 `model_provider` 决定。

### DeepSeek

- 兼容已有官方 setup script 环境：若发现包含 `experimental_bearer_token` 的官方 provider table，会原样继续使用，不复制、不迁移、不显示明文 Token。
- 新配置使用当前 Codex 的 `[model_providers.deepseek.auth]` command-backed auth，不再新增已移除的 `preferred_auth_method`。
- `minimal_client_version`、reasoning levels、context 与工具 metadata 均从官方 catalog 解析；Codex CLI 版本不足时直接阻止切换。
- 官方 `backup-deepseek` 仅显示路径、文件名、时间/大小与短哈希；本工具绝不删除、移动、重命名、覆盖，也不展示其可能含 Token 的文件内容。
- 在线 Validate/Level 3 会产生少量 DeepSeek API 调用，UI 会先请求确认。

### LM Studio

- `lms server status` 用于窄范围发现当前端口；否则使用已保存 endpoint 或默认 `127.0.0.1:1234`，不进行广泛端口扫描。
- 默认 1234、无认证、无 `CODEX_OSS_*` 重定向时使用 Codex 内置 `lmstudio`，绝不创建 `[model_providers.lmstudio]`。
- 非默认端口或启用认证时使用不冲突的 `lmstudio_local_cmm` custom provider。
- 非 loopback endpoint 必须是 HTTPS；401 会提示 Token，Token 输入框使用系统密码字符。
- 首版只发现 loaded/unloaded 状态，不会自动 load/unload，不会改变 GPU offload、KV cache、context 或量化参数。
- LM Studio 仅报告 reasoning `on/off` 时，不会把它猜成 Codex 的 `low/medium/high`；此时本地配置明确删除 `model_reasoning_effort`。只有 Provider 返回值与 Codex 支持 effort 的精确交集才允许写入。

### Codex Instruction Hierarchy 与 Qwen Prompt Template

普通 `/v1/responses` 返回 200 不能证明 Codex Agent 可用。Codex 会发送自己的 base instructions，并把工作区、工具和运行模式约束作为独立的 `developer` 消息。LM Studio 的 Responses adapter 可能把二者交给模型模板作为多条 system 消息；部分 Qwen GGUF 模板包含：

```text
System message must be at the beginning.
```

这类模板会让“普通请求 PASS、Codex 第一句话立即 500”。管理器因此执行两次只改变 developer 消息的差分预检：

```text
Control:       instructions + user
Codex-shaped:  instructions + developer + user
```

任一正式 LM Studio Preview 与 Commit 都必须实时通过第二种结构；每次还会先从 native Models API 重新确认同一 loaded instance 与相同实际 context。instance 缺失/context 改变时不会发送可能触发后端自动加载的推理请求。成功结果不会被长期缓存，也没有绕过按钮；所有失败都发生在 History backup 和 `config.toml` 写入之前（Initial Snapshot 仍只按首次启动规则管理）。

如果错误被分类为 `lmstudio-chat-template-system-order` 或 `lmstudio-chat-template-developer-role`，LM Studio 页可只读分析对应 GGUF。修补器不会套用通用 Qwen/GPT 模板，而是要求宏、system/tool 初始区、主 message 循环和拒绝分支全部精确匹配。生成模板只把开头连续的 system/developer 指令按原顺序合并为一个初始 system block；后置 system/developer 仍被拒绝，assistant/tool/thinking/vision 文本保持不变。

导出目录：

```text
%LOCALAPPDATA%\CodexModelManager\template-fixes\<model-id>\<timestamp>\
  original-chat-template.jinja
  codex-compatible-chat-template.jinja
  manifest.json
  APPLY.md
```

用户必须在 LM Studio 的 **My Models → 模型设置 → Prompt Template** 中手动启用 override、粘贴完整兼容模板并保存，然后手动卸载/重载模型。只有重载后的真实差分检测才算 PASS。撤销时删除/禁用该 per-model override 并再次重载；原始 GGUF 从未被修改。官方入口参见 [LM Studio Prompt Template](https://www.lmstudio.ai/docs/app/advanced/prompt-template) 与 [Per-model Defaults](https://lmstudio.ai/docs/app/advanced/per-model)。

## Local Context、Max Context 与 Auto Compact

`max_context_length` 是模型理论上限；`loaded_instances[].config.context_length` 才是当前 LM Studio 实例真正分配的上下文。管理器强制：

```text
model_context_window = actual loaded context
0 < model_auto_compact_token_limit < model_context_window
```

默认 compact 建议值为：

```text
min(floor(loadedContext * 0.90), loadedContext - 8192)
```

它是**管理器安全建议值**，不是 Codex 官方百分比标准。每个 local model 的 loaded context 与 compact 偏好保存在管理器自己的 `appsettings.json`；如果实际 loaded context 改变，旧偏好不会被盲目套用。

最终只读审计时，`/api/v1/models` 返回 16 个模型但没有任何 `loaded_instances`；与此同时 `lms ps` 显示 `qwen/qwen3.8-27b@q6_k` 为 `IDLE`、context `131072`。这两个表面互相矛盾的状态不会被管理器合并或猜测：安全切换只信任 native API 的 `loaded_instances[].config.context_length`，所以当前会阻止 LM Studio Preview/Switch，并要求用户在 LM Studio 中确认模型已为 Server 实例实际加载后重新刷新。`lms ps` 的 context、模型理论 Max 或旧缓存都不会被写进 Codex。

本机只读 GGUF 检查还确认了两个不同的源模板结构：Qwen3.6 的模板 SHA-256 为 `E84F32A23FDDA27689F868AA4A1A5621F41133E51A48D7F3EFCBEA2839574259`，两个已检查的 Qwen3.8-27B Q6_K/Q8_0 文件共享 `C3CF9E34ABF4F9E36C2D72165AA9C132D3E2A725B6C2586AAA3A8AF9D7A81041`。后者额外保留 `reasoning_instructions`。修补规则 `qwen-leading-instructions-v2` 不是按模型名或 SHA 白名单放行，而是分别要求两个已支持结构中的每个锚点精确且唯一匹配；未知第三种结构仍会显示 `Unsupported Template`。

## 管理器会修改什么

受管根键集中登记在兼容层：

- `model`
- `model_provider`
- `model_catalog_json`
- `model_context_window`
- `model_auto_compact_token_limit`
- `model_auto_compact_token_limit_scope`（Local 建议值按当前官方 `total` 语义计算；切回 OpenAI 时恢复原状态）
- `model_reasoning_effort`
- `preferred_auth_method`（只用于识别/清理 legacy 冲突）
- `forced_login_method`
- `openai_base_url`

受管 table 仅包括：

- `[model_providers.deepseek]` 及其 auth 子 table
- `[model_providers.lmstudio_local_cmm]`

外部工具或用户创建的 `[model_providers.lmstudio_local]` 不属于本工具，会原样保留。

未登记键会被拒绝修改。引擎用 Tomlyn 严格验证，但不把整个 TOML 重新序列化；实际修改是倒序 source-span patch，从而保留编码、BOM、LF/CRLF、尾换行、注释、顺序和未知 section。

## 管理器绝不会修改什么

- `auth.json`、ChatGPT 登录态、Cookie、OpenAI Credential、Windows 系统级环境变量。
- Thread、Project、Goal、session、history、memory 等 `.codex` 数据库或目录。
- 用户 MCP、Project Trust、sandbox、approval、permissions、hooks、plugins、skills、notifications、Computer Use 等无关配置。
- LM Studio 模型生命周期与 GPU/KV/context 设置。
- DeepSeek 官方 `backup-deepseek`。
- 整个 `.codex` 目录；恢复只处理快照 manifest 明确登记的 `config.toml`、`models.json` 以及用户曾明确选择由管理器修改的外部 TOML override 文件，绝不回滚 Thread/Project/session 数据。

## Preview、提交事务和外部修改

Preview 只在内存中生成 semantic diff，不创建 History，也不写 Codex 配置。提交时：

1. 再检测 Codex 进程、Provider、模型、版本、context、metadata 与 Secondary Overrides。
2. 比较 `LastWriteTimeUtc + length + SHA-256`；预览后有任何外部变化即停止。
3. 对 LM Studio 再执行一次 Codex instruction hierarchy preflight；随后从真实文件重新生成计划并比较 plan hash。任何失败都发生在创建 History 之前。
4. 对首次被明确选择的外部 override TOML 创建不可覆盖的 supplemental baseline，再创建包含本次所有目标文件的 History 快照。
5. 写同目录 `CreateNew` 临时文件，`Flush(true)`，验证 TOML/JSON 与 SHA；本工具的 provider/override state 也作为同一批次中的 `appsettings.json` 候选写入。
6. 依赖文件与 appsettings 先提交，主 `CODEX_HOME\config.toml` 明确标记为最后提交；现有文件用 `File.Replace`，新文件用同卷 `File.Move`。
7. 重读和再验证；失败时按相反顺序回滚。若极端情况下回滚也失败，会保留 rollback 文件并要求从 History 恢复。

两个管理器实例通过命名 semaphore 串行化写入；正式替换期间还会对已存在目标文件持有允许原子 rename、但拒绝并发 writer 的短期锁。只读、锁定、磁盘空间不足或并发改动都不会覆盖原文件。

## Initial Snapshot、History 与 DeepSeek 官方备份

目录：`%USERPROFILE%\.codex\model-switcher-backup\`

- `initial\`：第一次启动时的真实状态，UI 名称为 **Initial Snapshot**；记录文件当时“存在”或“缺失”，创建后不自动覆盖。它不被错误命名成“OpenAI 原始环境”。
- `supplemental-baseline\<path-sha256>\`：某个外部 agent/profile/project TOML 第一次被用户明确选择修改时保存的不可覆盖原始状态；不包含未勾选文件。
- `history\yyyyMMdd-HHmmssfff\`：每次真实切换以及每次恢复前的当前状态；若事务包含已勾选的外部 override 文件，它们也进入同一个快照。manifest 含操作、前后 provider/model、版本、编码、换行、长度和 SHA-256，不含 Token。
- `backup-deepseek\`：DeepSeek 官方脚本自己的目录，与本工具完全独立。

“恢复上一次”“恢复所选”“恢复 Initial Snapshot”都会先备份当前状态，因此恢复本身也可逆。恢复 Initial 时还会恢复全部 supplemental baseline；SHA、原始路径标识或 TOML 校验失败即拒绝恢复。`backup-deepseek` 永远不参与这些操作。

## Secondary Model Overrides 与隐藏云调用

管理器扫描主配置、profile/project 配置及引用的 agent config，包括：

- `review_model`
- `agents.default_subagent_model`
- agent/profile 引用配置
- memory extract/consolidation model
- 未来疑似 `*_model` 键

Local 主模型不代表这些覆盖一定跟随 Local。默认策略是 **Preserve** 并提示可能的云调用；Secondary 列表同样默认不勾选。选择 `FollowMain` 时，管理器只对用户明确勾选且扫描器确认可编辑的键做精确文本补丁，并把主配置或外部 agent/profile/project TOML 纳入同一事务、History 和外部修改检测；首次触及外部文件前先创建 supplemental baseline。`RestoreOriginal` 可逐项恢复精确原值及原始 TOML 引号形式；未勾选项始终保持原样。

## Agent、Plan、Goal、MCP 和高级能力边界

“Responses API 可访问”不等于“全部 Codex 能力支持”。状态含义：

- `Supported`：当前测试直接证明。
- `Likely Supported`：架构/metadata 有依据，但没有端到端证明。
- `Untested`：没有足够证据。
- `Known Limitation`：存在已知 provider/backend 风险。
- `Failed`：当前测试已复现失败。

Project/Thread 多数属于 Codex App 层，切换器不会清理它们。Plan/Goal/MCP/Skills 还依赖模型 metadata、工具调用格式与后端实现；管理器不会为了让警告消失而复制 GPT/DeepSeek metadata 给 Qwen。

Level 1/2 先执行普通 control 与 Codex-shaped 指令层级差分；只有层级 PASS 才继续测试带相同 instructions/developer/user 结构的 SSE streaming、reasoning artifact 和严格 dummy function call。Level 3 在 `%TEMP%\CodexModelManager\smoke\<guid>` 中创建临时 `CODEX_HOME` 和 workspace，使用 `workspace-write`、`approval_policy=never`，测试读取、PowerShell shell、结构化 `apply_patch/file_change` 事件与临时 `cmm_ping` MCP；不复制 `auth.json`，不使用危险 bypass。

截至 2026-08-18，失败会话和独立差分请求均证明当前测试过的 Qwen 模板是“普通 control 通过，但加入 developer 后触发 system-order 500”。管理器现在会在真实配置写入前准确分类并阻止，不再把简单 Responses PASS 误报成 Codex Agent 可用。模板修补后的状态必须由用户应用、重载并重新实测，不能仅凭成功导出升级为 Supported。详见 [`docs/KNOWN-LIMITATIONS.md`](docs/KNOWN-LIMITATIONS.md)。

## 凭据、日志与目录

管理器自身目录：`%LOCALAPPDATA%\CodexModelManager\`

```text
appsettings.json       非敏感 UI/provider state、per-model context 偏好
catalogs\              官方 DeepSeek script/catalog 缓存与来源元数据
bin\credential\       稳定 Credential Helper
bin\mcp\              临时 MCP test helper
logs\                  脱敏日志
temp\                  管理器临时文件
template-fixes\        原模板、兼容模板、哈希 manifest 与手动应用说明
```

Windows Credential Manager target：

- `CodexModelManager/DeepSeek`
- `CodexModelManager/LMStudio`

统一 redactor 覆盖已注册 secret、Bearer/API key 形态、Authorization header 与 URL query。日志不记录请求正文、完整 TOML、Cookie、auth.json、Token 或 credential 内容。

## 完全恢复到安装管理器以前

1. 完全关闭 Codex。
2. 在“备份历史”页选择 **恢复 Initial Snapshot**；确认后当前状态会先进入 History，主 `config.toml`/`models.json` 与所有曾由管理器明确修改过的 supplemental TOML 会一起恢复到各自首次触及时的状态。
3. 重新启动 Codex，确认模型、MCP 与 Project/Trust 均正常。
4. 如果曾在 LM Studio 手动应用兼容 Prompt Template，还应在对应模型设置中禁用/删除 per-model override 并手动重载；Codex Initial Snapshot 不管理 LM Studio 的独立设置。
5. 如要卸载应用，可删除 `%LOCALAPPDATA%\CodexModelManager`，并在 Windows“凭据管理器”中删除上述两个 `CodexModelManager/*` Generic Credential。
6. 建议先保留 `~\.codex\model-switcher-backup` 一段时间；确认无误后再由用户自行归档。不要删除或改动 `backup-deepseek`。

## 构建与测试

构建要求：

- Windows 10/11 x64
- .NET SDK `9.0.316`（由 `global.json` 固定）；产品项目目标框架为 .NET 8
- PowerShell 7 或 Windows PowerShell 5.1

详细构建、测试与发布步骤见 [`BUILD.md`](BUILD.md)。快速命令：

```powershell
dotnet test .\tests\CodexModelManager.Tests\CodexModelManager.Tests.csproj
.\publish.ps1
```

发布输出：`artifacts\publish\win-x64`（相对仓库根目录）

## 项目结构

```text
CodexModelManager.sln                  解决方案
Directory.Build.props                  共享构建属性（nullable、分析器、warnings-as-errors）
Directory.Packages.props               集中包版本管理
global.json                            .NET SDK 版本固定
NuGet.Config                           NuGet 源与 restore 目录配置
publish.ps1                            self-contained win-x64 发布脚本
src/
  CodexModelManager.App/               WinForms 主程序
  CodexModelManager.Core/              兼容层、精确 TOML 补丁、备份与切换事务
  CodexModelManager.CredentialHelper/  command-backed auth Helper
  CodexModelManager.TestMcpServer/     临时 MCP 测试 Helper
tests/
  CodexModelManager.Tests/             xUnit 测试（非 live + opt-in live）
docs/                                  兼容性、验证与环境审计文档
artifacts/                             构建/发布输出（默认不入库）
```

## 官方依据

- [OpenAI Codex Configuration Reference](https://developers.openai.com/codex/config-reference)
- [OpenAI Codex Advanced Configuration](https://developers.openai.com/codex/config-advanced)
- [Codex App Server](https://developers.openai.com/codex/app-server)
- [Codex model provider source](https://github.com/openai/codex/blob/main/codex-rs/model-provider-info/src/lib.rs)
- [DeepSeek Integrate with Codex](https://api-docs.deepseek.com/quick_start/agent_integrations/codex)
- [DeepSeek official Windows setup script](https://cdn.deepseek.com/api-docs/codex-deepseek-setup-en.ps1)
- [LM Studio Codex integration](https://lmstudio.ai/docs/integrations/codex)
- [LM Studio `/api/v1/models`](https://lmstudio.ai/docs/developer/rest/list)
- [LM Studio OpenAI-compatible endpoints](https://lmstudio.ai/docs/developer/openai-compat)
- [LM Studio Authentication](https://lmstudio.ai/docs/developer/core/authentication)

调查差异、来源哈希与已知 issue 见 [`docs/OFFICIAL-COMPATIBILITY-NOTES.md`](docs/OFFICIAL-COMPATIBILITY-NOTES.md)；最终构建、测试、发布哈希与隔离 smoke 记录见 [`docs/VERIFICATION.md`](docs/VERIFICATION.md)。

## 许可

本项目基于 [MIT 许可证](LICENSE) 开源。
