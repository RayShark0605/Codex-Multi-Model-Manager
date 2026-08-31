# Environment Audit (redacted)

审计时间：原始基线 2026-08-18；最新只读增量 2026-08-31（Asia/Shanghai）。本文件不含完整配置、Token、Authorization、Cookie 或 `auth.json` 内容。

## 2026-08-31 Qwen3.8 Flash Next / LM Studio 0.4.23 只读增量

- LM Studio 正在运行的主程序文件版本为 `0.4.23.0`，`lms` CLI 为 `1.3.3`，endpoint 为 `http://127.0.0.1:1234`。native `/api/v1/models` 的唯一 loaded LLM 为 `qwen3.8-flash-next@iq4_xs`：publisher `unsloth`，format `gguf`，architecture `qwen4exp`，quantization `IQ4_XS`，loaded/max context `196608 / 262144`。
- native loaded config 为：eval/physical batch `2048 / 512`、parallel `1`、flash attention `true`、context checkpoints `16`、MTP/simple/external speculative draft 均未启用、`num_experts=10`、KV cache GPU offload `true`。这是本轮 reload 前的权威运行时基线；后续真实切换必须逐字段复现，不能只比较 context。
- `lms ps --json` 的 `identifier` 与逻辑 `modelKey` 均为 `qwen3.8-flash-next@iq4_xs`，但 concrete sharded identity 为 `unsloth/Qwen3.8-Flash-Next-GGUF/Qwen3.8-Flash-Next-UD-IQ4_XS-00001-of-00003.gguf`。最终物理路径为 `J:\LM Studio Models\unsloth\Qwen3.8-Flash-Next-GGUF\Qwen3.8-Flash-Next-UD-IQ4_XS-00001-of-00003.gguf`；concrete identity 与规范化路径尾部一致，但不等于逻辑 source/load key。
- GGUF metadata 只读解析结果：GGUF v3，第一 shard `10,946,624` bytes，最后写入 `2026-08-27T02:19:17.0812276Z`；源 Prompt Template SHA-256 为 `12827F24B742EA4E80CDC12DBCF9622227056B9F797252A3149263D4F9AAADCE`，可确定性生成 `qwen-interleaved-instructions-v3`，目标模板 SHA-256 为 `9DC0DA000D1DF280BE9F6F64D314EB52879C0DF5C3C951F74105964136592F85`。
- 当前 concrete defaults 位于 `C:\Users\xr\.lmstudio\.internal\user-concrete-model-default-config\unsloth\Qwen3.8-Flash-Next-GGUF\Qwen3.8-Flash-Next-UD-IQ4_XS-00001-of-00003.gguf.json`，`946` bytes，SHA-256 `AD63A945C08969EA937B6E44FEB2443D25739933FB4906F2117EC126DC3DF94C`。根结构为 `preset`、`operation.fields`、`load.fields`；八个 load 字段依次保存 context、offload ratio、CPU thread pool、parallel sessions、context checkpoints、CPU expert ratio、K/V cache quant，Prompt Template 字段数为 0。
- 使用当前生产代码对上述实物生成只读 defaults 计划成功：LM Studio version `0.4.23.0`，mutation `Add`，原字段状态 `Missing`，候选 `13,303` bytes，SHA-256 `65EC0194CCD81A21C21CEF9D1607F69C5F5A9115A393BF8204D74BEE21043011`。候选与原 JSON 的语义差异只有 `load.fields[llm.load.promptTemplate]`；计划生成后真实 defaults 仍保持原 SHA。
- 四阶段只读探针仍准确报告 Basic/Leading/Conversation `HTTP 200`、Continuation `HTTP 500`，failure code `lmstudio-chat-template-system-order`；LM Studio Jinja 日志为 `System message must be at the beginning`。这是“检测成功并确认模板不兼容”，不是检测流程异常，也没有被测试伪装成 PASS。
- server log 记录该 87.2 GiB 实例从 `2026-08-31 15:38:12` 开始 load，到 `15:47:03` 报告 `model loaded`，约 8 分 50 秒。因此生命周期与自动回滚预算使用 30 分钟；普通 Provider/Preview 仍为 3 分钟，四阶段请求仍逐阶段 45 秒。
- `Category=LiveLmStudio` 且未启用 mutation 的现场回归为 `3 PASS / 0 FAIL / 2 条件不适用 SKIP`。它证明 0.4.23 Preview、逻辑 source key/concrete identity 分离、GGUF 模板和普通 Responses 发现路径；前后 loaded instance/config、defaults `946` bytes/SHA、Codex config `6,216` bytes/SHA `5C69961AB82C718BF3F25DDE2126FE32EE53081A7A692C1CC1E08679CAC0EEBD`、19 个 journal 集合完全一致，未完成 journal 数为 0。
- 当前 Codex Desktop/CLI 仍在运行，CLI 实测版本为 `0.151.0-alpha.7.2`；真实配置仍是 implicit `openai`、model `gpt-5.6-sol`。本增量没有启动管理器 GUI、写 defaults、调用 `/load`/`/unload`、提交 Provider、写 Credential Manager 或运行 post-patch Level 3；这些必须在用户完全关闭全部 Codex 进程后用新发布工件执行。

## 2026-08-22 Unsloth Q6_K_XL 只读增量

- native `/api/v1/models` 权威快照：source/instance ID 均为 `qwen3.8-27b@q6_k_xl`，type `llm`，architecture `qwen35`，quantization `Q6_K_XL`，loaded context `161024`，Max `262144`，`selected_variant=null`。
- `lms ls --json --variants` 只列出另一个 `qwen/qwen3.8-27b` Hub 家族，不能定位当前 Unsloth 文件；endpoint-aware `lms ps --json --host 127.0.0.1 --port 1234` 则唯一给出 publisher `unsloth` 与相对路径 `unsloth/Qwen3.8-27B-GGUF/Qwen3.8-27B-UD-Q6_K_XL.gguf`。新 locator 的现场断言确认 provenance 为 `lms ps --json`，但 instance/context/架构/量化仍以 native 为准。
- 当前 GGUF 的 184 行 metadata Prompt Template 与运行模板证据 SHA 均为 `12827F24B742EA4E80CDC12DBCF9622227056B9F797252A3149263D4F9AAADCE`；新精确模板族可确定性生成 `qwen-interleaved-instructions-v3`，SHA 为 `9DC0DA000D1DF280BE9F6F64D314EB52879C0DF5C3C951F74105964136592F85`。业务放行依赖完整结构与 post-patch 复验，不依赖模型名或 SHA allowlist。
- 修复前的已确认四阶段行为为 Basic/Leading/Conversation `200`，Continuation `500`，failure code `lmstudio-chat-template-system-order`；新 probe 文案会把它明确说明为“只接受开头连续指令、拒绝后置 developer”，而不是将重新检测本身显示成崩溃。
- 两项现场测试仅执行 native/CLI/GGUF metadata 读取与临时目录导出，均 `1 PASS / 0 FAIL`。前后 loaded instance、真实 `config.toml`（5456 bytes，SHA-256 `68D514211B78B939C63D79802B03EB27539E2B021192A0420BBC522D3B4BDE96`）以及 12 个 transaction/lock 文件的名称、长度和 SHA 集合完全一致。证据：`artifacts/test-results/live-q6-k-xl-readonly-invariants.json`。
- 本轮没有调用 `/load`、`/unload`，没有应用 Prompt Template，没有切换真实 Provider，也没有修改 GGUF、LM Studio settings、Credential Manager、真实 Codex 配置或既有 journal。post-patch 四阶段 PASS、真实 Codex Agent 与 Provider 切换保持 `Untested`。
- GUI smoke 后续被用户明确叫停并判定为 `ABORTED_BY_USER / NOT PASS`：用户观察到重复错误框（疑似 Git 无法启动），且当前 Codex 正在运行。叫停后没有再次启动 `CodexModelManager.exe`；精确发布路径残留进程为 0，隔离 GUI 临时目录已清理，真实切换仍未执行。

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
| Qwen3.8-27B-Uncensored Q6_K | `C3CF9E34ABF4F9E36C2D72165AA9C132D3E2A725B6C2586AAA3A8AF9D7A81041` | `4AA5CC42C084FCC8235AAF0500835F4F9419A72280EA7E02D08EEE9A97807D8B` | Supported v3 |
| Qwen3.8-27B-Uncensored Q8_0 | `C3CF9E34ABF4F9E36C2D72165AA9C132D3E2A725B6C2586AAA3A8AF9D7A81041` | `4AA5CC42C084FCC8235AAF0500835F4F9419A72280EA7E02D08EEE9A97807D8B` | Supported v3 |
| Qwen3.6-35B-A3B Q4_K_M | `E84F32A23FDDA27689F868AA4A1A5621F41133E51A48D7F3EFCBEA2839574259` | `235C3E8D316D80E23827174F1A8CEF37B1E5018CF70ED8F52F2C6FB9C0E233CD` | Supported v3 |

三项均为 GGUF v3，仅读取 metadata header。Qwen3.8 模板包含额外的 `reasoning_instructions` system 前缀，因此并不等同于最初确认的 Qwen3.6 模板。修补器 `qwen-interleaved-instructions-v3` 通过精确结构锚点分别支持这两个变体，不使用 SHA 或模型名作为业务放行条件。v3 会遍历完整消息序列，按原相对顺序收集所有 system/developer，合并为唯一前导 system block并保持双换行；tools 和 reasoning-aware 分支均保留。未知第三种结构、混合换行或未知管理器 Marker 仍被拒绝。

## 2026-08-20 earlier Q8 / 404 recovery audit

- LM Studio 版本 `0.4.21.0`，`lms` CLI `1.3.3`，endpoint `http://127.0.0.1:1234`。
- 当时 native `/api/v1/models` 报告 `qwen/qwen3.8-27b` loaded instance，`selected_variant=qwen/qwen3.8-27b@q8_0`，实际 `context_length=32768`；这取代当时截图中的 `262144`，但不是后续操作的固定目标。实现仍在每次操作前重读。
- `lms ls --json --variants` 的精确 Q8 条目给出 `indexedModelIdentifier=qwen/qwen3.8-27b@lmstudio-community/Qwen3.8-27B-GGUF/Qwen3.8-27B-Q8_0.gguf`；结合 `~/.lmstudio/settings.json` 的 `downloadsFolder=J:\\LM Studio Models` 可唯一解析实际 GGUF。旧 locator 只读取逻辑 `path=qwen/qwen3.8-27b`，因此返回空路径；新版改为按 selected variant 和 indexed identifier 解析。
- 已安装 0.4.21 主进程包的实际请求 schema接受顶层 `prompt_template` 对象及当前实例暴露的扩展加载字段。公开 REST 文档只作为 list/load/unload 基础契约；`prompt_template` 仍由请求回显、重新列举与 hierarchy probe 三层验证，任何不一致均回滚。
- 失败事务 `ac6ea94927c1465693a826960f139630` 的 LM Studio server log 明确记录 `Model qwen/qwen3.8-27b@q8_0 not found in downloaded models`；`lms load ...@q8_0 --estimate-only` 同样拒绝，而 `lms load qwen/qwen3.8-27b --estimate-only` 能解析当前 Q8 下载。根因是旧实现把 `selected_variant` 错当成 `/load.model`。补丁 load 404 后，旧回滚又重复使用同一无效值，schema-v1 journal 最终只剩 `RollbackFailed`，导致恢复误把后来出现的唯一原实例判成歧义。
- 新版只读 recovery assessment 曾直接读取该 legacy journal 和当时 native/Responses 状态：唯一实例 ID、Q8_0、架构、context 32768、完整 load config 与 GGUF 指纹均匹配，control HTTP 200 且 Codex-shaped 精确复现 `lmstudio-chat-template-system-order`；评估结果为 `AlreadyRestored`，并验证评估前后 loaded instance ID 集合未变化。该事务随后已按零重载路径更新为 schema 2 / `RolledBack`。
- 本次开发会话本身运行于 Codex Desktop，因此产品的“Codex 必须完全关闭”门禁会阻止真实 unload/load；该门禁没有为了测试而绕过。显式 lifecycle live test只有在关闭 Codex并设置 `CMM_RUN_LIVE_LM_MUTATION=1` 后才会执行。

## 2026-08-20 Plan→Default continuation re-audit

- 失败任务的 LM Studio 请求先包含前导 developer、多轮 user/assistant/reasoning/function-call 历史，随后在用户批准计划前追加 Default collaboration-mode developer；Jinja 在模型推理之前抛出 `System and developer messages must precede conversation messages.`。
- 根因是旧 `qwen-leading-instructions-v2` 只扫描连续前导 system/developer，并主动拒绝对话开始后的第二段指令。原两阶段探测只覆盖前导 developer，因此曾错误显示 PASS。
- 本轮实现后的实时只读四阶段探测以当时 native 状态为准：instance `qwen/qwen3.8-27b`、variant `qwen/qwen3.8-27b@q6_k`、Q6_K、实际 context `70144`；结果为 Basic 200、Leading 200、Conversation 200、Continuation 500，准确分类 `lmstudio-chat-template-continuation-instruction-order`。
- completed schema-v2 事务 `595a50afb4d342098626100d577aaa08`、当前 instance/config/variant、Q6_K GGUF 指纹和确定性 v2 SHA `AA8741EB5D416E5E481B8E3BFE530CEEC25A005B1CCD384E6670FEA51B147531` 全部匹配；新增 live 只读测试成功创建 v2→v3 计划，且前后 loaded instance ID 集合不变、没有写新 journal，也没有 unload/load。
- 目标 Qwen3.8 v3 SHA 为 `4AA5CC42C084FCC8235AAF0500835F4F9419A72280EA7E02D08EEE9A97807D8B`。真正的 v3 生命周期注入、四项 200、隔离 Codex agent `CMM_PONG` 与 Plan→Default 工具闭环仍必须在关闭当前 Codex 任务后由发布 GUI 执行；产品进程门禁没有被绕过。发布工件自己的 MCP helper `cmm_ping` smoke 已通过，这不等价于模型侧 Codex agent smoke。

## 写入边界

本次修复的源码、测试和发布写入位于工程目录、工程内 NuGet/编译目录和经验证的 `%TEMP%\CodexModelManager*`。最终 GUI smoke 同时使用随机临时 `CODEX_HOME` 与 `CMM_LOCALAPPDATA_OVERRIDE`，退出后已清理；隔离 config 启动前后 SHA 一致。

早期 GUI smoke 的第一版 PowerShell 驱动误用了只读 `$HOME` 变量名，使临时 `CODEX_HOME` 回退到 `%USERPROFILE%`，新建了 53-byte 的 `%USERPROFILE%\config.toml` 与只含 `initial/config.toml`、`initial/manifest.json` 的 `%USERPROFILE%\model-switcher-backup`。程序停止后，已按创建时间、精确内容、SHA 和路径白名单验证并只删除这两个新建路径；最终复核中二者均不存在。后续 smoke 使用 `$tempCodexHome` 且启用 `ErrorActionPreference=Stop`。

用户此前实际使用旧版管理器切换后，真实 `model-switcher-backup` 已存在：Initial 与 History 均继续保留；计划中指定的失败快照 `history\20260818-192701363\config.toml` 仍不由本轮覆盖或删除。最终只读复核时，真实 `config.toml` 为 5427 bytes、SHA-256 `B2A3A7CD819B20304A017AA6202B96B85F28B7A9DEF8F445FD540C2C1C75B079`，Provider 仍为 implicit `openai`、model 仍为 `gpt-5.6-sol`、reasoning 为 `xhigh`。本轮 v3 开发没有执行真实 Provider 切换，没有写 `models.json`，也没有触碰 `backup-deepseek`、`auth.json`、Credential Manager、Token 或用户/系统环境变量。
