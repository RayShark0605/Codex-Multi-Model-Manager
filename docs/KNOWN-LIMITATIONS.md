# Known Limitations

## 已确认

1. **Qwen 内置模板以及旧 `qwen-leading-instructions-v2` 都不能由单轮成功推导完整 Codex 兼容。**
   - 内置模板的差分请求证明 `instructions + user` 返回 200，只增加独立 developer 消息便返回 `System message must be at the beginning`。
   - 旧 v2 可让 Basic、Leading Developer、无后置 developer 的多轮 Conversation Control 三项返回 200，但 Plan→Default 会在历史后追加 developer，随后精确返回 `System and developer messages must precede conversation messages.`。当前 Unsloth prefix-merged-system 内置模板同样只收集开头连续指令，现场四阶段形状为 200/200/200/500，但 Continuation 错误仍是 `System message must be at the beginning.`；planner 仅把这一完整形状识别为 BuiltIn provenance。
   - 失败发生在 Jinja 渲染、模型生成、shell、file editing 和 MCP 之前；因此普通 Responses 或单独 function-call PASS 不能升级 Codex Agent 状态。
   - 管理器分别分类为 `lmstudio-chat-template-system-order`、`lmstudio-chat-template-developer-role` 和 `lmstudio-chat-template-continuation-instruction-order`，并在 Preview/Commit、备份和任何真实配置写入之前硬阻止。
   - 对三个已识别失败码且结构精确匹配的 GGUF，可由用户先预览当前运行时来源、原始/v2/v3 哈希、per-model defaults 路径/前后指纹、目标模板和完整加载配置，再明确确认事务式持久 defaults 写入与 unload/load；成功导出、成功写文件、成功 POST 或成功 load 都不等于兼容，无 REST `prompt_template` 的重载、重新列举的配置保持、持久字段复核及四阶段实时差分才是依据。手工导出/重载仍是回退路径。
   - 同模型不同量化可能共享同一错误模板，但仍按 loaded instance 单独检测；不会按名称或量化继承 PASS。
   - 本机 Qwen3.6 的源/v3 SHA 为 `E84F32A23FDDA27689F868AA4A1A5621F41133E51A48D7F3EFCBEA2839574259` / `235C3E8D316D80E23827174F1A8CEF37B1E5018CF70ED8F52F2C6FB9C0E233CD`；较早 Qwen3.8 Q6_K/Q8_0 为 `C3CF9E34ABF4F9E36C2D72165AA9C132D3E2A725B6C2586AAA3A8AF9D7A81041` / `4AA5CC42C084FCC8235AAF0500835F4F9419A72280EA7E02D08EEE9A97807D8B`；当前 Unsloth Q6_K_XL prefix-merged-system 为 `12827F24B742EA4E80CDC12DBCF9622227056B9F797252A3149263D4F9AAADCE` / `9DC0DA000D1DF280BE9F6F64D314EB52879C0DF5C3C951F74105964136592F85`。v3 对三个结构族分别做完整精确复验，并在主循环渲染 content 前显式跳过已合并的 system/developer；这些 SHA 只是审计证据，不是按名称或哈希放行的 allowlist。
   - 任何第三种模板、锚点缺失/重复、混合换行或未知人工修改都会被保守拒绝；管理器不会生成猜测模板。

2. **Qwen metadata 是 fallback。**
   - 当前官方 Codex catalog 未提供审计 model ID 的 metadata。本工具故意不伪造 apply_patch/tool/reasoning/Plan 能力。

3. **Secondary 外部配置是显式 opt-in。**
   - 主配置和引用的 agent/profile/project TOML 都会扫描，但默认 Preserve 且默认不勾选。
   - 只有用户明确勾选的可编辑项才参与 FollowMain/RestoreOriginal；这些文件进入同一原子事务、History 和外部修改检测，首次修改前另建不可覆盖的 supplemental baseline。
   - 只处理扫描到的 TOML 字符串型 model override；不自动改写任意 agent 行为配置，也不猜测未知格式。RestoreOriginal 会恢复记录的原始 TOML 字符串 token（包括单/双引号形式）。

4. **MCP 保持 Known Limitation/Untested。**
   - 只有真实 `cmm_ping` 临时 MCP 测试通过后，当前模型的本次报告才可升级；不会据此修改用户 MCP。

5. **长上下文工具调用 JSON 截断已定位，但新预算的真实长任务效果仍是 Untested。**
   - 2026-08-23 的 Codex session 与 LM Studio server log 交叉证明：失败采样达到 `n_tokens=120063`、`truncated=1` 后，`handleToolCallGenerationFailed` 因不完整 arguments 抛出 `Unterminated string in JSON`；Codex UI 的 `stream disconnected before completion` 是上层包装，不是普通网络抖动。
   - 故障链是长 reasoning/assistant 生成进入 tool-call arguments 后撞上 `120064` 硬窗口，JSON 被截断并解析失败。增加 retry 或 stream timeout 只会重复同一边界失败，不能修复已被截断的 JSON。
   - 新平衡值 `95488` 为本次 `120064` 窗口留下 `24576` tokens，比现场单次约 1.88 万 tokens 的增长多约 5.7k，但不对无限长 reasoning 提供数学保证。若仍复现，安全优先建议为 `87296`，而不是继续增加重试次数。
   - `tool_output_token_limit=2401` 仅限制单个工具结果写回历史，不能限制 reasoning、assistant 文本或函数 arguments 的生成长度，也不能替代模型输出上限。
   - 本轮按要求不切换真实 Provider；在使用新配置完成一次真实长上下文任务并检查 LM Studio 日志前，运行时结论必须保持 **Untested**。

## 设计边界

- 常规发现、刷新、测试与 Preview 不自动 load/unload。只有失败码为 `lmstudio-chat-template-system-order`、`lmstudio-chat-template-developer-role` 或 `lmstudio-chat-template-continuation-instruction-order`，精确变体与 GGUF 均可证明且用户确认后，Switch 才执行事务式 unload/load；它保留 native API 暴露的加载配置，而不是选择新的 context、量化、GPU/KV 或 speculative 参数。
- 自动 v2→v3 升级还要求精确的四阶段 v2 行为、completed v2 journal、当前 instance/config/variant、GGUF 指纹和确定性 v2 SHA 共同匹配；缺一项就在 unload 前阻断。v3 应用失败、用户取消或 Codex Commit 失败时恢复相同 v2，而不是错误退回内置模板。
- 自动持久化目前只对本机 loopback LM Studio `0.4.21.x` 与 `0.4.23.x` 已确认的 per-model defaults JSON 结构开放。`0.4.22.x`、`0.4.24.x`、prerelease、畸形或缺失版本证据，远程 endpoint，目标 defaults 文件缺失/过大/过深，根结构不符，路径包含 reparse point/junction，concrete identity 不唯一或 Prompt Template 为未知自定义内容时，自动 Switch 会在任何文件写入和 unload 前 fail closed；只读分析、导出和 LM Studio My Models 手工设置仍可用。
- 自动路径不写 GGUF。它只在严格 concrete identity 对应的 per-model defaults 中新增精确 v3、把有 completed provenance 的精确 v2 升级为 v3，或对精确 v3 No-op；除 `llm.load.promptTemplate` 外的 preset、operation/load 参数与未知 JSON 属性保持语义不变。目标 `/load` 明确不发送 REST `prompt_template`，必须由无运行时覆盖的重载、配置复核和四阶段全 PASS 证明持久 default 生效。
- schema-v4 在写 defaults 前创建并验证 CurrentUser DPAPI 精确备份。失败恢复优先处理持久字段：整个文件仍为候选时恢复原始字节，存在无关并发变化时只恢复管理器拥有的 Prompt Template，字段被外部替换为未知内容时进入 `RecoveryBlocked` 且不覆盖。手工撤销仍由用户在 LM Studio 中明确操作；不要删除其他 defaults 字段或修改 GGUF。
- 普通 Provider、刷新和 Preview 的共享 HTTP 客户端仍为 3 分钟；四阶段 Responses 探针每阶段仍为 45 秒。只有 LM Studio 精确 unload/load、状态复核和事务恢复使用 30 分钟生命周期客户端，自动回滚另有独立 30 分钟 token。这个预算用于容纳已观察到约 8–10 分钟的 87.2 GiB 模型加载，不代表网络/推理故障会让普通界面等待 30 分钟。
- `/load.model` 只接受 native model `key` 的行为以 LM Studio 0.4.21 实测为准；`selected_variant` 不是 load ID。管理器仍要求 load 前后 `selected_variant`、量化、架构、max context 和完整配置一致，因此使用源 key 不代表允许静默回退到默认量化。
- LM Studio native API 与 `lms ps` 可能短暂显示不同 loaded 状态。安全切换只信任 `/api/v1/models` 的 loaded instance/config；`lms ps` 只贡献严格匹配后的本机文件路径与 concrete identity 证据。2026-08-24 只读实测的 esatapedico NVFP4 模型实际 context/Max 均为 `262144`，native 与 CLI 的 quantization 都缺失；缺失值不会按文件名猜成 `NVFP4`，只有双方都缺失才视为一致，`lms ls --json --variants` 候选也采用相同的可空精确比较。当前重载形态中 `identifier` 是 loaded ID，`modelKey` 是完整 source/load key；旧形态中 `modelKey` 也可能等于 loaded ID。定位器只接受这两种 modelKey 语义，且 native source 为完整 `.gguf` 相对路径时，还要求 `lms ps` 所有存在的 `path`/`indexedModelIdentifier` 与其一致。以上现场值不会成为代码常量，也不会由 CLI 反向覆盖 native。`lms ls` 与 `lms ps` 的两个有效路径冲突、任一 CLI 失败/超时/非法 JSON、字段冲突、歧义、越界或非 GGUF 时自动定位均 fail closed；手工选择只获得只读分析能力，不能生成持久化 concrete identity。
- 崩溃恢复事务不会在应用启动时静默写回或重载；存在未完成、回滚失败或 recovery-blocked 事务时会阻止新的 Preview/Switch，但刷新 native 状态及 **检查/恢复未完成事务** 始终可用。schema-v1/v2 旧 journal 的 provenance 可能不完整；schema-v3 记录 runtime-only provenance，schema-v4 还记录 concrete defaults 身份、原/候选指纹、DPAPI 备份与持久阶段。恢复评估同时把当前 instance 和 defaults 指纹纳入 state fingerprint；执行恢复时先验证/恢复 defaults，再处理实例。只有持久状态安全、唯一归因、完整配置一致、GGUF 未变且原运行时签名精确复现时才允许关闭；若 native 状态出现多实例歧义或 Prompt Template 被外部改成未知内容，恢复会停止而不是猜测或覆盖。
- 不执行 DeepSeek 官方 setup script；在线下载失败时使用带 provenance 的发行快照，用户应关注缓存抓取时间。
- OpenAI App Server 不可达时 model cache 可能过期，UI 会标记 stale。
- 新凭据只提供 Windows Credential Manager command-backed 模式；不自动创建/修改用户环境变量。
- 已有 DeepSeek plaintext bearer 为保证官方脚本互操作而继续使用；用户可自行重新配置为 Credential Manager 模式，但本工具不会偷偷复制明文。
- Process 检测使用名称、产品描述、路径与子进程线索，可能出现保守型 false positive；首版不提供强制关闭。
- 非 loopback LM endpoint 只接受 HTTPS。
- LM Studio 可以同时加载 LLM 与 embedding；管理器会显示两者，但只允许已知 `llm`（或 fallback 中类型 Unknown 且满足其他安全门槛）的候选进入切换流程。
- Restore 只处理快照明确登记的 `config.toml`、`models.json` 和 opt-in supplemental TOML，不恢复整个 `.codex`，因此不会回滚 Thread/Project/session 数据。
- 本轮没有在当前 Codex 会话中对真实 NVFP4 instance 执行 schema-v4 defaults 写入、unload/reload、LM Studio 重启后四阶段、Codex Plan→执行或 Provider Commit；这些明确保持 **Untested**。一次模板分析成功、一次 HTTP 200 或历史 schema-v3 `Completed` 都不是持久兼容证明。Plan、Goal、Web Search、Image、Computer Use、Parallel Tools、Skills 等没有直接端到端证据时也保持 Untested。
- 2026-08-22 的 GUI smoke 已按用户要求中止且不计为 PASS：用户观察到重复错误框（疑似 Git 无法启动），并明确禁止在当前 Codex 运行期间再次启动 `CodexModelManager.exe`。本轮只确认该 EXE 无残留进程；错误未通过重启复现或修复，保持 Untested/Unresolved。

## 2026-08-24 审阅修复后的验证边界

- WinForms 现在在 composition 生命周期内注册统一的 `Application.ThreadException` 处理器，凭据保存/状态刷新也进入同一异步错误路径；异常会先写脱敏日志，再显示脱敏后的类型与消息。隔离 STA 自动化已经覆盖这些代码路径。
- 上述修复只确认了先前缺失的全局异常边界。2026-08-22 用户看到的“重复错误框（疑似 Git 无法启动）”没有在真实 GUI 中重新复现，因此不得据此宣称该历史现象已彻底解决；其真实 GUI 状态仍为 **Untested / Unresolved**。
- 本轮未启动可见主程序，未执行真实 Provider 切换、LM Studio `/load`/`/unload`、真实 Credential Manager 写入或真实长上下文任务。隔离 STA、fake process/HTTP、临时目录和纯文本回归均已验证；真实 GUI 字体/DPI、真实关窗长任务回滚以及 Provider 端到端行为继续明确为 **Untested**。
- `<32位 GUID>.json` 命名的损坏 LM Studio transaction journal 仍会硬阻断恢复枚举；只忽略不符合 journal 命名约定的无关 JSON。这是故意保留的 fail-closed 安全边界。

## 2026-08-24 NVFP4 定位与分析修复边界

- `esatapedico/Qwen3.8-27B-NVFP4-MTP-HIGHEST` 的自动 GGUF 定位、只读模板分析和 UI 空路径防御已通过自动化及真实只读验证；这不等于运行时模板已经应用。
- 本次发布版 GUI smoke 已完成“刷新模型 → 自动填入精确路径 → 分析 Prompt Template → 正常关闭”，未出现“操作失败”对话框；此前另一次 GUI 运行观察到的疑似 Git 重复错误没有在本次限定路径中复现，仍不将其扩大解释为所有 GUI 场景均已验证。
- 当前原始实例仍呈现 Basic/Leading/Conversation `200`、Continuation `500`，failure code 为 `lmstudio-chat-template-system-order`。在 Codex 完全关闭后完成事务式模板重载、四阶段全 PASS、配置 Commit 和新 Codex 任务前，真实 Provider 切换继续标记为 **Untested**。
