# Final Verification

验证日期：2026-08-20（Asia/Shanghai）。自动写入测试全部使用临时目录；发布版 GUI smoke 同时设置随机临时 `CODEX_HOME` 与 `CMM_LOCALAPPDATA_OVERRIDE`。真实 LM Studio 只执行 native list、四阶段 Responses、随机不存在模型的 `prompt_template` schema probe 与 GGUF metadata 读取；没有执行真实 unload/load，也没有修改用户 Codex 配置或现有 transaction journal。

## Build / test / static checks

| 检查 | 结果 |
|---|---|
| Release solution build | PASS，0 warning / 0 error |
| xUnit solution | 163 total：156 PASS / 0 FAIL / 7 SKIP |
| 普通测试结果 | `artifacts\test-results\final-v3-postdocs-tests.trx` |
| `dotnet format --verify-no-changes --no-restore` | PASS |
| `git diff --check` | PASS |
| NuGet `--vulnerable --include-transitive` | 5 个项目均未报告已知 vulnerable package |
| 生产代码 Secret 形态扫描 | 0 hit；测试中的 `sk-...fixture` 仅为脱敏测试值 |
| 变更源码 TODO/FIXME/HACK/NotImplemented/Console 调试扫描 | 0 hit |
| 模板正文日志扫描 | 0 hit；日志只记录阶段、实例/变体与短哈希 |

七个普通运行中跳过的测试均为显式 opt-in：三个 `LiveLmStudio` 只读用例、一个只读 incomplete-journal 用例、一个 `LiveLmStudioMutation` 生命周期用例、一个 `LiveCodexSmoke` 和一个指定实物路径的 `LiveGguf`。随后显式执行了安全的只读部分：

- `LiveLmStudio`：3 PASS / 0 FAIL / 1 SKIP，最终结果为 `artifacts\test-results\live-lm-v3-readonly-final.trx`；唯一 SKIP 是当前没有 incomplete journal。
- Qwen3.8 Q6_K、Qwen3.8 Q8_0、Qwen3.6 Q4_K_M 三个实物 GGUF 各 1 PASS / 0 FAIL，结果分别为 `live-gguf-v3-qwen38-q6.trx`、`live-gguf-v3-qwen38-q8.trx`、`live-gguf-v3-qwen36-q4.trx`。

非 live 回归除既有 Provider/TOML/backup/secondary override/Responses/SSE/tool/GGUF 安全矩阵外，本轮新增或强化：

- 四阶段请求体严格为 Basic、Leading Developer、Conversation Control、Continuation Developer；步骤 3/4 的唯一输入差异是最后一个 user 前是否插入后置 developer。
- HTTP 200 但没有 JSON `output` 数组仍判失败；前置阶段失败后不发送后续请求。
- v2 的 `200 / 200 / 200 / 500` 精确分类为 `lmstudio-chat-template-continuation-instruction-order`；Conversation Control 自身失败不会误归类。
- `ResponsesCompatibilityClient` 只有四阶段全部 PASS 才继续 streaming/tool/reasoning，并把四项独立状态暴露给 UI。
- v3 对完整 messages 收集所有 system/developer，保持相对顺序与双换行；reasoning 前缀及 tools/user/assistant/tool/reasoning sentinel 均保留。
- v3 主 conversation 循环在调用 `render_content` 前显式跳过已合并的 system/developer，避免重复输出及 vision 计数等隐藏副作用。
- 原始 Qwen、精确 v2、精确 v3、未知 Marker、混合换行和未知结构的保守分类；v2/v3 确定性重建必须满足预期 SHA-256。
- v2→v3 只有在 completed v2 journal、instance/config/variant、GGUF 指纹、行为签名与 v2 SHA 共同匹配时才允许进入 unload 前阶段。
- journal schema v3 保存 BuiltIn/ManagerRule provenance、原/目标规则、哈希、evidence transaction 与原四阶段摘要；schema v1/v2 继续只读兼容。
- v3 load 或验证失败时恢复确定性 v2 对象模板，而非错误恢复内置模板；恢复必须重新得到 v2 的前三项 PASS/Continuation 精确失败。
- 崩溃恢复覆盖无实例、已知补丁、多实例歧义、响应缺 ID、状态漂移和同 ID 已恢复 v2；最后一种只关闭 journal，零 unload/load。
- 任一四阶段未全 PASS 时，Codex 配置写入次数为零。

## Real LM Studio read-only verification

最终只读复核时 LM Studio 0.4.21 的 authoritative native 状态：

- instance ID / source model key：`qwen/qwen3.8-27b`
- selected variant：`qwen/qwen3.8-27b@q6_k`
- architecture / quantization：`qwen35` / `Q6_K`
- actual loaded context：`70144`
- completed v2 provenance：`595a50afb4d342098626100d577aaa08`，旧规则 `qwen-leading-instructions-v2`

对当前 v2 运行时的实时四阶段证据：

| 阶段 | HTTP | `output` 数组 | 判定 |
|---|---:|---:|---|
| Basic Control | 200 | Yes | PASS |
| Leading Developer | 200 | Yes | PASS |
| Conversation Control | 200 | Yes | PASS |
| Continuation Developer | 500 | No | FAILED |

最终失败码为 `lmstudio-chat-template-continuation-instruction-order`，`IsCompatible=false`。这证明旧 v2 能通过前导与普通多轮请求，但仍会在 Plan→Default 一类后置 developer 更新处于 Jinja 渲染阶段失败。

不含任何 Prompt/错误正文的最终结构化现场证据保存为 `artifacts\test-results\live-lm-v3-probe-final.json`；捕获时间为 `2026-08-20T22:19:16.6264461+08:00`。

只读 live planner 进一步证明：当前 instance ID、Q6_K variant、70144 context、完整 load config、Q6_K GGUF 指纹、completed journal 与确定性 v2 SHA 全部匹配，可创建 `ManagerRule/v2 → v3` 升级计划；计划前后 loaded instance ID 集合不变，没有新增 journal，也没有 unload/load。

随机不存在 model key 的 schema probe 返回预期 `404/model_not_found`，证明当前 0.4.21 运行时接受顶层对象形态 `prompt_template` 并已越过请求 schema。它不是模板生效证明；正式流程仍以 load response、native config 复核与四阶段最终 PASS 为准。

## Prompt Template verification

| GGUF | Source SHA-256 | v2 SHA-256 | v3 SHA-256 |
|---|---|---|---|
| Qwen3.8-27B Q6_K | `C3CF9E34ABF4F9E36C2D72165AA9C132D3E2A725B6C2586AAA3A8AF9D7A81041` | `AA8741EB5D416E5E481B8E3BFE530CEEC25A005B1CCD384E6670FEA51B147531` | `4AA5CC42C084FCC8235AAF0500835F4F9419A72280EA7E02D08EEE9A97807D8B` |
| Qwen3.8-27B Q8_0 | `C3CF9E34ABF4F9E36C2D72165AA9C132D3E2A725B6C2586AAA3A8AF9D7A81041` | `AA8741EB5D416E5E481B8E3BFE530CEEC25A005B1CCD384E6670FEA51B147531` | `4AA5CC42C084FCC8235AAF0500835F4F9419A72280EA7E02D08EEE9A97807D8B` |
| Qwen3.6-35B-A3B Q4_K_M | `E84F32A23FDDA27689F868AA4A1A5621F41133E51A48D7F3EFCBEA2839574259` | `9E6CDDF08965594E25EE1678C9E823BB6413F51847964D3FD0A84DF1E14A2D73` | `235C3E8D316D80E23827174F1A8CEF37B1E5018CF70ED8F52F2C6FB9C0E233CD` |

三项均仅读取 GGUF v3 metadata header。SHA 只用于实物/事务身份与漂移检测，业务放行仍要求模板锚点精确且唯一匹配，并在运行时完成四阶段探测。

## Published artifacts

目录：`D:\MyProjects\Codex Multi-Model Manager\artifacts\publish\win-x64`

| 文件 | Bytes | SHA-256 | File version |
|---|---:|---|---|
| `CodexModelManager.exe` | 71,957,186 | `EF1C506359C07AAFCF8478E387A759AE0286622479F559E1BF8E71F75981B320` | `1.0.0.0` |
| `helpers\credential\CodexModelManager.CredentialHelper.exe` | 35,424,843 | `DBD79E6584D4DF546BEA04E6CB4B296BF5C2107BE87A56537B50D90D1F4CC67F` | `1.0.0.0` |
| `helpers\mcp\CodexModelManager.TestMcpServer.exe` | 35,092,453 | `91345B6E9480E045AD4232B2721C3206EF27ABEFAC0AF900452EDC0F31A9294A` | `1.0.0.0` |

发布参数为 `win-x64`、self-contained、single-file、`PublishTrimmed=false`。`publish.ps1` 在测试/发布前显式清除所有 live/mutation opt-in 环境变量，因此发布过程不会误触真实 LM Studio 生命周期。

## Runtime smoke

- 隐藏启动最终发布 EXE：进程存活，UI thread `Responding=true`。
- 隔离 `config.toml` 启动前后 SHA 均为 `ABC8B19D3F5D195DB674FA4CBA5C9065201B09706D37FAD4819BE7F6044AFCBB`。
- 隔离 appsettings、Initial Snapshot 与 `transactions` 目录创建成功，incomplete transaction 数为 0；仅停止本次精确 PID，并在绝对路径校验后删除随机临时目录。
- GUI smoke：`artifacts\test-results\gui-smoke-v3.json`。
- Credential Helper 非法参数契约：exit 2、stdout 0 bytes、stderr 0 bytes。
- MCP Helper：`initialize`、`tools/list`、`tools/call(cmm_ping)` 均有效，返回 `CMM_PONG`，exit 0、stderr 0 bytes。
- Helper smoke：`artifacts\test-results\helper-smoke-v3.json`。

MCP helper 的 `CMM_PONG` 仅验证发布 helper 协议，不等同于“Codex agent 通过当前 Qwen 调用 LM Studio”的模型侧 smoke。

## Authoritative user state and remaining end-to-end step

最终只读复核时，真实 `C:\Users\xr\.codex\config.toml` 为 5427 bytes，SHA-256 `B2A3A7CD819B20304A017AA6202B96B85F28B7A9DEF8F445FD540C2C1C75B079`；Provider 仍为 implicit `openai`，model 为 `gpt-5.6-sol`，reasoning 为 `xhigh`。现有 LM Studio journals 均为 `Completed` 或 `RolledBack`，没有 incomplete transaction。

真实 v3 生命周期注入、补丁后四项 200、隔离 Codex agent `CMM_PONG`、真实配置 Commit、重启后调用以及 Plan→Default 无害工具闭环仍未在本开发会话中执行。原因是当前会话本身运行在 Codex/ChatGPT Desktop 中，产品门禁会在写 journal 和 unload 之前阻断；没有绕过该门禁。

完成现场闭环时应：完全关闭 Codex Desktop/ChatGPT Desktop、CLI 与 helper → 启动本页记录的新 EXE → Preview 当前 Q6_K/70144 v2→v3 升级 → 确认运行时模板事务 → 确认四项均 200/PASS → 执行隔离 Codex smoke → 最终确认 Codex 配置 → 重启 Codex → 执行一次 Plan→Default + 临时目录无害工具任务。任一步失败时应保留错误 journal 并按 v3 provenance 恢复 v2；四阶段 PASS 前真实 Codex 配置不会写入。
