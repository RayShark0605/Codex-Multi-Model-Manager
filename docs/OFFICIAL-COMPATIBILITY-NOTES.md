# Official Compatibility Notes

核对日期：2026-08-23。优先级为官方文档/当前源码 > 官方脚本 > 官方 issue。

## Codex Provider 与认证

- 当前 Codex 源码将 `openai`、`ollama`、`lmstudio` 视为内置保留 Provider ID，custom provider 不能覆盖。默认 LM Studio 因此使用 `model_provider = "lmstudio"`；非默认 endpoint/认证使用 `lmstudio_local_cmm`。
- 当前配置参考支持 provider command auth：`[model_providers.<id>.auth]` 下的 `command`、`args`、`cwd`、`timeout_ms`、`refresh_interval_ms`。本工具的新 DeepSeek/需认证 LM 配置采用这一机制。
- 当前配置参考新增 `model_auto_compact_token_limit_scope = "total" | "body_after_prefix"`。Local 安全建议阈值按 `total` 计算并显式写入；OpenAI provider-specific state 会恢复该键原本的存在/缺失和值。
- 当前 Codex 源码对未知/本地模型默认采用原始 context window 的 95% 作为说明性有效窗口；因此 `120064` 在 UI 中约为 `114k`，但 `model_context_window` 仍应保持服务端真实的 `120064`。
- `tool_output_token_limit` 是模型 metadata/config override，用于限制单个 tool/function 输出写入上下文的规模；它不是模型 reasoning、assistant 文本或函数参数的输出上限。本工具把它纳入受控根键，并为 OpenAI/DeepSeek 分别恢复原值或原本缺失状态。
- Codex turn loop 只有在模型返回仍需 follow-up 时才会在同一回合继续检查 rollover/compact；若该次采样已结束回合，下一用户回合的 pre-turn 检查才可能立刻压缩。因此需要在本地硬窗口之前留下足够的单次采样余量，不能依赖 tool output 计数更新后一定触发 mid-turn compact。
- command auth 不能与 `env_key`、`experimental_bearer_token` 或 `requires_openai_auth` 混合。已有 DeepSeek 官方明文 bearer table 被当作不可见 opaque 片段继续兼容。
- `preferred_auth_method` 已不在当前 Codex 配置参考中；DeepSeek 官方脚本仍会写它。本工具识别并在新 DeepSeek/Local 模式清理 legacy 冲突，但不会改写官方 backup。

依据：

- [Configuration Reference](https://developers.openai.com/codex/config-reference)
- [Advanced Configuration](https://developers.openai.com/codex/config-advanced)
- [model-provider-info source](https://github.com/openai/codex/blob/main/codex-rs/model-provider-info/src/lib.rs)
- [turn loop source](https://github.com/openai/codex/blob/main/codex-rs/core/src/session/turn.rs)
- [fallback model metadata source](https://github.com/openai/codex/blob/main/codex-rs/protocol/src/openai_models.rs)
- [configuration schema](https://github.com/openai/codex/blob/main/codex-rs/core/config.schema.json)
- [model config override source](https://github.com/openai/codex/blob/main/codex-rs/models-manager/src/model_info.rs)

## OpenAI model catalog

- 账户可见模型和 reasoning levels 优先通过 App Server `model/list` / provider capabilities 获取。
- 当前 App Server 的 `modelProvider/capabilities/read` 参数是空对象，结果按 `namespaceTools`、`imageGeneration`、`webSearch` 分别解析；缺失字段保持 Unknown。
- `models_cache.json` 仅作为不可用时 fallback，UI 标记 `IsStale`；不把 GPT-5.6 Sol/Terra/Luna 散落硬编码在业务事件中。

依据：[Codex App Server](https://developers.openai.com/codex/app-server)。

## DeepSeek 官方脚本差异

当前官方 Windows setup script：

- 创建独立 `backup-deepseek`；
- 写 `model_provider = "deepseek"`、Responses provider 与 `models.json`；
- 仍使用 TOML 明文 `experimental_bearer_token`；
- 仍写 legacy `preferred_auth_method`。

本工具不会执行脚本，只解析其 here-string catalog。新配置使用 Credential Manager + command-backed auth；已有官方配置原样互操作。含 `experimental_bearer_token` 的官方脚本 provider table 即使切往 OpenAI/LM Studio也保留原文，仅通过主 `model_provider` 停用，避免破坏以后重新运行官方脚本的环境。官方模型 catalog 的 `minimal_client_version`、reasoning/tool metadata 不由本工具猜测。

发行内 snapshot：

- 文件：`src/CodexModelManager.Core/Catalogs/deepseek-models.official-snapshot.json`
- 官方脚本 SHA-256：`239C5E7E4A24A5216CF03756CC66D7459C748A46D1D4BF084418D2B58EF54A36`
- catalog SHA-256：`B459A6E438D6A9939D01FD0DBB4693F165ED732BC8E4FD58D7145D9D94BD49A4`
- 当前 catalog 的最低 CLI 版本由每个 model entry 读取；本版本快照中的 DeepSeek 项为 `0.144.0`，业务代码没有写死该版本。

依据：

- [DeepSeek Integrate with Codex](https://api-docs.deepseek.com/quick_start/agent_integrations/codex)
- [Official setup PowerShell](https://cdn.deepseek.com/api-docs/codex-deepseek-setup-en.ps1)

## LM Studio

- native `/api/v1/models` 返回 `key`、display name、quantization object、size、architecture、max context、capabilities 与 loaded instances。
- native `type` 用于区分 `llm`、`embedding` 等实例；已加载不等于适合 Codex，已知非 LLM 类型会被拒绝切换。
- 当前实际 payload 的 quantization 是 object（例如 `{ name: "Q6_K", ... }`），不是必须为 string；parser 同时兼容两种形态。
- Codex model ID 使用 loaded instance `id`；实际 context 使用 `loaded_instances[].config.context_length`。GGUF 自动定位保留 Hub `lms ls --json --variants`，并使用官方 loaded-model 查询面 `lms ps --json --host/--port` 获取文件位置；CLI 证据不反向覆盖 native loaded state。
- fallback API 缺失的能力保持 `Unknown`，不会从模型名字推断。
- LM Studio 可以要求认证，localhost 也不能假设永远无 Token。
- LM Studio 官方文档支持 list/load/unload、per-model defaults 与模型级 Prompt Template。另经本机 0.4.21 实际运行包 schema 与 HTTP 行为确认，`/api/v1/models/load` 接受顶层 `{ prompt_template: { type: "jinja", template, stop_strings } }`；该字段尚未出现在公开 REST 参数页，因此旧 schema-v1–v3 runtime-only 事务仍把它作为严格验证、失败即回滚的版本相关能力，而不是稳定官方契约。当前 schema-v4 正式路径改为写入经验证的 per-model default，并用**不含** REST `prompt_template` 的重载证明持久设置生效。
- 自动流程只在精确源模板与三个已知失败码匹配、用户预览确认后运行；它保存 `selected_variant` 和全部当前可观察 load config，使用响应返回的新 instance ID，重新列举并执行 Basic/Leading/Conversation/Continuation 四阶段 Responses 差分。GGUF 始终只读；LM Studio `0.4.21.x` 与 `0.4.23.x` 的 schema-v4 路径只新增或升级 concrete GGUF defaults 中唯一的 `llm.load.promptTemplate` 字段，保留其他字段与未知属性，且在失败时从 DPAPI 证据恢复。其他版本继续 fail closed，手工导出仍保留。
- 0.4.21 的实测加载契约区分三种 ID：`/load.model` 使用 list 返回的源 `key`；`selected_variant` 只作预期量化/文件验证；`instance_id` 只用于 Responses 与 `/unload`。向 `/load` 发送 `qwen/qwen3.8-27b@q8_0` 会得到 `404 model_not_found`，而源 key 可进入加载流程，因此管理器不会再把三者互换。
- 0.4.23.0 的 Qwen3.8 Flash Next 现场再次证明逻辑加载 key 与 concrete GGUF 身份必须分开：native `SourceModelKey=qwen3.8-flash-next@iq4_xs`，而 `lms ps` 的 sharded concrete identity 为 `unsloth/Qwen3.8-Flash-Next-GGUF/Qwen3.8-Flash-Next-UD-IQ4_XS-00001-of-00003.gguf`；后者必须与最终物理 GGUF 路径的规范化尾部一致，但不得被错误要求等于前者。
- LM Studio 0.4.23 的正式发布记录包含 Qwen 3.8 Flash Next 改进；本工具只据现场结构精确新增 `0.4.23.x` allowlist，不由此放行 `0.4.22.x`、`0.4.24.x` 或未来版本。大型模型生命周期请求与自动回滚使用独立 30 分钟预算，普通 Provider/Preview 仍为 3 分钟，四阶段探针仍逐阶段 45 秒。
- `prompt_template` schema 能力在卸载前用随机不存在的 model key 做无副作用探测：只有对象形态通过 schema 并到达 `404/model_not_found` 才继续。HTTP 错误只保留 status 与截断脱敏后的 `error.type/code/param/message`，不保留原始响应、模板正文或 bearer token。
- 本机实物说明不能把“Qwen 模板”当成单一格式：Qwen3.6 的已审计源模板 SHA 为 `E84F32A23FDDA27689F868AA4A1A5621F41133E51A48D7F3EFCBEA2839574259`，较早 Qwen3.8 Q6_K/Q8_0 为 `C3CF9E34ABF4F9E36C2D72165AA9C132D3E2A725B6C2586AAA3A8AF9D7A81041`，当前 Unsloth Q6_K_XL 184 行 prefix-merged-system 模板为 `12827F24B742EA4E80CDC12DBCF9622227056B9F797252A3149263D4F9AAADCE`。`qwen-interleaved-instructions-v3` 按每个模板族的宏、tools/system 区、反向扫描、主循环、vision/reasoning/tool-call/generation 与拒绝分支精确匹配，不按 SHA 或模型名放行；旧 v2 只用于精确升级与回滚。
- Responses 输入允许在多轮历史中出现新的 developer/system 指令。Codex 在 Plan→Default、权限或 turn-context 更新时会追加 developer；因此 `instructions + developer + user` 单轮通过并不充分。四阶段探测的步骤 3/4 仅相差最后一个 user 前的 developer，用于把普通多轮错误与 continuation 模板错误分开。

依据：

- [LM Studio Models API](https://lmstudio.ai/docs/developer/rest/list)
- [LM Studio `lms ps`](https://lmstudio.ai/docs/cli/local-models/ps)
- [LM Studio Load API](https://lmstudio.ai/docs/developer/rest/load)
- [LM Studio Unload API](https://lmstudio.ai/docs/developer/rest/unload)
- [LM Studio Codex integration](https://lmstudio.ai/docs/integrations/codex)
- [LM Studio 0.4.23 changelog](https://lmstudio.ai/changelog/lmstudio/lmstudio-v0.4.23)
- [Per-model Defaults](https://lmstudio.ai/docs/app/advanced/per-model)
- [OpenAI-compatible endpoints](https://lmstudio.ai/docs/developer/openai-compat)
- [Authentication](https://lmstudio.ai/docs/developer/core/authentication)
- [Prompt Template](https://www.lmstudio.ai/docs/app/advanced/prompt-template)
- [Per-model Defaults](https://lmstudio.ai/docs/app/advanced/per-model)

## Qwen metadata 与 MCP

- 当前 Codex 对审计中的 Qwen instance 报告 metadata not found，并回退 fallback metadata。
- 本工具没有复制/伪造 GPT 或 DeepSeek catalog entry，因此 UI 会保留 compatibility warning。
- Custom/local Responses backend 的 MCP namespace tool schema 曾有未解决兼容性报告；“Responses + function call PASS”不能推导“MCP PASS”。
- Codex 的独立或后置 developer 指令都不能由“普通 Responses PASS”推导兼容。内置模板可表现为 Basic 200/Leading 500；旧 v2 可表现为 Basic 200/Leading 200/Conversation 200/Continuation 500。管理器必须将完整四阶段检查放在切换写入之前。

相关 issue：

- [openai/codex #33263](https://github.com/openai/codex/issues/33263)
- [openai/codex #23186](https://github.com/openai/codex/issues/23186)
- [openai/codex #9392](https://github.com/openai/codex/issues/9392)

## 本 Prompt 与当前行为的主要差异

1. DeepSeek 官方脚本不是当前 Codex 最安全的新凭据范式；本工具保留兼容但新配置不再新增 plaintext bearer。
2. `preferred_auth_method` 是官方脚本遗留字段，不应机械复制到新配置。
3. LM Studio quantization 在当前 native API 是 object，parser 不能只按 string 读取。
4. 当前 Qwen 的 L1/L2 成功不代表完整 Codex Agent 成功；真实 Level 3 已证明其 chat template 仍不兼容 Codex 消息序列。
5. 因此新版管理器把四阶段差分作为 LM Studio 切换硬门槛，并同时提供不修改 GGUF 的手工 v3 导出与经确认的事务式运行时 Prompt Template 注入；两者都必须以重载后的真实差分结果为准。
