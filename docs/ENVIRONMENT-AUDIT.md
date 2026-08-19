# Environment Audit (redacted)

审计时间：2026-08-18（Asia/Shanghai）。本文件不含完整配置、Token、Authorization、Cookie 或 `auth.json` 内容。

## Codex

| 项目 | 结果 |
|---|---|
| Codex Desktop | `26.814.5167.0` |
| bundled Codex CLI | `0.148.0-alpha.15` |
| 显式 `CODEX_HOME` 环境变量 | 未设置 |
| 实际 home | `%USERPROFILE%\.codex` |
| `config.toml` | 存在，5390 bytes |
| 最终只读 SHA-256 | `FB2E44DD795360D6744F62C1524A76966EB8B7ECDA95B269FACD2078EE778B8C` |
| 最后写入（UTC） | `2026-08-18T11:27:20.4800492Z` |
| 编码/换行 | UTF-8 无 BOM / LF |
| 当前 provider | `openai`（未显式写入，使用内置默认） |
| 当前 model | `gpt-5.6-sol` |
| reasoning | `max` |
| MCP 顶级实例 | 7 |
| Project Trust 实例 | 12 |
| `models.json` | 不存在 |
| `backup-deepseek` | 不存在 |
| 本工具 Initial / History | Initial 存在；History 3 份 |

审计期间曾观察到 Codex 自行改变配置文件指纹，因此实现将预览指纹与提交前二次指纹检查作为强制门槛。开发和自动测试未执行真实 Provider 切换。

隔离临时 `CODEX_HOME` 的 App Server 只读探测已返回 `gpt-5.6-sol`、`gpt-5.6-terra`、`gpt-5.6-luna` 等动态 model list，并从 `modelProvider/capabilities/read` 返回当前 OpenAI provider 的 `namespaceTools=true`、`imageGeneration=true`、`webSearch=true`。这些 provider-level 声明不会替代对用户实际 MCP/工具的端到端测试。

## DeepSeek

- 当前配置中未识别到 DeepSeek provider。
- 当前未发现 DeepSeek 官方 `backup-deepseek`。
- 未发现相关 Token 环境变量；审计只检查“是否存在”，不读取或记录值。
- 官方 setup script 仅下载到临时位置解析，未执行。

## LM Studio

| 项目 | 结果 |
|---|---|
| Version | `0.4.21+2` |
| `lms` CLI | `1.3.3`，commit `71bd99c` |
| Server | `http://127.0.0.1:1234`，无认证 |
| `lms server status` | running on port 1234 |
| native API | `/api/v1/models` |
| 最终模型数 | 16（实时状态可能继续变化） |
| native `loaded_instances` | 0 |
| `lms ps` | `qwen/qwen3.8-27b@q6_k`，`IDLE`，context `131072` |
| 安全切换判定 | 阻止；不把 `lms ps` 或理论 Max 猜作 native loaded context |

`/api/v1/models` 与 `lms ps` 的最终状态不同。管理器将 native API 作为 Codex Server 实例与实际 context 的权威面；只要 `loaded_instances` 缺失，就不会允许 Preview/Switch。开发过程没有调用 load/unload，也没有改变 context、GPU offload、KV cache 或 Prompt Template override。

## Live compatibility evidence

- Level 1/2：早期空闲的 Q6_K 基线中 native discovery、普通 `/v1/responses`、SSE streaming、严格 dummy function call 均通过；这只是当时的普通请求证据。
- 后续复测发现运行时新增了 loaded embedding；类型防护已正确排除它。随后 Q6_K LLM instance 长时间保持 `GENERATING`，普通 Responses 在 3 分钟门槛超时，因此复测记录为运行时忙或卡住，而不是继续宣称最新一次请求 PASS；管理器没有为清理该状态而自动 unload/reload 模型。
- 指令层级差分：`instructions + user` 为 HTTP 200；只加入独立 `developer` 消息后为 HTTP 500，正文稳定包含 `System message must be at the beginning`，分类为 `lmstudio-chat-template-system-order`。
- Level 3：Q6_K 上的真实 Codex CLI 成功启动临时 thread，但 LM Studio engine 返回同一 Jinja chat-template 错误；Codex 最终 exit 1。
- 因 Level 3 在首次模型请求即失败，shell、`apply_patch` 与临时 MCP 没有获得端到端成功证据，不能标记 Supported。
- 最终 native API 报告 0 个 loaded instance，因此本轮最终状态没有发送新推理请求，也没有运行 post-template L2/L3；管理器会继续硬阻止切换。

### GGUF Prompt Template 只读证据

| GGUF | Source template SHA-256 | Patched template SHA-256 | 结果 |
|---|---|---|---|
| Qwen3.8-27B-Uncensored Q6_K | `C3CF9E34ABF4F9E36C2D72165AA9C132D3E2A725B6C2586AAA3A8AF9D7A81041` | `AA8741EB5D416E5E481B8E3BFE530CEEC25A005B1CCD384E6670FEA51B147531` | Supported |
| Qwen3.8-27B-Uncensored Q8_0 | `C3CF9E34ABF4F9E36C2D72165AA9C132D3E2A725B6C2586AAA3A8AF9D7A81041` | `AA8741EB5D416E5E481B8E3BFE530CEEC25A005B1CCD384E6670FEA51B147531` | Supported |
| Qwen3.6-35B-A3B Q4_K_M | `E84F32A23FDDA27689F868AA4A1A5621F41133E51A48D7F3EFCBEA2839574259` | `9E6CDDF08965594E25EE1678C9E823BB6413F51847964D3FD0A84DF1E14A2D73` | Supported |

三项均为 GGUF v3，仅读取 metadata header。Qwen3.8 模板包含额外的 `reasoning_instructions` system 前缀，因此并不等同于最初确认的 Qwen3.6 模板。修补器 `qwen-leading-instructions-v2` 通过精确结构锚点分别支持这两个变体，不使用 SHA 或模型名作为业务放行条件。实际 Qwen3.8 修补模板还用 Jinja 3.1.4 做了离线语义渲染：连续 system/developer 合并成唯一 system block，顺序与双换行保持，tools 模式保持，后置 system 仍被拒绝。

## 写入边界

本次修复的源码、测试和发布写入位于工程目录、工程内 NuGet/编译目录和经验证的 `%TEMP%\CodexModelManager*`。最终 GUI smoke 同时使用随机临时 `CODEX_HOME` 与 `CMM_LOCALAPPDATA_OVERRIDE`，退出后已清理；隔离 config 启动前后 SHA 一致。

早期 GUI smoke 的第一版 PowerShell 驱动误用了只读 `$HOME` 变量名，使临时 `CODEX_HOME` 回退到 `%USERPROFILE%`，新建了 53-byte 的 `%USERPROFILE%\config.toml` 与只含 `initial/config.toml`、`initial/manifest.json` 的 `%USERPROFILE%\model-switcher-backup`。程序停止后，已按创建时间、精确内容、SHA 和路径白名单验证并只删除这两个新建路径；最终复核中二者均不存在。后续 smoke 使用 `$tempCodexHome` 且启用 `ErrorActionPreference=Stop`。

用户此前实际使用旧版管理器切换后，真实 `model-switcher-backup` 已存在：Initial 存在、History 3 份；计划中指定的失败快照 `history\20260818-192701363\config.toml` 仍存在，5500 bytes、SHA-256 `B440C7686F903BF08AD179455E012038A37949886CD2D5D252E71F5E948704FD`。这些是当前真实状态，不被本轮修复删除或覆盖。最终真实 `config.toml` 为 5390 bytes、SHA-256 `FB2E44DD795360D6744F62C1524A76966EB8B7ECDA95B269FACD2078EE778B8C`，Provider 仍为 implicit `openai`、model 仍为 `gpt-5.6-sol`、reasoning 仍为 `max`。误建的 `%USERPROFILE%\config.toml` 与 `%USERPROFILE%\model-switcher-backup` 均不存在。本轮修复没有执行真实 Provider 切换，没有写 `models.json`，也没有触碰 `backup-deepseek`、`auth.json`、Credential Manager、Token 或用户/系统环境变量。
