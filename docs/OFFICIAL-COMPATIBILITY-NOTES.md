# Official Compatibility Notes

核对日期：2026-08-18。优先级为官方文档/当前源码 > 官方脚本 > 官方 issue。

## Codex Provider 与认证

- 当前 Codex 源码将 `openai`、`ollama`、`lmstudio` 视为内置保留 Provider ID，custom provider 不能覆盖。默认 LM Studio 因此使用 `model_provider = "lmstudio"`；非默认 endpoint/认证使用 `lmstudio_local_cmm`。
- 当前配置参考支持 provider command auth：`[model_providers.<id>.auth]` 下的 `command`、`args`、`cwd`、`timeout_ms`、`refresh_interval_ms`。本工具的新 DeepSeek/需认证 LM 配置采用这一机制。
- 当前配置参考新增 `model_auto_compact_token_limit_scope = "total" | "body_after_prefix"`。Local 安全建议阈值按 `total` 计算并显式写入；OpenAI provider-specific state 会恢复该键原本的存在/缺失和值。
- command auth 不能与 `env_key`、`experimental_bearer_token` 或 `requires_openai_auth` 混合。已有 DeepSeek 官方明文 bearer table 被当作不可见 opaque 片段继续兼容。
- `preferred_auth_method` 已不在当前 Codex 配置参考中；DeepSeek 官方脚本仍会写它。本工具识别并在新 DeepSeek/Local 模式清理 legacy 冲突，但不会改写官方 backup。

依据：

- [Configuration Reference](https://developers.openai.com/codex/config-reference)
- [Advanced Configuration](https://developers.openai.com/codex/config-advanced)
- [model-provider-info source](https://github.com/openai/codex/blob/main/codex-rs/model-provider-info/src/lib.rs)

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
- Codex model ID 使用 loaded instance `id`；实际 context 使用 `loaded_instances[].config.context_length`。
- fallback API 缺失的能力保持 `Unknown`，不会从模型名字推断。
- LM Studio 可以要求认证，localhost 也不能假设永远无 Token。
- LM Studio 支持按模型覆盖 Prompt Template。管理器只导出精确匹配源模板的兼容版本，不写 LM Studio 内部配置；用户应用并重载后仍需用真实 Responses 差分验证。
- 本机实物说明不能把“Qwen 模板”当成单一格式：Qwen3.6 的已审计源模板 SHA 为 `E84F32A23FDDA27689F868AA4A1A5621F41133E51A48D7F3EFCBEA2839574259`，Qwen3.8 Q6_K/Q8_0 为 `C3CF9E34ABF4F9E36C2D72165AA9C132D3E2A725B6C2586AAA3A8AF9D7A81041` 且带额外 `reasoning_instructions`。修补器按宏、tools/system 区、主循环和拒绝分支的精确结构匹配两个变体，不按 SHA 或模型名放行。

依据：

- [LM Studio Models API](https://lmstudio.ai/docs/developer/rest/list)
- [LM Studio Codex integration](https://lmstudio.ai/docs/integrations/codex)
- [OpenAI-compatible endpoints](https://lmstudio.ai/docs/developer/openai-compat)
- [Authentication](https://lmstudio.ai/docs/developer/core/authentication)
- [Prompt Template](https://www.lmstudio.ai/docs/app/advanced/prompt-template)
- [Per-model Defaults](https://lmstudio.ai/docs/app/advanced/per-model)

## Qwen metadata 与 MCP

- 当前 Codex 对审计中的 Qwen instance 报告 metadata not found，并回退 fallback metadata。
- 本工具没有复制/伪造 GPT 或 DeepSeek catalog entry，因此 UI 会保留 compatibility warning。
- Custom/local Responses backend 的 MCP namespace tool schema 曾有未解决兼容性报告；“Responses + function call PASS”不能推导“MCP PASS”。
- Codex 的独立 developer 指令也不能由“普通 Responses PASS”推导兼容。当前 Qwen template 的差分复现为 control 200、加入 developer 后 system-order 500；管理器必须将这一检查放在切换写入之前。

相关 issue：

- [openai/codex #33263](https://github.com/openai/codex/issues/33263)
- [openai/codex #23186](https://github.com/openai/codex/issues/23186)
- [openai/codex #9392](https://github.com/openai/codex/issues/9392)

## 本 Prompt 与当前行为的主要差异

1. DeepSeek 官方脚本不是当前 Codex 最安全的新凭据范式；本工具保留兼容但新配置不再新增 plaintext bearer。
2. `preferred_auth_method` 是官方脚本遗留字段，不应机械复制到新配置。
3. LM Studio quantization 在当前 native API 是 object，parser 不能只按 string 读取。
4. 当前 Qwen 的 L1/L2 成功不代表完整 Codex Agent 成功；真实 Level 3 已证明其 chat template 仍不兼容 Codex 消息序列。
5. 因此新版管理器把 `instructions + developer + user` 作为 LM Studio 切换硬门槛，并提供不修改 GGUF 的 Prompt Template override 导出工具。
