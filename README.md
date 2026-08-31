# Codex Multi-Model Manager

一个面向 Windows 的 .NET 8 / WinForms 小工具，用来**安全、可逆、可预览**地切换 Codex Desktop 实际使用的 OpenAI、DeepSeek 与 LM Studio 模型。

> 设计原则：真实 `config.toml` 始终是 Source of Truth；管理器只精确修改登记过的模型相关键，不接管整个 `.codex` 目录。

## 当前交付状态

- OpenAI：从 Codex App Server 动态获取账户可见模型；App Server 不可用时才读取并标记可能过期的 `models_cache.json`。
- DeepSeek：识别官方 `models.json`；否则下载并只解析官方 PowerShell setup script，离线时使用随发行版附带、带来源哈希的官方 catalog 快照。
- LM Studio：优先调用 `/api/v1/models`，回退 `/api/v0/models`、`/v1/models`；一条 loaded instance 对应一个可查看项。native `type` 会被保留，embedding 等已知非 LLM instance 不会进入 Codex 切换列表，核心层也会再次拒绝。GGUF 自动定位保留 Hub `lms ls --json --variants`，并新增 endpoint-aware `lms ps --json --host/--port` loaded-instance 证据；native loaded state 始终是权威面。
- 配置安全：TOML 精确文本补丁、语法/语义校验、预览指纹、命名同步锁、同目录临时文件、flush、原子替换、重读校验和自动回滚。
- 可逆备份：不可覆盖的 Initial Snapshot、显式修改外部 override 文件前的 supplemental baseline、每次切换/恢复前的 History、SHA-256 manifest。
- 凭据：新 Token 默认进入 Windows Credential Manager；Codex 通过 command-backed auth Helper 从 stdout 获取，不进入 TOML、日志或 manifest。
- 本地兼容性硬门槛：切换 LM Studio 前实时执行 Basic、Leading Developer、Conversation Control、Continuation Developer 四阶段差分请求；四项未全部返回 HTTP 200 且含 `output` 数组时，在备份和写盘前阻止切换。
- Prompt Template 修复：只读解析精确 loaded instance 的 GGUF，仅对结构精确匹配的 Qwen 指令层级失败提供可预览的 `qwen-interleaved-instructions-v3` Jinja 修补；现已覆盖当前 Unsloth 184 行 prefix-merged-system 模板族。在本机 loopback LM Studio `0.4.21.x` 与 `0.4.23.x` 上，确认后会事务式写入该 concrete GGUF 的 per-model Prompt Template default，再以**不含 REST `prompt_template`** 的请求 unload/load、复核全部可观察加载参数并执行四阶段探针。它不修改 GGUF；已验证的旧 v2 可安全升级，任何失败都会先恢复持久 defaults，再确定性恢复原运行时，手工导出仍作为回退路径。
- 测试：Codex-shaped Level 1/2 Responses/SSE/function calling；用户主动触发的 Level 3 真实 Codex CLI 临时工作区测试。
- 2026-08-24 审阅修复：已完成配置补丁、崩溃可恢复跨进程门控、设置隔离恢复、Provider/JSON/进程边界与 WinForms 生命周期的一轮系统性加固；逐项判定、运行时证据与测试映射见 [`docs/REMEDIATION-2026-08-24.md`](docs/REMEDIATION-2026-08-24.md)。

## 快速使用

1. **完全关闭 Codex Desktop/ChatGPT Desktop 及其 Codex 子进程。**管理器检测到相关进程时会禁止写入。
2. 运行（路径相对仓库根目录，由 `publish.ps1` 生成）：

   `artifacts\publish\win-x64\CodexModelManager.exe`

3. 首次启动会只创建一次 **Initial Snapshot**。
4. 若使用 DeepSeek，先在“设置与日志”页将 Token 保存到 Windows Credential Manager。LM Studio 返回 401 时同样保存 LM Token。
5. 选择 Provider 与 Model；LM Studio 页确认 `Loaded Context` 与 `Codex Configured Context` 一致。
6. 对 LM Studio 点击 **重新检测 Codex 指令层级**。只有 Basic、Leading、Conversation、Continuation 四项都 PASS 才能切换。
7. 若显示 **Template Fix Required** 或 **Template Upgrade Required (v2 → v3)**，可先点击 **Preview Changes** 查看当前运行时来源、GGUF 定位证据（`lms ps --json` 或 `lms ls --json --variants`）、精确 per-model defaults 路径、原/候选文件 SHA、Prompt Template 的 Add/Upgrade/No-op 语义、原始/v2/v3 模板 SHA、目标模板和完整加载配置；Preview 全程只读。点击 **Switch Model** 后会再次明确说明将修改该模型的 LM Studio 默认 Prompt Template 并执行一次 unload/reload。若 concrete identity、v2 provenance、defaults 结构或 LM Studio 版本不满足门槛，稳定诊断会保留 **分析/导出兼容模板** 和 LM Studio My Models 手工设置流程。“对应 GGUF”为空或当前选择不是已加载 LLM 时，**分析 Prompt Template** 保持禁用；底层入口也会返回可操作的中文校验错误，不再暴露 `ArgumentException(filePath)`。
8. 点击 **Preview Changes**，检查 semantic diff、Secondary Overrides 和警告。Secondary 列表默认全部不勾选；只有明确勾选的项才会在 `FollowMain`/`RestoreOriginal` 策略下修改。
9. **先完全退出 Codex Desktop/CLI，再单独启动管理器**，随后点击 **Switch Model** 并确认。当前 Codex 仍在运行时不要尝试真实切换；提交前会再次实时验证进程状态与指令层级，完成后再重新启动 Codex Desktop。

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
- 常规发现、刷新、兼容性测试与 Preview 不改变模型生命周期或任何 defaults 文件。自动 GGUF 定位把 `lms ps.identifier` 固定解释为 loaded instance ID，必须严格等于 native loaded ID；`modelKey` 必须非空，且只能等于 loaded ID（旧 CLI/旧加载形态）或规范化后的 native source/load key（当前重载形态）。publisher/source、type、format、architecture、context 同样严格匹配；quantization 按“双方都缺失或双方非空且相等”精确比较，单边缺失或值冲突仍会阻断。当 native source 是完整 `.gguf` 相对路径时，所有存在的 `path`/`indexedModelIdentifier` 都必须与其规范化后相等，且至少存在一个；普通 Hub source 继续使用 publisher/source 规则，并避免对已经完全限定的 `publisher/model` 重复拼接 publisher。最终路径必须唯一、真实、为 `.gguf` 且位于配置的 downloads/models 根目录；不从显示名、用户手选文件或文件名猜测 concrete identity/`NVFP4`，也不把进程 size 与单个文件长度强行等同。两数据面冲突、非法 JSON、CLI 失败/超时、歧义或非本机 endpoint 均 fail closed 并保留只读手工选择。只有三个已识别的模板失败码（含 v2 后置 developer 顺序错误）、精确 GGUF 和 concrete identity 均可证明且用户确认后，Switch 流程才修改 defaults 并调用原生 `/api/v1/models/unload` 与 `/load`；请求保留 native API 捕获的 context、batch、parallel、flash attention、KV cache、speculative decoding 等可观察参数，并在不一致时回滚。发现、刷新、Preview 和普通 Provider 请求仍使用 3 分钟客户端预算，四阶段推理各自使用 45 秒预算；仅精确 unload/load、状态复核和恢复使用独立 30 分钟生命周期客户端，自动回滚也有独立 30 分钟预算，因此大型模型加载不会在 3/5 分钟旧门槛处被误取消。
- LM Studio 仅报告 reasoning `on/off` 时，不会把它猜成 Codex 的 `low/medium/high`；此时本地配置明确删除 `model_reasoning_effort`。只有 Provider 返回值与 Codex 支持 effort 的精确交集才允许写入。

### Codex Instruction Hierarchy 与 Qwen Prompt Template

普通 `/v1/responses` 返回 200 不能证明 Codex Agent 可用。Codex 会发送自己的 base instructions，并把工作区、工具和运行模式约束作为独立的 `developer` 消息。LM Studio 的 Responses adapter 可能把二者交给模型模板作为多条 system 消息；部分 Qwen GGUF 模板包含：

```text
System message must be at the beginning.
```

这类模板会让“普通请求 PASS、Codex 第一句话立即 500”。旧 `qwen-leading-instructions-v2` 又只兼容连续前导指令：Plan Mode 能生成计划，但 Codex 在用户批准后追加 Default collaboration-mode `developer` 时会触发 `System and developer messages must precede conversation messages.`。当前 Unsloth Qwen3.8 prefix-merged-system 内置模板则先合并开头连续 system/developer，再进入第二个主循环，因此形成 Basic/Leading/Conversation 均 200、Continuation 500 且错误仍为 `System message must be at the beginning.` 的第三种精确形状；它保持 BuiltIn provenance，不与旧 v2 混淆。管理器因此执行四阶段差分预检：

```text
Basic:         instructions + user
Leading:       instructions + developer + user
Conversation:  instructions + developer + user + assistant + user
Continuation:  与 Conversation 相同，仅在最后一个 user 前增加 developer
```

任一正式 LM Studio Preview 与 Commit 都必须实时通过四种结构；步骤 3/4 只改变后置 developer，因此普通多轮失败不会被误归类成模板升级问题。每次还会先从 native Models API 重新确认同一 loaded instance 与相同实际 context。instance 缺失/context 改变时不会发送可能触发后端自动加载的推理请求。成功结果不会被长期缓存，也没有绕过按钮；所有失败都发生在 History backup 和 `config.toml` 写入之前（Initial Snapshot 仍只按首次启动规则管理）。

如果错误被分类为 `lmstudio-chat-template-system-order`、`lmstudio-chat-template-developer-role` 或 `lmstudio-chat-template-continuation-instruction-order`，LM Studio 页可只读分析对应 GGUF。修补器不会套用通用 Qwen/GPT 模板，而是要求宏、system/tool 初始区、反向扫描、主 message 循环、vision/reasoning/tool-call/generation 分支和拒绝路径全部精确且唯一匹配。当前 Unsloth 模板族还必须逐字匹配 `sysns.count == loop.index0` 前缀聚合、`merged_system` 两个输出区和 `loop.index0 >= num_sys` 保护；任何 one-change near-match、重复锚点或混合换行都返回 Unsupported。v3 遍历完整 `messages`，按原始相对顺序收集任意位置的 system/developer，使用双换行合并为唯一初始 system block；`reasoning_instructions`、tools、反向扫描和其他非目标分支逐字保持，主 conversation 循环跳过所有已合并项。

导出目录：

```text
%LOCALAPPDATA%\CodexModelManager\template-fixes\<model-id>\<timestamp>\
  original-chat-template.jinja
  codex-compatible-chat-template.jinja
  manifest.json
  APPLY.md
```

自动路径仅在本机 loopback LM Studio `0.4.21.x` 与 `0.4.23.x` 上启用持久化；`0.4.22.x`、`0.4.24.x` 及其他未核验版本继续 fail closed。它使用经 locator 严格证明的 concrete model identifier，把目标字段写入 `%USERPROFILE%\.lmstudio\.internal\user-concrete-model-default-config\<publisher>\...\<file.gguf>.json` 的 `load.fields`；路径穿越、绝对/UNC 标识、root 外路径、现有 reparse point/junction、未知 JSON 结构、重复字段或用户自定义 Prompt Template 全部在任何写入和 unload 前阻断。目标 `/api/v1/models/load` 请求**不包含**顶层 REST `prompt_template`，从而由无运行时覆盖的重载与四阶段探针证明模板确实来自 per-model default。除 `llm.load.promptTemplate` 外，preset、operation/load 参数及未知 JSON 属性保持语义不变；GGUF 始终只读。官方说明 per-model defaults 会用于模型的后续加载，并支持模型级 Prompt Template 覆盖，参见 [Per-model Defaults](https://lmstudio.ai/docs/app/advanced/per-model) 与 [Prompt Template](https://lmstudio.ai/docs/app/advanced/prompt-template)；LM Studio `0.4.23` 的发布记录参见 [0.4.23 changelog](https://lmstudio.ai/changelog/lmstudio/lmstudio-v0.4.23)，模型管理端点参见 [List](https://lmstudio.ai/docs/developer/rest/list)、[Load](https://lmstudio.ai/docs/developer/rest/load) 与 [Unload](https://lmstudio.ai/docs/developer/rest/unload)。未知版本或自动路径被阻断时，仍可在 **My Models → 模型设置 → Prompt Template** 中手工应用导出的模板。

`/load` 的 `model` 始终使用 native list 返回的源模型 `key`（例如 `qwen/qwen3.8-27b`）；`selected_variant`（例如 `...@q8_0`）只用于精确 GGUF 定位、并发指纹和加载后量化校验。旧实现曾把 variant 字符串当成 load ID，LM Studio 0.4.21 会返回 `404 model_not_found`；新版禁止混用 source key、selected variant 与 instance ID，也不预测 `:2` 之类的新实例后缀。

正式持久修复在 `%LOCALAPPDATA%\CodexModelManager\transactions` 中先写不含模板正文、完整 defaults 正文和 Token 的 schema-v4 恢复记录；它额外保存 concrete identity、defaults 路径与前后 SHA、原字段状态、目标规则/SHA、CurrentUser DPAPI 加密备份路径和持久化稳定阶段。备份必须完成写盘、解密和 SHA 校验后才允许原子修改 defaults；写入后再次确认 native instance、GGUF、concrete identity 和 defaults 未漂移，才会 unload。load、配置回显、四阶段、持久字段复核、Codex Commit 或最终完成标记任一步失败/取消，都会先恢复 defaults，再卸载可唯一归因的补丁实例并恢复原运行时。若整个 defaults 仍等于管理器候选则精确恢复原始字节；若只有无关字段并发变化则只恢复管理器拥有的 Prompt Template 并保留其他变化；若该字段被外部改成未知内容则进入 `RecoveryBlocked`，绝不覆盖或继续生命周期操作。schema-v1–v3 继续按原语义读取，旧 schema-v3 `Completed` 只证明当时的 runtime-only patch，不再作为重启后的持久证明。崩溃恢复的只读评估会同时指纹化当前 instance 和 defaults；只有持久状态处理完毕、原运行时签名复现后才关闭 journal。

## Local Context、Max Context、Auto Compact 与 Tool Output Limit

`max_context_length` 是模型理论上限；`loaded_instances[].config.context_length` 才是当前 LM Studio 实例真正分配的上下文。管理器保持真实窗口，不把 Codex 的说明性有效窗口伪装成服务端硬窗口，并强制：

```text
model_context_window = actual loaded context
0 < tool_output_token_limit < model_auto_compact_token_limit < model_context_window
model_context_window - model_auto_compact_token_limit >= 1024
model_auto_compact_token_limit_scope = "total"
```

平衡模式的 Auto Compact 建议值为：

```text
autoCompact = min(
    floor(loadedContext * 0.80),
    loadedContext - min(24576, floor(loadedContext / 2)))
```

这会同时保留至少 20% 的比例余量和最多 24,576 tokens 的绝对余量约束。对于 `loadedContext=120064`，建议值是 `95488`，到 LM Studio 硬窗口还剩 `24576`；Codex 对未知/本地模型按默认 95% 估算的有效窗口为 `floor(120064 × 0.95)=114060`（UI 约 `114k`），因此到该说明性边界还剩 `18572`。

单个工具结果写回历史的建议值为：

```text
toolOutputLimit = clamp(floor(loadedContext / 50), 2048, 4096)
```

极小窗口还会受 Auto Compact 的额外比例保护。对于 `120064`，写入 `tool_output_token_limit=2401`。该键只限制**单个 tool/function 输出进入 context manager 的规模**，不限制模型 reasoning、assistant 文本、正在生成的函数参数 JSON，也不是 LM Studio `/v1/responses` 的模型输出上限；它只是减缓后续历史膨胀的第二道防线。

这些数值是**管理器策略**，不是 Codex 官方固定百分比。默认选择“自动建议”；用户可切到 Manual 并保留合法自定义值。手动值高于平衡建议时只显示风险警告，不静默改写；达到/超过 loaded context 或余量小于 1,024 tokens 时仍会阻止切换。偏好 schema v2 会把同一 loaded context 下精确等于旧 `90% / 8192` 公式的值迁移为 Automatic 新建议，把其他旧值视为 Manual 原样保留；loaded context 改变后不会跨窗口复用旧手动值。

2026-08-23 本轮只读审计时，native `/api/v1/models` 报告 `qwen3.8-27b@q6_k_xl` 已加载，实际 `context_length=120064`、Max `262144`；这些是现场快照，不是代码常量。管理器在预览、卸载前、补丁加载后和 Codex Commit 前重新读取 native 状态，始终使用当时真实的 `loaded_instances[].config.context_length`，不会把 `lms ps`、模型理论 Max、截图或缓存猜作 loaded context。

本机只读 GGUF 检查确认了三个不同的源模板结构：Qwen3.6 的模板 SHA-256 为 `E84F32A23FDDA27689F868AA4A1A5621F41133E51A48D7F3EFCBEA2839574259`，对应 v3 为 `235C3E8D316D80E23827174F1A8CEF37B1E5018CF70ED8F52F2C6FB9C0E233CD`；两个较早检查的 Qwen3.8-27B Q6_K/Q8_0 文件共享源 SHA `C3CF9E34ABF4F9E36C2D72165AA9C132D3E2A725B6C2586AAA3A8AF9D7A81041`，对应 v3 为 `4AA5CC42C084FCC8235AAF0500835F4F9419A72280EA7E02D08EEE9A97807D8B`；当前 Unsloth `Qwen3.8-27B-UD-Q6_K_XL.gguf` 的 184 行 prefix-merged-system 源 SHA 为 `12827F24B742EA4E80CDC12DBCF9622227056B9F797252A3149263D4F9AAADCE`，确定性 v3 SHA 为 `9DC0DA000D1DF280BE9F6F64D314EB52879C0DF5C3C951F74105964136592F85`。v3 在主 conversation 循环中对已经合并的 system/developer 连 `render_content` 都不再调用，避免 vision 计数等隐藏副作用。所有 SHA 仅用于审计、重建与漂移检测；`qwen-interleaved-instructions-v3` 仍按各模板族完整精确结构放行，未知结构或 Marker 保守返回 `Unsupported Template`。旧 v2 只用于精确识别、升级和事务回滚。

## 管理器会修改什么

受管根键集中登记在兼容层：

- `model`
- `model_provider`
- `model_catalog_json`
- `model_context_window`
- `model_auto_compact_token_limit`
- `model_auto_compact_token_limit_scope`（Local 建议值按当前官方 `total` 语义计算；切回 OpenAI 时恢复原状态）
- `tool_output_token_limit`（Local 写入自适应值；切回 OpenAI/DeepSeek 时恢复原值或删除原先不存在的键）
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
- 未经专用模板预览与明确确认的 LM Studio 模型生命周期，以及任何主动改变 GPU/KV/context/量化的操作；受支持修复事务只按原配置重载同一源模型。
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

Level 1/2 先执行四阶段指令层级差分；只有四项 PASS 才继续测试 SSE streaming、reasoning artifact 和严格 dummy function call。Level 3 在 `%TEMP%\CodexModelManager\smoke\<guid>` 中创建临时 `CODEX_HOME` 和 workspace，使用 `workspace-write`、`approval_policy=never`，测试读取、PowerShell shell、结构化 `apply_patch/file_change` 事件与临时 `cmm_ping` MCP；不复制 `auth.json`，不使用危险 bypass。

截至 2026-08-20，本次失败会话和实时差分证明当前已加载旧 v2 实例为 `Basic=200 / Leading=200 / Conversation=200 / Continuation=500`；最后一步精确返回 `System and developer messages must precede conversation messages.`。管理器现在将其显示为 **Template Upgrade Required (v2 → v3)**，只有 completed v2 journal、当前实例/config、GGUF 指纹和确定性 v2 SHA 全部吻合才允许升级；四阶段全 200 前不会生成可提交的 Codex 配置计划。成功生成模板或 load 返回 200 本身都不会升级为 Supported。详见 [`docs/KNOWN-LIMITATIONS.md`](docs/KNOWN-LIMITATIONS.md)。

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
transactions\          不含模板正文/Token 的 LM Studio 恢复事务记录
  encrypted-backups\   schema-v4 的 CurrentUser DPAPI defaults 精确备份
```

Windows Credential Manager target：

- `CodexModelManager/DeepSeek`
- `CodexModelManager/LMStudio`

统一 redactor 覆盖已注册 secret、Bearer/API key 形态、Authorization header 与 URL query。日志不记录请求正文、完整 TOML、Cookie、auth.json、Token 或 credential 内容。

## 完全恢复到安装管理器以前

1. 完全关闭 Codex。
2. 在“备份历史”页选择 **恢复 Initial Snapshot**；确认后当前状态会先进入 History，主 `config.toml`/`models.json` 与所有曾由管理器明确修改过的 supplemental TOML 会一起恢复到各自首次触及时的状态。
3. 重新启动 Codex，确认模型、MCP 与 Project/Trust 均正常。
4. 若存在未完成的 schema-v4 事务，先用管理器的 **检查/恢复未完成事务**，让它从 DPAPI 备份恢复 defaults 并复核原实例；不要直接删除 journal/备份。已完成事务如需手工撤销，可在 LM Studio **My Models → 模型设置 → Prompt Template** 中删除该模型的覆盖并重新加载；高级用户也可在完全关闭 Codex 与该模型后，备份对应 JSON，再只删除 `load.fields` 中唯一的 `llm.load.promptTemplate` 条目，绝不要删除其他 load/operation/preset 字段或修改 GGUF。Codex Initial Snapshot 不管理 LM Studio 的独立运行状态。
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
dotnet test .\tests\CodexModelManager.App.Tests\CodexModelManager.App.Tests.csproj
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
  CodexModelManager.App.Tests/         net8.0-windows 隔离 STA WinForms 回归测试（不显示窗口）
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
- [LM Studio `/api/v1/models/load`](https://lmstudio.ai/docs/developer/rest/load)
- [LM Studio `/api/v1/models/unload`](https://lmstudio.ai/docs/developer/rest/unload)
- [LM Studio OpenAI-compatible endpoints](https://lmstudio.ai/docs/developer/openai-compat)
- [LM Studio Authentication](https://lmstudio.ai/docs/developer/core/authentication)

调查差异、来源哈希与已知 issue 见 [`docs/OFFICIAL-COMPATIBILITY-NOTES.md`](docs/OFFICIAL-COMPATIBILITY-NOTES.md)；最终构建、测试、发布哈希与隔离 smoke 记录见 [`docs/VERIFICATION.md`](docs/VERIFICATION.md)。

## 许可

本项目基于 [MIT 许可证](LICENSE) 开源。
