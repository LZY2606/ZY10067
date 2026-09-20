# MicroFocus 显微多焦平面合成台

显微镜操作员对同一视野采集多张不同焦平面的图像（有时补拍局部区域）。本系统读取每张图的
**像素、倍率、载台坐标、Z 高度、采集时间**，校验视野是否可对齐，计算**逐像素局部清晰度**并生成
候选合成图；网页上可以查看每个输出像素/瓦片来自哪张输入、手工改选来源或排除带尘点的帧。

原始证据一经接收**不可原地改写**，派生物（合成版本）**带规则版本、参数和输入指纹**；
导出包含合成图、来源掩膜、可机器读取的变换矩阵，导入端**逐像素复核来源索引**。

## 技术栈与离线说明

- .NET 10 SDK，ASP.NET Core Minimal API，纯静态 HTML/CSS/JS 前端（无外部 CDN/前端构建）。
- 无任何第三方 NuGet 包依赖：测试只用本机缓存的 `xunit` / `Microsoft.NET.Test.Sdk`，
  图像编解码（P5/P6 PGM/PPM、PNG）、Zip 导出全部用 BCL 手写，**测试与运行不访问外部网络**。

## 安装、测试、启动

```bash
dotnet restore
dotnet test --nologo && dotnet run --project src/Web --urls http://127.0.0.1:5203
```

打开 <http://127.0.0.1:5203>。

数据默认落在 `src/Web/data`，可用环境变量覆盖：

```bash
MICROFOCUS_DATA=/srv/microfocus dotnet run --project src/Web --urls http://127.0.0.1:5203
```

## 目录结构

```
src/Core
  Models.cs                  领域模型（帧/任务/合成/瓦片/版本/导入审计）
  Imaging/PnmCodec.cs        P2/P3/P5/P6 PNM 编解码（无外部图像库）
  Imaging/Affine.cs          帧像素 ↔ 画布像素 的 2x3 仿射变换
  Imaging/PngEncoder.cs      网页预览用的极简 PNG 编码器
  Storage/EvidenceStore.cs   内容寻址、只写一次的原始证据
  Storage/JsonStore.cs       原子写 JSON 实体库（临时文件+rename）
  Storage/DerivativeStore.cs 版本派生物（合成图/掩膜/清单）
  Services/AlignmentPlanner.cs  对齐校验、倍率推断表、画布并集
  Services/Compositor.cs     窗口拉普拉斯能量清晰度 + 逐像素选源 + 可信度分类
  Services/CompositeService.cs 候选处理/取消/中断恢复/乐观并发
  Services/CompositeEditing.cs 排除帧、手工改选瓦片、脏瓦片重算
  Services/VersionPublisher.cs 不可变版本发布、旧版本重发/草稿
  Export/PackageService.cs   导出 zip + 导入逐像素审计
  Maintenance/RecoveryService.cs 故障恢复/证据校验
src/Web                      Minimal API + wwwroot 前端
tests/Core.Tests             26 个离线测试（领域 + 内存版 HTTP 端到端）
```

## 核心规则（随版本固化）

- `alignment-1.0`：对齐与几何规则版本。
- `focus-stack-1.0`：选源算法版本（窗口拉普拉斯能量，7×7 窗口，最近邻采样）。
- 倍率未给像素尺寸时，按内置物镜标定表（4/10/20/40/63/100x → 标称 μm/px）推断，
  推断区域可信度降级，并写入诊断 `PIXEL_SIZE_INFERRED`。
- 载台坐标单位缺失（或坐标缺失）→ 该帧**不参与对齐**，诊断 `STAGE_UNIT_MISSING`，
  绝不在几何里“猜”一个单位。
- 画布只覆盖可对齐帧在载台坐标上的**并集**，画布最精细尺度，**不做边缘外推、不无声拉伸凑满**。
- 像素可信度（写入 `trust.u8`，网页有叠加色）：
  - `0 Trusted` 同倍率下 ≥2 帧可交叉验证；
  - `1 SingleFrame` 仅单帧（含补拍独占区、无法交叉验证）；
  - `2 ScaleMissing` 覆盖帧的像素尺寸是推断的；
  - `3 MagnificationConflict` 该像素上的帧倍率互相矛盾；
  - `4 NoCoverage` 无帧覆盖（并集内不应出现；失败瓦片用此显式标红）；
  - `5 ManualOverride` 操作者手工指定来源。
- 一个合成按 64×64（测试可配 32）切成瓦片，**所有瓦片 Validated 才允许发布**；
  任何瓦片失败 → 候选 `Failed`，拒绝发布，失败原因显式可见（如 `TILE_NO_COVERAGE`）。

## 状态流转

```
Pending → Processing → Ready（全部瓦片验证通过）→ Publishing → Published
                 │           │
                 ├─ 取消 → Cancelled（不替换上一已发布版本）
                 └─ 有失败瓦片/异常 → Failed（可“重试处理”或“只重算脏瓦片”）
Published 是不可变版本；在其之上再编辑会进入新草稿，下一次发布只会【追加】新版本。
```

- **并发**：每个合成带 `revision`。编辑/发布必须带当前 rev；两个浏览器基于同一旧版本提交时，
  后到者收到 **409 `REVISION_CONFLICT`** 与当前 rev。网页弹窗展示冲突内容，可选
  “放弃本地改动刷新”或“在最新状态上重新合并”（重新确认瓦片来源后再提交），**后到者不会静默覆盖**。
- **中途取消**：只影响未发布候选；`PublishedVersionId` 与已写入的版本文件保持不变。
- **进程崩溃**：启动时 `RecoverInterrupted()` 把停留在 `Processing/Publishing` 的候选标记为
  `Failed` 并写明“上一已发布版本未受影响，可重试”。

## 不可变证据与版本指纹

- 上传字节写入 `data/evidence/<sha前2位>/<sha>.bin`：临时文件 + 原子 rename，只写一次。
  同字节且同采集元数据才去重；**同名文件以不同字节重新上传会得到新帧 ID/新指纹**，
  已发布版本里的指纹仍然指向旧证据，不会被“偷换”（有专门测试
  `Version_KeepsOldFingerprint_WhenSameNameFileReuploaded`）。
- 每个版本 `manifest.json` 固化：规则版本、参数、每个输入帧的 SHA-256/尺寸/倍率/Z/载台/采集时间、
  帧→画布的 2×3 仿射矩阵（含逆矩阵）、排除帧列表、每瓦片分数与可信度统计、父版本。

## 导出 / 导入（逐像素）

导出 zip：

| 文件 | 内容 |
| --- | --- |
| `composite.pgm` | P5 8-bit 合成灰度图 |
| `source-index.u16` | 大端 uint16，逐像素来源帧在变换帧表中的 1-based 索引，0=无覆盖 |
| `trust.u8` | 逐像素可信度代码（见上表） |
| `manifest.json` | 规则版本、参数、输入指纹、2×3 变换矩阵、瓦片分数 |

导入流程（`POST /api/jobs/{jobId}/imports`）：

1. 按 SHA-256 在任务已有**不可变证据**中匹配输入指纹；缺失/被篡改直接判失败。
2. 从原始证据重新做对齐、重算变换矩阵并与 manifest 逐元素比对。
3. 用相同规则与 manifest 中的排除/手工选源重算每个像素的来源索引和可信度，
   与包内 `source-index.u16`、`trust.u8`、`composite.pgm` **逐像素**核对。
4. 输出 `PixelAudit`（检查像素数、来源不一致数、可信度不一致数、缺失指纹、错误明细），
   全部一致才 `Verified`，并记录每次导入结果。

## HTTP API 摘要

```
POST   /api/jobs                                 创建任务
GET    /api/jobs                                 任务列表
POST   /api/jobs/{jobId}/frames                  multipart 上传帧（file + 倍率/坐标/Z/单位…）
GET    /api/jobs/{jobId}/frames
GET    /api/frames/{frameId}/preview.png         帧缩略图（原始字节不落派生改写）
POST   /api/jobs/{jobId}/composites              新建候选合成
POST   /api/composites/{id}/process              生成/重试候选（重算全部瓦片）
POST   /api/composites/{id}/reprocess            只重算脏瓦片
POST   /api/composites/{id}/cancel               中途取消（不替换已发布版本）
POST   /api/composites/{id}/frames/{fid}/exclusion   排除/恢复帧（带 revision）
POST   /api/composites/{id}/tiles/{i}/source     改选瓦片来源或恢复自动（带 revision）
GET    /api/composites/{id}/tiles/{i}/preview.png?overlay=trust|source
POST   /api/composites/{id}/publish              全部瓦片验证通过才发布
GET    /api/composites/{id}/versions
POST   /api/composites/{id}/republish            用旧版本指纹追加发布新版本
POST   /api/composites/{id}/draft-from-version   从旧版本生成可编辑草稿
GET    /api/versions/{vid}/preview.png?overlay=trust|source
GET    /api/versions/{vid}/export                导出 zip
POST   /api/jobs/{jobId}/imports                 导入并逐像素审计
GET    /api/jobs/{jobId}/imports
POST   /api/admin/recovery                       恢复扫描 + 证据哈希校验
```

冲突响应：HTTP 409
`{"error":"REVISION_CONFLICT","message":"…","currentRevision":N}`；业务校验失败统一 422 且带中文诊断。

## 故障恢复演练（新维护者可照做一遍）

目标：演示三类故障——证据损坏、派生物丢失、进程中断——以及如何确认系统没有用坏数据静默发布。

1. **准备**：`dotnet restore`，`dotnet test --nologo` 应 26/26 通过；
   `dotnet run --project src/Web --urls http://127.0.0.1:5203`，网页新建任务、上传至少两张
   同视野不同 Z 的 PGM（载台单位填 `um`、倍率 20、像素尺寸 0.32），生成候选并发布 v1，下载导出包。
2. **证据被篡改/损坏**：
   ```bash
   # 找到某帧 sha（任务帧列表或 data/frames/*.json 里的 sha256）
   find data/evidence -name '*.bin'          # 内容寻址文件
   # 故意改一个字节（演练后请丢弃该 data 目录）
   printf '\xff' | dd of=data/evidence/xx/<sha>.bin bs=1 seek=100 count=1 conv=notrunc
   curl -s -X POST http://127.0.0.1:5203/api/admin/recovery
   ```
   恢复报告会列出 `corruptEvidence` 且 `healthy=false`；导入审计也会因哈希不符拒绝该版本。
   处置原则：**损坏的原始证据不能“修复成另一个文件”**，只能由操作者重新上传（得到新指纹的新帧），
   再从证据完好的版本重建。
3. **派生物丢失（可再生）**：
   ```bash
   mv data/derivatives/<compositeId>/<versionId> /tmp/missing-version
   curl -s -X POST http://127.0.0.1:5203/api/admin/recovery   # 报告 missingDerivatives
   ```
   原始证据仍然完好时，派生物可从证据再生：在网页上对该版本“用此版本输入重新发布新版本”
   （或“创建草稿”后发布），系统按 manifest 里相同的规则版本、参数、排除/手工选源重算，
   旧版本目录的缺失不影响其他版本。
4. **处理中进程被 kill**：合成处理进行中直接 `kill -9` 服务进程；重新启动后，
   `/api/health` 的 `recoveredInterrupted` 与恢复报告显示被中断候选数，候选被置为 `Failed`
   且提示“上一已发布版本未受影响”。在网页点“重试处理”即可重新跑完所有瓦片。
5. **恢复报告落盘**：每次 `POST /api/admin/recovery` 都会在
   `data/reports/recovery-<时间戳>.json` 留存证据缺失、哈希不符、派生物缺失和处置说明。

## 前端要点

- 左侧任务/帧：上传时即可看到倍率、像素尺寸、坐标单位是否可信、Z、采集时间、SHA 指纹。
- 中间候选：对齐诊断列表（error/warning/info 分级），帧排除复选框（尘点帧直接排除）。
- 瓦片网格：颜色=状态（Pending/Validated/Failed），✋ 表示手工改选；点瓦片看
  合成结果 / 可信度叠加 / 来源帧叠加三张预览，以及每帧清晰度分、覆盖/被选像素数，可强制改选。
- 版本弹窗：合成图 + 可信度 + 来源掩膜、规则参数、输入指纹表、导出、旧版本重发/建草稿、导入审计历史。
