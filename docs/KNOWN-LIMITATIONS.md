# Known Limitations

## 已确认

1. **已测试的 Qwen GGUF Prompt Template 与 Codex 指令层级不兼容，除非用户应用并验证 override。**
   - 差分请求证明 `instructions + user` 返回 200，只增加独立 developer 消息便返回 `System message must be at the beginning`。
   - 失败发生在 Jinja 渲染、模型生成、shell、file editing 和 MCP 之前；因此普通 Responses 或单独 function-call PASS 不能升级 Codex Agent 状态。
   - 管理器现在将其分类为 `lmstudio-chat-template-system-order`，并在 Preview/Commit、备份和任何真实配置写入之前硬阻止。
   - 可为结构精确匹配的 GGUF 导出兼容模板，但必须由用户在 LM Studio 手动应用和重载；成功导出本身不等于兼容，重载后的实时差分和 L3 才是依据。
   - 同模型不同量化可能共享同一错误模板，但仍按 loaded instance 单独检测；不会按名称或量化继承 PASS。
   - 本机 Qwen3.6 的源模板 SHA 为 `E84F32A23FDDA27689F868AA4A1A5621F41133E51A48D7F3EFCBEA2839574259`；已检查的 Qwen3.8 Q6_K/Q8_0 为另一结构与 SHA `C3CF9E34ABF4F9E36C2D72165AA9C132D3E2A725B6C2586AAA3A8AF9D7A81041`，后者还有 `reasoning_instructions` 前缀。`qwen-leading-instructions-v2` 对两种结构分别做唯一锚点验证，但这些 SHA 只是审计证据，不是按名称或哈希放行的 allowlist。
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

- 不自动 load/unload LM 模型，不更改 GPU offload、KV cache 或 context。
- 不自动写 LM Studio per-model Prompt Template 设置；修补器只读 GGUF 并输出独立工件。撤销 override 也由用户在 LM Studio 中明确操作。
- LM Studio native API 与 `lms ps` 可能短暂显示不同 loaded 状态；最终审计实际观察到 native `loaded_instances=0`，同时 `lms ps` 显示 Qwen3.8 Q6_K `IDLE`/`131072`。安全切换只信任 `/api/v1/models` 的 `loaded_instances[].config.context_length`，缺失时拒绝而不是猜测。
- 不执行 DeepSeek 官方 setup script；在线下载失败时使用带 provenance 的发行快照，用户应关注缓存抓取时间。
- OpenAI App Server 不可达时 model cache 可能过期，UI 会标记 stale。
- 新凭据只提供 Windows Credential Manager command-backed 模式；不自动创建/修改用户环境变量。
- 已有 DeepSeek plaintext bearer 为保证官方脚本互操作而继续使用；用户可自行重新配置为 Credential Manager 模式，但本工具不会偷偷复制明文。
- Process 检测使用名称、产品描述、路径与子进程线索，可能出现保守型 false positive；首版不提供强制关闭。
- 非 loopback LM endpoint 只接受 HTTPS。
- LM Studio 可以同时加载 LLM 与 embedding；管理器会显示两者，但只允许已知 `llm`（或 fallback 中类型 Unknown 且满足其他安全门槛）的候选进入切换流程。
- Restore 只处理快照明确登记的 `config.toml`、`models.json` 和 opt-in supplemental TOML，不恢复整个 `.codex`，因此不会回滚 Thread/Project/session 数据。
- Plan、Goal、Web Search、Image、Computer Use、Parallel Tools、Skills 等没有直接端到端证据时保持 Untested。
