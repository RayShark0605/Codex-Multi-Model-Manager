# Final Verification

主验证日期：2026-08-22；最新增量验证：2026-08-27（Asia/Shanghai）。

## 2026-08-27：目录整理与提交前回归

### 提交范围与目录边界

- 本次从 `master @ 942007ea6c0b` 的既有未提交工作开始，清点了 45 个已跟踪修改和 17 个应纳入版本管理的新文件。原有审阅加固、NVFP4 定位、持久模板事务及配套 Core/App 测试、文档作为相互依赖的完整实现集提交为 `dc773548e67a2eb05cfaccfd28a460e33acf2026`；没有拆成不可构建的半成品提交。
- 整理动作仅调整解决方案中 Core 测试项目的归属，让两个测试项目统一位于 `tests` solution folder，保留原有 BOM/CRLF。原有源码和测试文件经前后 SHA-256 复核，字节完全未改写；本次没有借整理目录扩展产品行为。
- `.gitignore` 继续排除 `bin/obj`、NuGet/.NET/应用本地缓存、日志、测试原始工件及发布产物。根目录 `nul` 已确认只是 34 bytes 的命令错误输出；删除被本次工具执行策略阻止，因此保留本地，并新增精确的 `/nul` 忽略项，不能把 Git clean 解释成该文件已从磁盘删除。
- 原始 tracked patch、17 个未跟踪源码/文档副本及文件 SHA 清单保留在 `artifacts\repository-cleanup-2026-08-27\`；`nul` 的逐字节副本为同目录的 `removed-nul-output.txt`。这些整理备份均被忽略，不进入提交。
- 暂存只使用逐项核对的显式路径；没有纳入 EXE/DLL、真实用户配置、凭据、事务 journal、DPAPI 备份、TRX、日志或 `.user` 文件。高置信度凭据模式扫描唯一命中为拒绝 URL userinfo 的负例测试 `http://user:pass@localhost:1234/`，不是实际凭据。

### 本轮重新执行的验证

| 检查 | 命令 | 结果 |
|---|---|---|
| Core Release | `dotnet test .\tests\CodexModelManager.Tests\CodexModelManager.Tests.csproj -c Release` | **344 PASS / 0 FAIL / 8 opt-in SKIP** |
| App Release | `dotnet test .\tests\CodexModelManager.App.Tests\CodexModelManager.App.Tests.csproj -c Release` | **13 PASS / 0 FAIL / 0 SKIP** |
| Debug build | `dotnet build .\CodexModelManager.sln -c Debug --no-restore` | **PASS，0 warning / 0 error** |
| Release build | `dotnet build .\CodexModelManager.sln -c Release --no-restore` | **PASS，0 warning / 0 error** |
| 格式 | `dotnet format .\CodexModelManager.sln --verify-no-changes --no-restore` | **PASS** |
| 差异空白 | `git diff --check`、`git diff --cached --check` | **PASS** |
| 发布 | `.\publish.ps1` | **PASS**；重新运行 Release build 和两套测试后发布三个 EXE |

本轮合计 **357 PASS / 0 FAIL / 8 预期 SKIP**。所有 live 开关在验证进程中关闭；8 个跳过全部来自现有显式 opt-in 用例，没有新增或放宽跳过条件。

TRX、构建/格式/发布日志、按测试 outcome 汇总的 `test-summary.json` 和 `publish-artifacts.json` 位于 `artifacts\test-results\repository-cleanup-2026-08-27\`。本次重新发布的三个 EXE 的长度和 SHA-256 均与下方 2026-08-24 持久修复发布表一致；主程序仍为 `artifacts\publish\win-x64\CodexModelManager.exe`，72,041,391 bytes，SHA-256 `840E60F4768F39A02D231D62CB2CD0B3EEDCAC0189D809ADE459E282EFC95DDC`。发布产物只保留在本地，不跟踪到 Git。

### 本轮未执行的行为

本次只整理、验证和本地提交，没有推送，没有启动管理器可见窗口，没有写真实 Codex/LM Studio defaults、执行 Provider Commit、调用 LM Studio `/load` 或 `/unload`，也没有重启 Codex/LM Studio。下方 2026-08-24 的 live 模型、配置哈希和事务记录是当日历史证据，本次没有将其重新判定为当前状态。LM Studio 重启后的四阶段探针与全新 Codex Plan→执行端到端验收仍保持 **Untested**。

## 2026-08-24：NVFP4 Plan→执行持久 Prompt Template 修复

### 本轮根因与实现结论

- 旧 schema-v3 事务只把 `qwen-interleaved-instructions-v3` 注入当时的 loaded instance。LM Studio 重启/重载后，目标模型因 per-model defaults 中没有 `llm.load.promptTemplate` 而重新采用 GGUF 内置模板；Plan 后新增的 developer 控制消息再次命中内置模板的 `System message must be at the beginning`。本轮没有修改 Codex 的 Plan 消息序列，也没有弱化 Continuation Developer 探针。
- 当前重载形态的 `lms ps --json` 已重新确认：`identifier=qwen3.8-27b-nvfp4-mtp` 是 native loaded instance ID；`modelKey=esatapedico/qwen3.8-27b-nvfp4-mtp-gguf/qwen3.8-27b-nvfp4-mtp-highest.gguf` 是 source/load key。定位器现在严格区分两种职责，同时仍兼容旧形态 `modelKey=loaded ID`；任何第三种值、空字段或其他身份/配置冲突均阻断。
- locator 只从 `lms ps` 已验证的精确物理路径产生 concrete model identifier。正式修复只支持本机 loopback LM Studio `0.4.21.x`，目标为：

  `C:\Users\xr\.lmstudio\.internal\user-concrete-model-default-config\esatapedico\Qwen3.8-27B-NVFP4-MTP-GGUF\Qwen3.8-27B-NVFP4-MTP-HIGHEST.gguf.json`

- 新 `LmStudioPerModelDefaultsStore` 对 concrete 路径、models/defaults root、reparse point、JSON 大小/深度/结构、字段重复和未知自定义模板全部 fail closed。它只新增精确 v3、升级具有 completed provenance 的精确 v2，或对精确 v3 No-op；除 `llm.load.promptTemplate` 外的 JSON 语义保持不变。
- schema-v4 流程按 `Prepared journal → CurrentUser DPAPI 备份并回读校验 → 原子 defaults 写入/复核 → 再次漂移检查 → 精确 unload → 不含 REST prompt_template 的 load → 完整配置复核 → 四阶段 PASS → defaults 再复核 → Codex Commit → Completed/PersistentDefaultVerified` 执行。回滚先恢复持久字段，再处理实例；外部替换 Prompt Template 时进入 `RecoveryBlocked`，不会覆盖用户内容或继续 unload/load。
- UI 新增八种持久状态、旧 runtime-only Completed 的重载漂移诊断、包含 defaults 路径/前后 SHA/Add-Upgrade-No-op 的 Preview，以及统一的 busy/模型/path 按钮状态。Refresh 和 Preview 仍为只读。

LM Studio 官方文档确认 per-model defaults 会用于从应用及 `lms load` 发起的后续加载，并允许模型级 Prompt Template 覆盖：[Per-model Defaults](https://lmstudio.ai/docs/app/advanced/per-model)、[Prompt Template](https://lmstudio.ai/docs/app/advanced/prompt-template)。

### 自动化、格式与发布

| 检查 | 最终结果 |
|---|---|
| Core Release | 344 PASS / 0 FAIL / 8 opt-in SKIP |
| App Release | 13 PASS / 0 FAIL / 0 SKIP |
| `dotnet format .\CodexModelManager.sln --verify-no-changes --no-restore` | PASS |
| `git diff --check` | PASS |
| `publish.ps1` | PASS；Release build 0 warning / 0 error，并再次执行同一全量测试 |

八个 Core SKIP 均为显式 opt-in live/lifecycle 测试；本轮新增的持久 Preview live 测试属于预期只读 opt-in，不会在普通回归中触发真实文件写入或模型生命周期。全量 TRX：

- `artifacts\test-results\persistent-v3-fix\core-release.trx`
- `artifacts\test-results\persistent-v3-fix\app-release.trx`

新增回归覆盖当前/旧 `lms ps` 身份形态、所有 fail-closed locator 反例、安全 concrete defaults 映射、v3 Add/No-op、带 provenance 的 v2 Upgrade、未知/重复/非法/超限 JSON、DPAPI 往返与损坏、完整/字段级恢复、并发漂移、schema-v4 阶段/路径/SHA 防篡改、无 REST `prompt_template` 的成功加载、defaults 写入前失败零 lifecycle、load/配置/hierarchy/Commit 失败回滚、BuiltIn/v2 原状态恢复、崩溃阶段恢复、外部模板改写 RecoveryBlocked、UI 状态/颜色/重载漂移分类与按钮生命周期。

### 2026-08-24 21:28 只读 live 复核

| 字段 | 重新发现值 |
|---|---|
| endpoint / LM Studio | `http://127.0.0.1:1234` / `0.4.21.0` |
| native loaded ID | `qwen3.8-27b-nvfp4-mtp` |
| native source / CLI modelKey | `esatapedico/qwen3.8-27b-nvfp4-mtp-gguf/qwen3.8-27b-nvfp4-mtp-highest.gguf` / 同值 |
| CLI identifier | `qwen3.8-27b-nvfp4-mtp` |
| CLI path / indexed identity | `esatapedico/Qwen3.8-27B-NVFP4-MTP-GGUF/Qwen3.8-27B-NVFP4-MTP-HIGHEST.gguf` / 同值 |
| type / format / architecture / quantization | `llm` / `gguf` / `qwen35` / native 与 CLI 均缺失 |
| loaded / max context | `262144` / `262144` |
| 精确 GGUF | `J:\LM Studio Models\esatapedico\Qwen3.8-27B-NVFP4-MTP-GGUF\Qwen3.8-27B-NVFP4-MTP-HIGHEST.gguf` |
| 原模板 / v3 SHA-256 | `12827F24B742EA4E80CDC12DBCF9622227056B9F797252A3149263D4F9AAADCE` / `9DC0DA000D1DF280BE9F6F64D314EB52879C0DF5C3C951F74105964136592F85` |
| 当前 defaults | 1,067 bytes / `E94F602B80ADC475C4BAE1896D1C7246738E37CA191FB4CDA78E91C9B9CCA8D0` / Prompt Template 字段 0 / Preview=`Add` |
| 当前 Codex config | 5,627 bytes / `7C27544E22001F108EF7B6C81166B5FB8B9E1C6AB61841E803E6BC21ED63B4DF` / implicit OpenAI / `gpt-5.6-sol` |

- locator + 持久 Preview：2 PASS / 0 FAIL / 0 SKIP，`artifacts\test-results\persistent-v3-fix\live-readonly-final.trx`。
- 真实 GGUF 只读分析：1 PASS / 0 FAIL，`artifacts\test-results\persistent-v3-fix\live-gguf-final.trx`。
- Preview 前后 Codex config 与 defaults 的长度/SHA 均完全一致；没有创建该 Preview 的 transaction journal，没有 `/load`、`/unload` 或 Provider Commit。结构化证据：
  - `artifacts\test-results\persistent-v3-fix\live-readonly-snapshot.json`
  - `artifacts\test-results\persistent-v3-fix\live-final-invariants.json`
  - `artifacts\test-results\persistent-v3-fix\final-state.json`（18 个 journal，未完成数 0，管理器进程数 0）

### 发布产物

| 文件 | Bytes | SHA-256 | File version |
|---|---:|---|---|
| `artifacts\publish\win-x64\CodexModelManager.exe` | 72,041,391 | `840E60F4768F39A02D231D62CB2CD0B3EEDCAC0189D809ADE459E282EFC95DDC` | `1.0.0.0` |
| `artifacts\publish\win-x64\helpers\credential\CodexModelManager.CredentialHelper.exe` | 35,486,259 | `B499D8E9CE09E8013131B420E6692893F97AD97723113181866EEA0E84E588FA` | `1.0.0.0` |
| `artifacts\publish\win-x64\helpers\mcp\CodexModelManager.TestMcpServer.exe` | 35,092,926 | `C1E9641256BE9E0C91142706DD3000EDF78E59775079A59C42EAB2B1E1E7E000` | `1.0.0.0` |

### 当前验收边界

源码、自动化、发布和当前机器只读验证已完成；但当前 Codex 会话仍在运行，因此没有写真实 defaults、没有 unload/reload、没有提交 Provider，也没有执行 LM Studio 重启后的四阶段与全新 Codex Plan→执行。当前状态必须记为：**代码与只读验证 PASS；最终持久端到端验收待用户关闭全部 Codex 后执行**。只有持久字段正确、LM Studio 重启后四阶段仍 PASS、全新 Codex Plan→执行与工具调用成功三项同时成立，才能报告“已完全修复”。

## 2026-08-24：esatapedico NVFP4 自动定位与 Prompt Template 分析修复（历史阶段，以上持久修复已取代 runtime-only 完成语义）

### 修复结论

- LM Studio native `/api/v1/models` 对当前模型合法返回 `quantization=null`；定位器不再把 quantization 当作 loaded snapshot 必填字段，但仍要求 native 与 `lms ps` 双方都缺失，或者双方都有相同非空值。`lms ls --json --variants` 候选也使用同一可空精确规则；单边缺失和不同值继续 fail closed。
- 该历史阶段首次确认 native source 是完整 `.gguf` 相对路径，并建立了对 `path`/`indexedModelIdentifier` 的严格校验；当时观测到旧加载形态中的 `modelKey` 可等于 loaded ID。当前重载形态及两种受支持的 `modelKey` 语义以上一节的最新 live 证据为准；始终不从 `NVFP4` 名称猜测量化。
- “分析 Prompt Template”现在只有在选择已加载 LLM 且 GGUF 路径非空时才启用；空路径底层入口返回可操作的中文业务错误，不再暴露 `ArgumentException(filePath)`。

### 自动化与静态检查

| 检查 | 结果 |
|---|---|
| Core Release | 314 PASS / 0 FAIL / 7 opt-in SKIP |
| App Release | 12 PASS / 0 FAIL / 0 SKIP |
| 定位器专项 | 38 PASS / 0 FAIL |
| UI 专项 | 12 PASS / 0 FAIL |
| `dotnet format --verify-no-changes --no-restore` | PASS |
| `git diff --check` | PASS |
| Release publish | PASS，0 warning / 0 error |

全量 TRX 位于 `artifacts\test-results\nvfp4-fix\`。新增回归覆盖完整 source-path、native/CLI 双空 quantization、单边/不同 quantization、`path` 与 `indexedModelIdentifier` 冲突、至少一个路径身份字段、format/type/architecture/context/loaded ID/publisher 不一致、越界/缺失/非 GGUF/歧义，以及分析按钮和空路径防御。

### 当前 native、CLI 与 GGUF 只读证据

| 字段 | 当前值 |
|---|---|
| instance ID | `qwen3.8-27b-nvfp4-mtp` |
| native source | `esatapedico/qwen3.8-27b-nvfp4-mtp-gguf/qwen3.8-27b-nvfp4-mtp-highest.gguf` |
| type / architecture / format | `llm` / `qwen35` / `gguf` |
| native / CLI quantization | `null` / 缺失 |
| loaded context / max context | `262144` / `262144` |
| 精确文件 | `J:\LM Studio Models\esatapedico\Qwen3.8-27B-NVFP4-MTP-GGUF\Qwen3.8-27B-NVFP4-MTP-HIGHEST.gguf` |
| locator provenance | `lms ps --json` |

- live locator + GGUF test：2 PASS / 0 FAIL，`artifacts\test-results\nvfp4-fix\nvfp4-live-readonly-final.trx`。
- live Responses/hierarchy 分类：1 PASS / 0 FAIL，`artifacts\test-results\nvfp4-fix\nvfp4-live-compatibility-final.trx`。
- GGUF v3、architecture `qwen35`、原模板 SHA-256 `12827F24B742EA4E80CDC12DBCF9622227056B9F797252A3149263D4F9AAADCE`，状态 `Supported`，规则 `qwen-interleaved-instructions-v3`，修补后 SHA-256 `9DC0DA000D1DF280BE9F6F64D314EB52879C0DF5C3C951F74105964136592F85`。
- 原始运行时四阶段仍为 Basic/Leading/Conversation `200`、Continuation `500`，failure code `lmstudio-chat-template-system-order`；这是下一步事务式模板修复的输入，不是已完成的运行时兼容证明。

before、after、final、模板、hierarchy 和不变量证据分别保存在：

- `artifacts\test-results\nvfp4-fix\nvfp4-live-before.json`
- `artifacts\test-results\nvfp4-fix\nvfp4-live-after.json`
- `artifacts\test-results\nvfp4-fix\nvfp4-live-final.json`
- `artifacts\test-results\nvfp4-fix\nvfp4-template-analysis.json`
- `artifacts\test-results\nvfp4-fix\nvfp4-hierarchy-before-repair.json`
- `artifacts\test-results\nvfp4-fix\nvfp4-final-invariants.json`
- `artifacts\test-results\nvfp4-fix\transaction-state-final.json`

最终不变量全部为 `true`：`C:\Users\xr\.codex\config.toml` 前后均为 5545 bytes、SHA-256 `95B7CFCE62289B54AD0C8AAE1BDC91F4E52C18E84DD0E74A46B6A836D2FC643A`、模型 `gpt-5.6-sol`、隐式 OpenAI Provider；loaded instance/source/context、CLI identity/path 和 GGUF 长度/时间戳也均未变化。现有 17 个 transaction journal 全部为 `Completed` 或 `RolledBack`，未完成数为 0，本次只读验证没有创建新事务。

### 发布版 GUI smoke

- 发布版启动后进入 LM Studio 页，刷新成功自动填入上述精确 GGUF 路径，“分析 Prompt Template”按钮为 Enabled。
- 执行只读分析后显示 `Supported | SHA256 12827F24B742...`，没有“操作失败”窗口；管理器随后正常关闭且无残留进程。
- 结构化结果与截图：`artifacts\test-results\nvfp4-fix\nvfp4-gui-smoke.json`、`artifacts\test-results\nvfp4-fix\nvfp4-gui-analysis-pass.png`。

### 发布产物

| 文件 | Bytes | SHA-256 | File version |
|---|---:|---|---|
| `CodexModelManager.exe` | 72,012,406 | `5146A136F5FBCA4E5BC6CBB38EE7BBA8F009FCE1A5920FC0AE8B87F5C1CA0219` | `1.0.0.0` |
| `helpers\credential\CodexModelManager.CredentialHelper.exe` | 35,461,432 | `8385DF9B33452682D0ECD55A41E683DD2D608C65D1D454ECCA451354A425C859` | `1.0.0.0` |
| `helpers\mcp\CodexModelManager.TestMcpServer.exe` | 35,092,926 | `C1E9641256BE9E0C91142706DD3000EDF78E59775079A59C42EAB2B1E1E7E000` | `1.0.0.0` |

### 仍未执行的最终验收

本次未调用 LM Studio `/load` 或 `/unload`，未应用运行时模板，未 Commit Provider，也未启动切换后的新 Codex 任务。必须先完全关闭当前 Codex，再由发布版执行事务式模板重载、四阶段全 PASS、Codex 配置 Commit 和重启验证；在此之前，“真实 Provider 切换”继续标记为 **Untested**。

## 验证边界

本轮交付严格遵守只读现场边界：

- 没有调用 LM Studio `/load` 或 `/unload`；
- 没有把兼容 Prompt Template 应用到正在运行的实例；
- 没有写入 `C:\Users\xr\.codex\config.toml`；
- 没有切换真实 Codex Provider；
- 没有修改 GGUF、LM Studio settings、Credential Manager 或既有 transaction journal；
- 现场验证仅包括 native/CLI 状态读取、GGUF metadata 读取、临时目录模板导出和文件指纹比较。

用户明确要求停止运行 `CodexModelManager.exe` 后，本轮没有再次启动该程序。GUI smoke 不计为 PASS，详见“GUI 验证状态”。

## 构建、测试与静态检查

| 检查 | 结果 |
|---|---|
| 修改前基线 | 163 total：156 PASS / 0 FAIL / 7 opt-in SKIP |
| 修改后 Release 测试 | 197 total：190 PASS / 0 FAIL / 7 opt-in SKIP |
| 最终 TRX | `artifacts\test-results\final-q6-k-xl-offline.trx` |
| Release publish | PASS，0 warning / 0 error |
| `dotnet format --verify-no-changes --no-restore` | PASS |
| `git diff --check` | PASS |
| 生产源码敏感信息形态扫描 | 0 hit |
| 变更生产源码调试标记扫描 | 0 hit |

七个 SKIP 均为显式 opt-in 的现场测试，不会在普通回归中执行真实生命周期或真实 Codex Agent 操作。

### 自动化覆盖

#### GGUF 定位器

新增 endpoint-aware 的 `ILmStudioModelFileLocator.ResolveAsync(ModelProfile, Uri, CancellationToken)` 和结构化 `LmStudioModelFileResolutionAttempt`。定位器专项测试覆盖：

- 当 `lms ls --json --variants` 完全没有 Unsloth 模型时，仍可由 `lms ps --json --host <host> --port <port>` 唯一定位当前 Q6_K_XL；
- native loaded instance 的 identifier/source、type、architecture、quantization、context 与 CLI 候选逐项一致；
- publisher/source 等价匹配，同时支持 native source key 不带 publisher 的现场形状；
- 路径必须位于配置的 downloads/models 根目录，必须真实存在且扩展名严格为 `.gguf`；
- source、identifier、publisher、架构、量化、context、文件类型、文件存在性、路径越界、重复候选和 `lms ls`/`lms ps` 冲突全部 fail closed；
- 非 loopback endpoint、CLI 不存在、启动失败、超时、输出过大、非法 JSON、无匹配和歧义返回稳定脱敏诊断；
- 任一 CLI 数据面失败不会被另一个数据面的表面成功掩盖；
- 不把 CLI/native 的 size 与单个 GGUF 文件长度强制相等。

#### Prompt Template

新增当前 184 行 Unsloth prefix-merged-system 模板的独立精确结构族：

- 源 fixture：`src\CodexModelManager.Core\LmStudio\Templates\qwen-prefix-merged-system-source.jinja`；
- 源模板 SHA-256：`12827F24B742EA4E80CDC12DBCF9622227056B9F797252A3149263D4F9AAADCE`；
- 确定性 `qwen-interleaved-instructions-v3` SHA-256：`9DC0DA000D1DF280BE9F6F64D314EB52879C0DF5C3C951F74105964136592F85`。

规则不按模型名称或 SHA allowlist 放行，而是要求 canonical fixture 与全部关键结构唯一、完整匹配。测试覆盖：

- 任意位置、多次交错的 system/developer 按原始顺序收集，以双换行合并为唯一初始 system block；
- 主 conversation 循环跳过所有已合并指令，并且不会先调用 `render_content`；
- tools、vision、reasoning、反向扫描、user、assistant、tool response、tool-call 和 generation-prompt 分支保持不变；
- generation 确定、可重建并带 v3 marker；
- CRLF 可按受控规范处理，混合换行拒绝；
- 每个关键锚点的 one-change near-match、重复锚点和非目标分支变化均返回 `Unsupported Template`；
- patch 后再次执行完整 canonical 与结构复验。

#### 探测、provenance 与恢复

- failure code 继续使用 `lmstudio-chat-template-system-order`；
- Leading Developer 阶段失败会说明“前导独立指令被拒绝”；
- Continuation Developer 阶段失败会说明“模板只接受开头连续指令、拒绝对话中的后置 developer”；
- 当前 BuiltIn provenance 只接受精确四阶段形状：Basic、Leading、Conversation 均 PASS，Continuation 以 system-order 签名失败；
- Preview 只生成计划；fake locator/controller 测试确认 Preview 前后无 journal、无 unload/load、无配置写入；
- schema 3 恢复必须比较完整四阶段结果与 failure code；schema 1/2 只保留兼容读取，不降低 schema 3 验证强度；
- 旧 `qwen-leading-instructions-v2` 升级和恢复路径继续独立测试。

## 当前 Q6_K_XL 的只读现场验证

### native 权威快照

| 字段 | 现场值 |
|---|---|
| instance ID | `qwen3.8-27b@q6_k_xl` |
| native source key | `qwen3.8-27b@q6_k_xl` |
| type | `llm` |
| architecture | `qwen35` |
| quantization | `Q6_K_XL` |
| loaded context | `161024` |
| model max context | `262144` |
| selected variant | `null` |

native `/api/v1/models` 仍是 loaded instance 和实际 context 的权威来源。`lms ps` 只贡献文件位置证据，不反向覆盖 native 状态。

### 精确文件解析

`lms ps --json --host 127.0.0.1 --port 1234` 唯一解析到：

`J:\LM Studio Models\unsloth\Qwen3.8-27B-GGUF\Qwen3.8-27B-UD-Q6_K_XL.gguf`

解析 provenance 为 `lms ps --json`。`lms ls --json --variants` 没有当前 Unsloth 文件，但仍保留为 Hub variant 数据面。

### 两项只读 live 测试

| 测试 | 结果 | TRX |
|---|---|---|
| 当前 loaded instance 精确定位 | 1 PASS / 0 FAIL | `artifacts\test-results\live-q6-k-xl-locator.trx` |
| 当前 GGUF 模板识别与临时导出 | 1 PASS / 0 FAIL | `artifacts\test-results\live-q6-k-xl-template.trx` |

完整 before/after 证据保存为：

`artifacts\test-results\live-q6-k-xl-readonly-invariants.json`

其中确认：

- loaded instance 完整快照前后相同；
- 真实 Codex 配置前后均为 5456 bytes，SHA-256 均为 `68D514211B78B939C63D79802B03EB27539E2B021192A0420BBC522D3B4BDE96`；
- 12 个 transaction/lock 文件的名称、长度和 SHA 集合前后完全相同；
- `lifecycle_mutation_executed=false`；
- `codex_provider_switch_executed=false`。

## GUI 验证状态

**状态：`ABORTED_BY_USER / NOT PASS`。**

用户现场观察到每次运行 `CodexModelManager.exe` 都会弹出错误框，其中疑似包含 Git 无法启动的信息；同时当前 Codex 正在运行，本来就不满足真实切换的执行条件。按照用户的明确要求：

- 不再启动 `CodexModelManager.exe`；
- 不再尝试 GUI 自动化；
- 不把先前任何不完整 UI 观察记作 GUI smoke PASS；
- 没有点击 `Switch Model`；
- 没有执行真实 Provider 切换；
- 已确认精确发布路径的 `CodexModelManager.exe` 剩余进程数为 0；
- 已清理本轮遗留的隔离 GUI 临时目录。

结构化记录：

`artifacts\test-results\gui-smoke-q6-k-xl.json`

该记录明确设置 `accepted_as_pass=false`。疑似 Git 启动错误没有通过再次运行 EXE 复现，因此保持“未复现、未解决”，不会误报为已修复。对 `src`/`tests` 的 C# 静态搜索没有发现直接以 `ProcessStartInfo` 启动 `git`/`git.exe` 的代码路径；这只能排除直接调用，不能替代弹窗和日志证据。

## 发布工件

发布目录：

`D:\MyProjects\Codex Multi-Model Manager\artifacts\publish\win-x64`

| 文件 | Bytes | SHA-256 | File version |
|---|---:|---|---|
| `CodexModelManager.exe` | 71,985,490 | `285CFF43A05207A8626847312F691FAE076057FC01429925E83CBBEC8B8EA317` | `1.0.0.0` |
| `helpers\credential\CodexModelManager.CredentialHelper.exe` | 35,440,135 | `30BB244935D434678A8A2A4E1A9E38E4940F67A1B3587D32543524C6A4F52C9C` | `1.0.0.0` |
| `helpers\mcp\CodexModelManager.TestMcpServer.exe` | 35,092,602 | `6BF33CCEE6EF52B9BA752B5C63A168FF0952BBA07F998595947A7CBAE137C871` | `1.0.0.0` |

以上文件已经重新发布，但本轮在用户叫停后没有执行主程序。

## 最终验收结论

| 目标 | 状态 |
|---|---|
| Q6_K_XL 精确 GGUF 自动定位 | **PASS（源码、自动化、只读 live）** |
| 当前 Unsloth 184 行模板精确识别 | **PASS（源码、自动化、只读 live）** |
| 确定性 v3 模板生成与临时导出 | **PASS（源码、自动化、只读 live）** |
| Continuation Developer 准确分类和说明 | **PASS（自动化）** |
| BuiltIn provenance / schema 3 完整恢复签名 | **PASS（自动化）** |
| Preview 无生命周期副作用 | **PASS（fake controller 自动化）** |
| 发布构建 | **PASS** |
| GUI smoke | **ABORTED_BY_USER / NOT PASS** |
| 疑似 Git 启动错误 | **Untested / Unresolved（禁止再次启动 EXE）** |
| 把 v3 模板应用到真实 LM Studio 实例 | **Untested** |
| post-patch 四阶段 runtime PASS | **Untested** |
| 真实 Codex Agent | **Untested** |
| 真实 Codex Provider 切换 | **Untested** |

因此，本轮已经完成并验证了**定位器、模板识别/生成、探测分类、provenance、恢复强度、测试与重新发布**；但没有声称当前正在运行的 Codex 已切换到 LM Studio，也没有声称真实运行时模板已被修补。
## 2026-08-23：上下文溢出与工具调用 JSON 截断修复

### 根因证据

本轮交叉核对了以下现场工件：

- `C:\Users\xr\.lmstudio\server-logs\2026-08\2026-08-23.1.log`
- `C:\Users\xr\.codex\sessions\2026\08\22\rollout-2026-08-22T21-59-05-01a029c4-9993-7393-9f7e-b92a7fb0d722.jsonl`

失败链已确认为：模型生成到 `n_tokens=120063`、`truncated=1`，正在生成的 tool-call arguments JSON 在 `120064` 硬窗口处被截断，LM Studio 的 `handleToolCallGenerationFailed` 随后报告 `Unterminated string in JSON`。Codex UI 的 `stream disconnected before completion` 是 `response.failed` 的上层包装；提高 stream retry/timeout 不会修复已被截断的 JSON。

### 实现与自动化结果

- Auto Compact policy v2：`min(floor(L × 0.80), L - min(24576, floor(L / 2)))`；`L=120064` 得到 `95488`。
- Tool Output：`clamp(floor(L / 50), 2048, 4096)`，并带极小窗口比例保护；`L=120064` 得到 `2401`。
- LM Studio 候选显式写入 `model_auto_compact_token_limit_scope="total"`。
- 偏好 schema 升至 v2；旧公式精确值迁移为 Automatic，其他旧值迁移为 Manual，loaded context 改变时不复用旧手动值。
- `tool_output_token_limit` 已进入受控 root key；Local → OpenAI/DeepSeek 会逐字恢复 provider 历史值，原先不存在则删除本地专用值。
- 手动 compact 高于平衡建议时只警告；达到/超过 context 或不足 1,024 tokens 硬余量仍拒绝。

| 检查 | 结果 |
|---|---|
| 修改前基线 | 190 PASS / 0 FAIL / 7 SKIP |
| 最终 Debug 全量测试 | 206 PASS / 0 FAIL / 7 SKIP |
| 最终 Release 全量测试 | 206 PASS / 0 FAIL / 7 SKIP |
| 隔离 Codex Home SwitchMatrix | 42 PASS / 0 FAIL / 0 SKIP |
| Debug/Release build | PASS，0 warning / 0 error |
| `dotnet format --verify-no-changes --no-restore` | PASS |
| `git diff --check` | PASS |

测试工件：

- `D:\MyProjects\Codex Multi-Model Manager\artifacts\test-results\context-overflow-debug-final.trx`
- `D:\MyProjects\Codex Multi-Model Manager\artifacts\test-results\context-overflow-release.trx`
- `D:\MyProjects\Codex Multi-Model Manager\artifacts\test-results\context-overflow-isolated-roundtrip.trx`

### 真实状态零写入预览

只读验证重新读取 native `/api/v1/models`，当前唯一 loaded LLM 为 `qwen3.8-27b@q6_k_xl`，`context_length=120064`、Max `262144`。使用实际 `ConfigurationSwitchService.CreatePlanAsync` 和实际 LM Studio instruction-hierarchy preflight 生成预览，preflight 为 PASS，候选语义精确为：

```toml
model = "qwen3.8-27b@q6_k_xl"
model_provider = "lmstudio"
model_context_window = 120064
model_auto_compact_token_limit = 95488
model_auto_compact_token_limit_scope = "total"
tool_output_token_limit = 2401
```

验证程序从未调用 `CommitAsync`。真实 `C:\Users\xr\.codex\config.toml` 在预览前后均为 5,542 bytes，SHA-256 均为 `969B94165B0DF735FBE1D769DB12C06D14557DE5D5011B97D94548E6EA63F48D`；provider 仍是隐式 `openai`，model 仍是 `gpt-5.6-sol`。结构化证据：

`D:\MyProjects\Codex Multi-Model Manager\artifacts\test-results\context-overflow-readonly-preview.json`

### 发布工件

| 文件 | Bytes | SHA-256 |
|---|---:|---|
| `D:\MyProjects\Codex Multi-Model Manager\artifacts\publish\win-x64\CodexModelManager.exe` | 71,988,411 | `C8915E37A685598E22596C4585D411B2936CE56BE876D154D3A3D6EB3D3AFDB4` |
| `D:\MyProjects\Codex Multi-Model Manager\artifacts\publish\win-x64\helpers\credential\CodexModelManager.CredentialHelper.exe` | 35,441,726 | `23D7473F2ABC48D515664D3C87028125CB8DE9D888D8030FA7667148819D4187` |
| `D:\MyProjects\Codex Multi-Model Manager\artifacts\publish\win-x64\helpers\mcp\CodexModelManager.TestMcpServer.exe` | 35,092,605 | `2549EA8F0AEF26FA82822925D34E4EC4CEA326D64069440D3413459FB14C9A9C` |

### 尚未验证的运行时边界

按用户要求，本轮没有把候选提交到真实 Codex 配置，也没有把当前 provider 从 OpenAI 切换到 LM Studio。因此“真实长任务能否在约 95.5k 附近及时 compact，且日志不再出现 `120063 / truncated=1 / handleToolCallGenerationFailed / Unterminated string`”仍明确标记为 **Untested**。下次主动切换后应使用新任务或克隆任务验证；如果平衡阈值仍复现，下一档使用安全优先值 `87296`，不要增加 stream retries。

## 2026-08-24：全量代码审阅缺陷修复

### 判定与边界

本轮从 `master @ 942007ea6c0b`、206 passed / 0 failed / 7 skipped 的干净产品基线开始。M1–M6 全部接受；低危 #2–#8、#10–#13、#16–#40 接受，#20 部分接受；#1、#9、#14、#15 拒绝修改产品逻辑。每项的运行时证据、理由、实现和测试名称见 [`REMEDIATION-2026-08-24.md`](REMEDIATION-2026-08-24.md)。

本次验证没有启动可见 `CodexModelManager.exe`，没有写真实 Codex 配置或 Credential Manager，没有执行 Provider 切换、LM Studio `/load`/`/unload` 或真实长上下文任务。WinForms 验证使用专用 STA 线程和临时 composition，不显示窗口。

### 最终构建、测试与静态门禁

| 检查 | 命令/工件 | 结果 |
|---|---|---|
| Debug Solution build | `dotnet build .\CodexModelManager.sln -c Debug --no-restore` | **PASS**，0 warning / 0 error |
| Release Solution build | `dotnet build .\CodexModelManager.sln -c Release --no-restore` | **PASS**，0 warning / 0 error |
| Core Release 全量 | `artifacts\test-results\review-remediation-core-release.trx` | **301 passed / 0 failed / 7 opt-in skipped**（308 total） |
| App Release 隔离 STA | `artifacts\test-results\review-remediation-app-release.trx` | **10 passed / 0 failed / 0 skipped** |
| 合计 | Core + App | **311 passed / 0 failed / 7 skipped**（318 total） |
| 格式 | `dotnet format .\CodexModelManager.sln --verify-no-changes --no-restore` | **PASS** |
| 差异空白 | `git diff --check` | **PASS** |
| 发布脚本 | `.\publish.ps1` | **PASS**；脚本内重新执行 Release build、Core 与 App tests 后发布三个 self-contained win-x64 single-file 产物 |

七个 skipped 仍全部是既有显式 opt-in live 用例；普通验证没有隐式执行真实 LM Studio 生命周期或真实 Codex Agent 请求。既有未跟踪文件 `D:\MyProjects\Codex Multi-Model Manager\nul` 保持原样，没有删除或纳入修复内容。

### 发布工件

| 文件 | Bytes | SHA-256 |
|---|---:|---|
| `D:\MyProjects\Codex Multi-Model Manager\artifacts\publish\win-x64\CodexModelManager.exe` | 72,011,518 | `C3D1A8C427885FF89D9EB43EE498D7F9380F620CCE6E0F451317BB0D21D1E97C` |
| `D:\MyProjects\Codex Multi-Model Manager\artifacts\publish\win-x64\helpers\credential\CodexModelManager.CredentialHelper.exe` | 35,460,715 | `70C7044182C451DC2A7B3285BA49DD0AB71AC879221B76F006617C7724D5FD0B` |
| `D:\MyProjects\Codex Multi-Model Manager\artifacts\publish\win-x64\helpers\mcp\CodexModelManager.TestMcpServer.exe` | 35,092,926 | `C1E9641256BE9E0C91142706DD3000EDF78E59775079A59C42EAB2B1E1E7E000` |

### 尚未验证的运行时边界

- M1 的相对布局、8,000,000 context、日志裁剪、全局异常入口、credential failure、planner 资源释放、关窗 action gate 和 handle 生命周期已由不显示窗口的 STA 测试验证；真实字体/DPI 和人工 GUI 操作仍为 **Untested**。
- 默认 `ThreadException` 缺口已经修复并通过隔离测试，但 2026-08-22 的“重复错误框（疑似 Git 无法启动）”没有重新启动真实 GUI 复现，不能宣称该历史现象已经彻底解决，仍为 **Untested / Unresolved**。
- fake HTTP/process 测试证明了 timeout、SSE 首事件返回、JSON shape、UTF-8、bounded cleanup、端口解析和 cancellation 契约；真实 LM Studio/DeepSeek/OpenAI 网络行为本轮未重跑。
- 真正的关窗长任务取消/回滚、真实凭据服务异常以及真实 Provider 切换仍需在明确允许 live 行为的单独验证窗口中完成。
