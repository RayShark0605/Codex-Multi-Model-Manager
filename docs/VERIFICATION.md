# Final Verification

验证日期：2026-08-22（Asia/Shanghai）。

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