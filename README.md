# CipherBox

[English](README.en.md) | 简体中文

> 完全离线的 Windows 文件加密工具：把文件（或整个文件夹）加密成 `.cbx`，需要时再解开。
> 不用联网、不上传、不依赖任何外部程序，明文在查看和编辑时也不落盘。

当前版本 **2.0.0**（开发中，**未经第三方安全审计**，请勿用于生产环境的敏感数据）。

---

## 这是什么

一个朴素的需求：**文件想放硬盘上或发给别人，但不想被随便打开；需要时自己随时能看。**

- **完全离线**：没有任何网络代码，不联网、不上传、不校验授权
- **零外部依赖**：自包含发布，目标机器不需要装 .NET 运行时，一个目录拷过去就能跑
- **明文不落盘**：只读查看不产生明文文件；编辑在内存里完成，直接写回密文
- **三种形态**：单文件、批量（一个文件夹里 N 个独立密文）、归档（整个文件夹打成一个文件）

**系统要求**：64 位 Windows 10 1809 及以上，或 Windows 11。
不支持 Windows 7 / 8.1——.NET 10 运行时已不再支持这些系统。

---

## 功能一览

| 能力 | 说明 |
|---|---|
| 单文件加解密 | 一个文件 → 一个 `.cbx` |
| 批量加密 | 一个文件夹里每个文件各自加密，**每个都能单独解开**；同批共享一个随机批次 ID |
| 批量解密 | 先逐个验证口令、再检查批次一致性，全部通过才开始解密 |
| 文件夹归档 | 整个文件夹打包成**一个** `.cbx`，两个等级（见下） |
| 只读查看 | 不解密出文件，直接在窗口里看文本/图片；内容不落盘 |
| 编辑回写 | 改完重新加密覆盖原文件，需要再输一次口令 |
| 口令强度提示 | 加密时按长度与字符种类给「弱/中/强」三档提示 |
| 加密强度档位 | 快速 / 标准 / 高强度（只改 PBKDF2 迭代次数，格式不变） |
| 算法可选 | AES-256-GCM（硬件加速）或 ChaCha20-Poly1305（无 AES 指令时更快） |
| 中英双语 | 界面语言可切换，词条表完整 |
| 专业模式 | 默认关闭；打开后可见算法、全加密、跳过后缀检查等高级项 |

### 归档的两个等级

| 等级 | 解密前能看到什么 | 能否只导出部分 |
|---|---|---|
| 一般（解密时提供预览） | 输口令后可预览目录结构与名称 | ✅ 可勾选条目导出 |
| 完全（解密时不提供预览） | 只有总大小，连归档名都加密 | ❌ 只能整包解开 |

> **这是产品层面的限制，不是密码学保证。**
> 口令在手的人，用自写工具仍可解出清单。若你需要「连结构都不可解」，那不是本程序能提供的性质。

---

## 安全设计

| 项 | 做法 |
|---|---|
| 对称加密 | AES-256-GCM / ChaCha20-Poly1305，AEAD，每块独立认证 |
| 口令派生 | PBKDF2-HMAC-SHA256，默认 60 万次迭代（快速 10 万 / 高强度 1000 万） |
| 分块 | 1 MiB 一块，大文件不占内存 |
| 完整性 | 改任一字节、重排块、截断文件，解密都会失败并指出哪一块 |
| 明文不落盘 | 查看与编辑都在内存里完成 |
| 重名处理 | 自动加 `(1)`，**绝不覆盖**已有文件 |
| 失败清理 | 出错不留半成品（专业模式可选择性保留以便排查） |

### 能保证的

- 没有口令，读不出内容
- 没有口令，改出来的版本一定解不开
- 只读查看与编辑时明文不落盘

### 不能保证的（本地文件的固有边界）

- 阻止有写权限的人删除或替换密文（拒绝服务，本地无解）
- 阻止截屏、复制文本、调试器读取内存
- 抵御弱口令：离线爆破风险完全取决于口令强度

详见 [SECURITY.md](SECURITY.md)。

---

## 快速开始

### 图形界面

解压发行包，双击 `CipherBox.exe`。把文件或文件夹拖进窗口即可。

### 命令行

```powershell
# 加密（口令交互输入两遍）
enc 报告.docx
enc 报告.docx 备份.cbx

# 查看密文头信息（不需要口令）
dec 备份.cbx --info

# 只读查看，明文不落盘
dec 笔记.txt.cbx --view
dec 笔记.txt.cbx --view 4096

# 还原
dec 备份.cbx
dec 备份.cbx D:\输出目录
```

退出码：`0` 成功；`2` 可预期的失败（口令错 / 文件损坏 / 参数错误）；`1` 未预期异常。

---

## 文件格式（摘要）

扩展名 `.cbx`，所有整数**大端**。当前写 v2，读端兼容 v1。

```
magic[8] "CBXFILE1" | ver u8 | algId u8 | kdfId u8 | flags u8
chunkSize u32 | plainSize u64 | chunkCount u32 | kdfIterations u32
saltLen u8 + salt | nonceLen u8 + baseNonce | nameLen u16 + name
--- v2 追加 ---
kind u8 | batchLen u8 + batchId | archiveFlags u8
```

正文是 N 个独立认证的数据块：`ctLen u32 | ciphertext | tag[16]`

- 块 nonce = `baseNonce[0..7] ‖ BE32(块序号)`
- 块 AAD = `SHA256(头部原始字节) ‖ BE32(块序号)`

完整说明（含归档内部布局、名字加密的 AAD 构造）见 [release/docs/格式说明.md](release/docs/格式说明.md)。

---

## 项目结构

```
CipherBox/
├─ src/CBox.Core/        容器格式、算法注册表、归档与批量编排（唯一实现）
├─ src/CBox.Encrypt/     → enc.exe
├─ src/CBox.Decrypt/     → dec.exe
├─ src/CBox.App/         → CipherBox.exe（WPF 界面）
├─ src/Shared/           两个控制台程序共用的小工具
├─ release/              发布专用：LICENSE、README、格式说明、打包脚本
├─ tools/                冒烟测试与各类检查脚本
├─ build.ps1             一键构建（测试版）
└─ publish.ps1           自包含发布（测试版 / 发布版）
```

格式与算法只有一份实现（`CBox.Core`），三个可执行程序都调用它——避免「界面版和命令行版行为不一致」。

---

## 构建

需要 **.NET 10 SDK** 与 Windows。

```powershell
# 测试版（含诊断开关、故障注入）
powershell -ExecutionPolicy Bypass -File .\build.ps1

# 只产发布版，并附上文档
powershell -ExecutionPolicy Bypass -File .\release\publish-release.ps1
```

发布版产物在 `release\dist\CipherBox-v<版本>-win-x64\`。

---

## 检查脚本

发布前应全部通过：

| 脚本 | 检查内容 |
|---|---|
| `tools/smoke.ps1` | 16 项端到端冒烟测试 |
| `tools/check-privacy.ps1` | 本机路径、用户名、凭据、其他项目痕迹 |
| `tools/check-i18n-pairs.ps1` | 中英词条一一对应、占位符一致 |
| `tools/check-strings.ps1` | 代码用到的词条是否都在表里 |
| `tools/check-i18n-global.ps1` | 硬编码文案、废词条、漏译 |

```powershell
powershell -ExecutionPolicy Bypass -File .\tools\smoke.ps1
powershell -ExecutionPolicy Bypass -File .\tools\check-privacy.ps1
```

---

## 开发状态

已完成 1.1 → 2.0：容器格式 v1/v2、命令行双端、图形界面、只读查看器、编辑回写、
国际化、强弱档位、日志分级、双版本体系、文件夹/归档/批量加密、归档预览、口令强度提示、应用图标、安装包。

计划中：Argon2id、公钥模式（P-256 + ML-KEM）、正式发布 2.0。

---

## 下载

Windows 安装包与免安装压缩包见 [Releases](https://github.com/GuCunF/cipherbox/releases)。

| 文件 | 说明 |
|---|---|
| `CipherBox-v2.0.0-Setup.exe` | 安装包（自包含 .NET 运行时，无需额外安装） |
| `CipherBox-v2.0.0-win-x64.zip` | 免安装版（需目标机已安装 .NET 10 桌面运行时） |

## 代码签名

Windows 发布包计划通过 **[SignPath Foundation](https://signpath.org/)**（面向开源项目的免费代码签名项目）完成代码签名；
申请进行中，签名后的版本会在 Release 页标注。

在此之前，Windows 可能提示"未知发布者"。请以本仓库的 Release 页与下方校验值为准：

发布包会同时附上 SHA256，可用 `Get-FileHash <文件> -Algorithm SHA256` 核对。

### 如果弹出「Windows 已保护你的电脑」

**这是 Windows 对未签名程序的默认提示，不是病毒警告，也不是本程序的行为。**

从网络下载的文件会被 Windows 打上"来自网络"的标记，双击时就会拦一下。放行方法：

1. 点提示框里的 **「更多信息」**
2. 这时会出现 **「仍要运行」** 按钮
3. 点它即可

如果你不放心，可以先用命令行核对文件：

```powershell
Get-FileHash CipherBox-v2.0.1-Setup.exe -Algorithm SHA256
```

把结果和 Release 页面列出的 SHA256 对比，一致就说明文件没被篡改。

> 提示：把安装包放在桌面或下载文件夹里双击，比放在自建目录里更容易触发这个拦截。

## 许可

[MIT](LICENSE)