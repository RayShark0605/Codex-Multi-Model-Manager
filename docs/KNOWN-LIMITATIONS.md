# Known Limitations

## 已确认

1. **Qwen 内置模板以及旧 `qwen-leading-instructions-v2` 都不能由单轮成功推导完整 Codex 兼容。**
   - 内置模板的差分请求证明 `instructions + user` 返回 200，只增加独立 developer 消息便返回 `System message must be at the beginning`。
   - 旧 v2 可让 Basic、Leading Developer、无后置 developer 的多轮 Conversation Control 三项返回 200，但 Plan→Default 会在历史后追加 developer，随后精确返回 `System and developer messages must precede conversation messages.`。当前 Unsloth prefix-merged-system 内置模板同样只收集开头连续指令，现场四阶段形状为 200/200/200/500，但 Continuation 错误仍是 `System message must be at the beginning.`；planner 仅把这一完整形状识别为 BuiltIn provenance。
   - 失败发生在 Jinja 渲染、模型生成、shell、file editing 和 MCP 之前；因此普通 Responses 或单独 function-call PASS 不能升级 Codex Agent 状态。
   - 管理器分别分类为 `lmstudio-chat-template-system-order`、`lmstudio-chat-template-developer-role` 和 `lmstudio-chat-template-continuation-instruction-order`，并在 Preview/Commit、备份和任何真实配置写入之前硬阻止。
   - 对三个已识别失败码且结构精确匹配的 GGUF，可由用户先预览当前运行时来源、原始/v2/v3 哈希、目标模板和完整加载配置，再明确确认事务式运行时 unload/load；成功导出、成功 POST 或成功 load 都不等于兼容，重新列举的配置保持及四阶段实时差分才是依据。手工导出/重载仍是回退路径。
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

## 设计边界

- 常规发现、刷新、测试与 Preview 不自动 load/unload。只有失败码为 `lmstudio-chat-template-system-order`、`lmstudio-chat-template-developer-role` 或 `lmstudio-chat-template-continuation-instruction-order`，精确变体与 GGUF 均可证明且用户确认后，Switch 才执行事务式 unload/load；它保留 native API 暴露的加载配置，而不是选择新的 context、量化、GPU/KV 或 speculative 参数。
- 自动 v2→v3 升级还要求精确的四阶段 v2 行为、completed v2 journal、当前 instance/config/variant、GGUF 指纹和确定性 v2 SHA 共同匹配；缺一项就在 unload 前阻断。v3 应用失败、用户取消或 Codex Commit 失败时恢复相同 v2，而不是错误退回内置模板。
- 运行时 `prompt_template` 对象已由本机 LM Studio 0.4.21 schema 证实，但尚未进入公开 REST 参数文档，属于版本相关能力。请求被拒绝、响应不回显 `load_config`、重载配置漂移或 hierarchy 不 PASS 时自动回滚；未知版本不会仅凭版本号放行。
- 自动路径不写 GGUF 或持久化 LM Studio per-model override；补丁只属于当前加载实例。手工导出路径仍只读 GGUF 并输出独立工件，手工 override 的撤销仍由用户在 LM Studio 中明确操作。
- `/load.model` 只接受 native model `key` 的行为以 LM Studio 0.4.21 实测为准；`selected_variant` 不是 load ID。管理器仍要求 load 前后 `selected_variant`、量化、架构、max context 和完整配置一致，因此使用源 key 不代表允许静默回退到默认量化。
- LM Studio native API 与 `lms ps` 可能短暂显示不同 loaded 状态。安全切换只信任 `/api/v1/models` 的 loaded instance/config；`lms ps` 只贡献严格匹配后的本机文件路径证据。2026-08-22 只读实测为 Unsloth Qwen3.8 Q6_K_XL、实际 context `161024`、Max `262144`；这些不会成为固定值，也不会由 CLI 反向覆盖 native。`lms ls` 与 `lms ps` 的两个有效路径冲突、任一 CLI 失败/超时/非法 JSON、字段冲突、歧义、越界或非 GGUF 时自动定位均 fail closed，手工选择仍可用。
- 崩溃恢复事务不会在应用启动时静默重载；存在未完成/回滚失败事务时会阻止新的 Preview/Switch，但刷新 native 状态及 **检查/恢复未完成事务** 始终可用。schema-v1/v2 旧 journal 的 provenance 可能不完整；schema-v3 会记录 BuiltIn/ManagerRule、规则/SHA/evidence transaction 和原四阶段摘要。只有唯一归因、完整配置一致、GGUF 未变且原运行时签名精确复现时才允许关闭；若 native 状态出现无法归因的多实例歧义，恢复会停止而不是猜测卸载目标。
- 不执行 DeepSeek 官方 setup script；在线下载失败时使用带 provenance 的发行快照，用户应关注缓存抓取时间。
- OpenAI App Server 不可达时 model cache 可能过期，UI 会标记 stale。
- 新凭据只提供 Windows Credential Manager command-backed 模式；不自动创建/修改用户环境变量。
- 已有 DeepSeek plaintext bearer 为保证官方脚本互操作而继续使用；用户可自行重新配置为 Credential Manager 模式，但本工具不会偷偷复制明文。
- Process 检测使用名称、产品描述、路径与子进程线索，可能出现保守型 false positive；首版不提供强制关闭。
- 非 loopback LM endpoint 只接受 HTTPS。
- LM Studio 可以同时加载 LLM 与 embedding；管理器会显示两者，但只允许已知 `llm`（或 fallback 中类型 Unknown 且满足其他安全门槛）的候选进入切换流程。
- Restore 只处理快照明确登记的 `config.toml`、`models.json` 和 opt-in supplemental TOML，不恢复整个 `.codex`，因此不会回滚 Thread/Project/session 数据。
- 本轮没有对真实 Q6_K_XL 执行模板重载、post-patch 四阶段、Codex Agent 或 Provider 切换；这些明确保持 Untested。Plan、Goal、Web Search、Image、Computer Use、Parallel Tools、Skills 等没有直接端到端证据时也保持 Untested。
- 2026-08-22 的 GUI smoke 已按用户要求中止且不计为 PASS：用户观察到重复错误框（疑似 Git 无法启动），并明确禁止在当前 Codex 运行期间再次启动 `CodexModelManager.exe`。本轮只确认该 EXE 无残留进程；错误未通过重启复现或修复，保持 Untested/Unresolved。
