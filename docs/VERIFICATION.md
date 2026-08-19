# Final Verification

验证日期：2026-08-18（Asia/Shanghai）。所有自动写入测试均使用临时 `ICodexHomeProvider`；最终发布版 GUI smoke 同时设置随机临时 `CODEX_HOME` 与 `CMM_LOCALAPPDATA_OVERRIDE`。

## Build / test

| 检查 | 结果 |
|---|---|
| Debug test build | PASS，0 warning / 0 error |
| Release solution build | PASS，0 warning / 0 error |
| xUnit | 112 total：109 PASS / 0 FAIL / 3 SKIP |
| 跳过项 | 仅三个显式 opt-in fixture：`LiveLmStudio`、`LiveCodexSmoke`、`LiveGguf` |
| 本机 GGUF opt-in 测试 | Qwen3.8 Q6_K、Qwen3.8 Q8_0、Qwen3.6 Q4_K_M：各 1 PASS / 0 FAIL |
| `dotnet format --verify-no-changes` | PASS |
| `git diff --check`（空仓库 intent-to-add diff） | PASS |
| NuGet `--vulnerable --include-transitive` | 5 个项目均未报告已知 vulnerable package |
| 产品源码 Secret 形态扫描 | 0 literal secret-shaped hit |
| TODO/FIXME/HACK/NotImplemented 扫描 | 0 hit |

非 live 回归包括：

- 六种 Provider 双向转换、unknown section/MCP/Project/comment/BOM/换行保留、损坏/重复 TOML。
- context/compaction、LM `on/off` reasoning 清除、精确 reasoning intersection、DeepSeek catalog/version。
- hierarchy control/Codex-shaped 差分、system-order/developer-role/401/timeout/畸形 JSON 分类、响应结构校验、unsafe endpoint 与敏感 URL 拒绝。
- Preview/Commit 两次 LM preflight；每次先从 native API 重新核对 loaded instance 与实际 context，再发送 hierarchy 请求。instance 缺失/context 变化时不发送推理、零 History、零 config/provider-state 写入；成功后继续原有原子事务。
- GGUF v2/v3、截断/未知类型/异常长度/重复 template key、两种精确 Qwen 模板变体、stale source/hash 拒绝、manifest 无绝对路径或 Token。
- Secondary Override、quoted dotted table、Initial/History/supplemental restore、外部修改、文件锁、多文件 rollback、Secret redaction 和官方 `backup-deepseek` 共存。

普通 Release 测试结果文件：`artifacts\test-results\final-tests.trx`。三项实物 GGUF 结果分别为 `live-gguf-qwen38-q6.trx`、`live-gguf-qwen38-q8.trx`、`live-gguf-qwen36-q4.trx`。

最终状态还显式运行了 `LiveLmStudio`：Server 与模型发现成功，但因 native API 报告 0 个 `loaded_instances` 而安全 Skip，未发送 Responses 推理；记录为 `live-lm-current.trx`。在这一前提下没有运行 L3，避免把 `lms ps` 的 IDLE 条目误当作 native loaded instance。

## Prompt Template verification

| GGUF | Source SHA-256 | Patched SHA-256 | Rule |
|---|---|---|---|
| Qwen3.8-27B-Uncensored Q6_K | `C3CF9E34ABF4F9E36C2D72165AA9C132D3E2A725B6C2586AAA3A8AF9D7A81041` | `AA8741EB5D416E5E481B8E3BFE530CEEC25A005B1CCD384E6670FEA51B147531` | `qwen-leading-instructions-v2` |
| Qwen3.8-27B-Uncensored Q8_0 | `C3CF9E34ABF4F9E36C2D72165AA9C132D3E2A725B6C2586AAA3A8AF9D7A81041` | `AA8741EB5D416E5E481B8E3BFE530CEEC25A005B1CCD384E6670FEA51B147531` | `qwen-leading-instructions-v2` |
| Qwen3.6-35B-A3B Q4_K_M | `E84F32A23FDDA27689F868AA4A1A5621F41133E51A48D7F3EFCBEA2839574259` | `9E6CDDF08965594E25EE1678C9E823BB6413F51847964D3FD0A84DF1E14A2D73` | `qwen-leading-instructions-v2` |

三项均仅读取 GGUF v3 metadata header。Qwen3.8 的实际 patched template 另经 Python 3.12 / Jinja 3.1.4 离线语义渲染：

- `system + developer + user` 只产生一个 system block，内容按原顺序并以两个换行分隔。
- `system + system + user` 同样只产生一个 system block。
- tools 分支保留唯一 system block，tool 声明与高优先级指令顺序正确。
- user 之后出现 system/developer 仍抛出明确异常。

SHA 仅用于记录所测实物与防止分析后文件变化；业务代码仍要求所有结构锚点精确且唯一匹配，不按文件名、模型名或 SHA 白名单放行。

## Published artifacts

目录：`artifacts\publish\win-x64`

| 文件 | Bytes | SHA-256 |
|---|---:|---|
| `CodexModelManager.exe` | 71,881,468 | `29098F9F49B2355EECA17C2330D2FD9069F9F28DF3F5D5C852185841243759E6` |
| `helpers\credential\CodexModelManager.CredentialHelper.exe` | 35,365,445 | `10382E9E5D5FA8C23A7CFAB62EAF3C77E68188E0676351602D2A26E63EB65306` |
| `helpers\mcp\CodexModelManager.TestMcpServer.exe` | 35,092,362 | `0FED6E130208CCB5AD8848B6EDC765D53594506E9179C9BA8F0AFFA493F26CC0` |

发布参数为 `win-x64`、self-contained、single-file、`PublishTrimmed=false`。

## Runtime smoke

- 隐藏启动最终发布 EXE：进程存活、UI thread `Responding=true`。
- 隔离 config 启动前后 SHA 一致。
- 临时 Initial Snapshot 与隔离输入 SHA 一致，manifest operation 为 `InitialSnapshot`。
- 临时 `appsettings.json`、Credential Helper 和 MCP Helper 均创建成功。
- Credential Helper 的非法参数契约：exit 2、stdout 0 bytes、stderr 0 bytes。
- MCP Helper：`initialize`、`tools/list`、`tools/call(cmm_ping)` 三个 JSON-RPC 响应有效，返回 `CMM_PONG`，stderr 0 bytes。
- smoke 后仅停止该次生成的管理器 PID；随机临时目录已按绝对路径与 `%TEMP%` 前缀复核后删除。

## Authoritative-state check

最终真实 `%USERPROFILE%\.codex\config.toml`：5390 bytes，SHA-256 `FB2E44DD795360D6744F62C1524A76966EB8B7ECDA95B269FACD2078EE778B8C`。真实 Provider 仍是 implicit `openai`，模型仍为 `gpt-5.6-sol`，reasoning 仍为 `max`；本轮没有执行真实 Provider 切换。

最终只读 `/api/v1/models` 返回 16 个模型、0 个 `loaded_instances`；同一时刻 `lms ps` 显示 `qwen/qwen3.8-27b@q6_k` 为 `IDLE`、context `131072`。本工具不把两者猜测合并，因此当前 post-template Responses/L3 没有运行且真实 LM 切换仍会被硬阻止。用户在 LM Studio 手动确认模型被 Server 实际加载、应用 override 并重载后，必须重新执行 hierarchy probe；只有 control 与 Codex-shaped 都是 200 才能继续 Preview/Switch。
