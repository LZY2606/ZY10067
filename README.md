# MicroStack — 显微多焦平面瓦片合成系统

操作员对同一视野采集一组不同焦平面（Z 高度）的图像，必要时补拍局部区域。
MicroStack 读取每张图的像素、倍率、载台坐标、Z 高度和采集时间：

1. 校验每一帧能否参与对齐（单位缺失、倍率矛盾、标定缺失都会给出明确诊断）；
2. 计算局部清晰度（Tenengrad/Sobel）图，逐像素选择最清晰的可用来源生成候选合成图；
3. 输出按瓦片（tile）切分的候选版本，逐瓦片标记验证状态，全部验证后才能原子发布；
4. 每个版本都固定其输入指纹、规则版本、参数、逐瓦片分数与机器可读的变换矩阵；
5. 导出包可在另一台机器上**不相信合成图**地重算，并逐像素核对来源索引。

只依赖 .NET BCL（PNG 编解码为手写实现，压缩用内置 zlib），测试与运行**不需要外部网络**。

## 环境要求

- .NET SDK 10（`dotnet --version` 可用即可；运行时包含 ASP.NET Core）。

## 安装（还原依赖）

```bash
dotnet restore
```

## 测试并启动

```bash
dotnet test --nologo && dotnet run --project src/Web --urls http://127.0.0.1:5203
```

可见页面：<http://127.0.0.1:5203>

- 数据目录默认是 `src/Web/microstack-data/`，可用环境变量 `MICROSTACK_DATA` 覆盖。
- 页面顶部「载入演示数据」会生成一组确定性合成帧：3 个焦平面 + 1 张局部补拍 + 1 帧带尘点。

## 页面能做什么

- **原始证据**：上传 8-bit 灰度/RGB PNG。内容按 SHA-256 去重，文件与元数据一经写入不再原地修改；同名文件重新上传若字节不同会得到新的证据 ID，旧版本绑定的指纹不变。
- **合成**：
  - 画布叠加显示合成图、不可信/诊断层、瓦片网格；图例区分五类不可信区域。
  - 点击瓦片查看每个候选来源的平均清晰度分数、自动选中的来源；可单选**强制来源**或勾选**排除尘帧**（只作用于该瓦片），保存即生成新版本。
  - 每个瓦片可「标记已验证」；**全部瓦片验证后**才能发布；发布是对版本指针的一次原子切换。
  - 未发布候选可以「取消」，已发布版本不受影响。
  - 两个浏览器基于同一旧版本提交时：改动的瓦片不相交会**自动合并**；作用于同一瓦片时返回 409，页面显示两侧内容并允许逐瓦片选择后重新合并。
  - 「导出当前版本 zip」可下载导出包。
- **导入核对**：上传导出 zip，服务端用包内原始证据 + `transforms.json` + 本地规则重算，逐像素比对合成图、来源索引掩膜和诊断层，并核对证据哈希。

## 不可信区域（不允许无声拉伸凑满）

| 诊断码 | 含义 |
| --- | --- |
| `no-coverage` | 没有任何可对齐帧覆盖该像素（合成图中为黑） |
| `insufficient-alignment` | 覆盖帧数低于参数 `minimumAlignedFrames` |
| `extrapolated-edge` | 采样点落在来源帧边缘保护带（默认 2px）内，属外推 |
| `magnification-conflict` | 该区域落在与参考倍率矛盾的帧足迹内，显式标红，不参与选帧 |
| `calibration-missing` | 帧缺失倍率，无法标定像素尺寸，无法放置 |
| `units-missing`（帧级诊断） | 载台坐标数值/单位缺失，拒绝静默对齐 |

参考倍率取「可标定帧中出现次数最多的倍率」，平局按上传顺序最先出现者，结果稳定且可复现。

## 版本里都保存了什么

每个版本（`VersionRec`）都保存：

- 全部输入帧的快照：证据 ID、内容 SHA-256、原始文件名、接收时间、倍率、载台坐标及单位、Z 高度、采集时间；
- `ruleSetVersion`（当前 `microstack-rules-1.0.0`）与完整 `EngineParameters`（瓦片大小、清晰度窗口半径、边缘保护带、最低对齐帧数、像素尺度因子、倍率容差）；
- 瓦片列表（几何、排除帧、强制来源、验证状态）与逐帧对齐诊断；
- 五个内容寻址产物：合成图、诊断叠加 PNG、**来源索引掩膜 PNG**（像素值=输入帧序号，255=无来源）、逐瓦片分数 JSON、变换矩阵 JSON。

## 导出包结构

```
manifest.json        # 包格式、版本、规则版本、文件清单（含每帧 SHA-256）
composite.png        # 合成图（不可信像素为黑）
source-mask.png      # 每像素来源帧索引（255=无来源），可逐像素核对
overlay.png          # 不可信/诊断层（0=可信，1..5 对应上表）
transforms.json      # 机器可读：画布->每帧的仿射映射、像素尺度、载台坐标、倍率
scores.json          # 逐瓦片逐帧清晰度分数、选择、排除/强制、验证状态
evidence/NNN-*.png   # 原始证据（逐字节随包携带）
```

`transforms.json` 中每帧给出 `originCanvasX/Y`、`sourcePixelsPerCanvasPixel`（x/y 等比）、
像素尺寸（µm/px）、载台中心、Z 高度与采集时间。对任意画布像素 `(x,y)`：

```
sourceX = floor((x - originCanvasX) / sourcePixelsPerCanvasPixel)
sourceY = floor((y - originCanvasY) / sourcePixelsPerCanvasPixel)
```

导入核对即据此 + 规则参数重算，并与 `composite.png` / `source-mask.png` / `overlay.png` 逐像素比对。

## 目录结构

```
src/Core/Imaging    纯 BCL 的 PNG 编解码（8-bit 灰度/RGB，filter 0-4，无交织）
src/Core/Engine     几何变换、Tenengrad 清晰度、瓦片渲染、不可信掩膜、产物 JSON 模型
src/Core/Storage    内容寻址对象存储 + 原子 JSON 文档存储
src/Core/Services   证据入库、合成/版本/发布工作流、乐观并发与自动合并、演示数据
src/Core/Exchange   导出 zip 与导入逐像素核对
src/Web             ASP.NET Core 路由 + wwwroot 静态单页（无外部 CDN）
tests/              25 个 xUnit 测试（含真实 Kestrel 端到端与离线包篡改测试）
```

## HTTP 摘要

- `POST /api/evidence`（multipart `file`）→ 入库（重复内容去重）
- `POST /api/demo` → 生成演示合成
- `GET/POST /api/compositions`，`GET /api/compositions/{id}`
- `GET /api/compositions/{id}/versions/{vid}/artifacts/{composite|overlay|source-mask|scores|transforms}`
- `POST /api/compositions/{id}/versions`（带 `baseVersionId`；409 返回双方瓦片内容）
- `POST /api/compositions/{id}/versions/{vid}/tiles/validate`
- `DELETE /api/compositions/{id}/versions/{vid}`（取消未发布候选）
- `POST /api/compositions/{id}/publish`（未全部验证返回 409 + 未验证瓦片列表）
- `GET /api/compositions/{id}/export/{vid}` → zip
- `POST /api/imports/verify`（multipart `package`）→ 逐像素核对报告

## 给新维护者：一次故障恢复演练

本节可以照抄执行，用来验证「中途崩溃不替换上一版、原始证据不可变」。

### 1) 准备状态

```bash
dotnet restore
export MICROSTACK_DATA=/tmp/microstack-drill
dotnet run --project src/Web --urls http://127.0.0.1:5203
```

页面或接口上：载入演示数据 → 逐瓦片标记已验证 → 发布。记录：
- 合成 ID（`cmp_...`，下文记为 `$C`）、已发布版本（`ver_...`，记为 `$V`）。

### 2) 模拟「候选生成到一半进程被杀」

基于已发布版本改几个瓦片（生成新候选），在服务端写产物时直接 `kill -9` 服务进程
（或直接关闭终端）。然后检查磁盘：

```bash
ls $MICROSTACK_DATA/compositions/          # $C.json
ls $MICROSTACK_DATA/objects/ab/cd/         # 内容寻址产物
```

恢复保证：

- 合成文档是「先写 `*.tmp-<guid>` 再 `rename`」的原子替换；被杀时要么是完整旧文档，要么是完整新文档，不会出现半截 JSON。
- 对象存储同样是 tmp + rename；未挂入版本 JSON 的产物只是孤儿文件，不会被任何版本引用。
- 重新启动后 `GET /api/compositions/$C`：`publishedVersionId` 仍指向 `$V`；
  未完成的候选不会出现。**上一版可发布状态没有被替换。**

### 3) 修复损坏文档

- 若 `compositions/$C.json` 本身被外部破坏（例如磁盘满导致写入被绕过），
  所有对象与证据仍然完好：可从同目录的 `$C.json` 无法读取时定位问题。
  正常崩溃不会产生这种情况；手工恢复时，用 `.tmp-*` 之外最近一次完整文件替换，
  或直接删除损坏的 `$C.json`——证据目录 `evidence/` 与对象存储不受影响，
  原始证据仍可用于重建新合成。
- 任何版本都可通过其产物手工检查：产物键是 SHA-256，文件名即内容指纹，可直接
  `shasum -a 256` 校验。

### 4) 验证证据不可被同名重传偷换

上传 `a.png`（内容 A），发布；再用**不同内容但同名**的 `a.png` 上传并新建合成。
打开旧版本：其帧列表中的 `contentSha256` 与 `receivedAt` 仍是内容 A 的指纹；
导出旧版本 zip，包内 `evidence/` 字节的哈希与 `manifest.json` 一致。

### 5) 验证跨机恢复（离线）

在机器 A：`GET /api/compositions/$C/export/$V` 得到 zip；
拷贝到机器 B（或本机另一个空 `MICROSTACK_DATA`），页面「导入核对」标签上传：

- `verified=true` 表示重算合成图、来源索引、诊断层逐像素一致且证据哈希匹配；
- 修改 zip 内 `composite.png` 任意像素，会报告 `pixel-mismatch` 与坐标；
- 修改任一证据 PNG，会报告 `evidence-hash-mismatch`。

### 6) 回归

```bash
dotnet test --nologo
```

覆盖：PNG 往返、几何诊断、补拍足迹不拉伸、证据不可变/同名重传、分数与参数随版本保存、
逐瓦片验证门控、取消不影响发布、409 冲突内容与重新合并、导出导入逐像素一致、
以及篡改合成图/证据被检出。
