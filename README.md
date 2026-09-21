# MiniOCR

基于 [Sdcb.SimdPaddleOCR](https://github.com/sdcb/SimdPaddleOCR) 的 **Native AOT** PDF OCR HTTP API（**.NET 11 RC / `net11.0`**）。

从 URL 并发下载 PDF（≤300 MB），按页流式栅格化 + OCR（最多约 2000 页），返回每页文本、耗时，以及 **公司名 / 人名** JSON（优先 OpenAI 兼容 LLM NER，可回退启发式）。模型为 PP-OCRv6 **ChineseV6Tiny**。

## 环境要求

- 目标平台：Windows / Linux / macOS（x64 与 ARM64）；本仓库 CI 产出多平台 Native AOT 包
- [.NET 11 RC SDK](https://dotnet.microsoft.com/download/dotnet/11.0)
- Native AOT 需要本机 C 工具链（`gcc` / `clang` + zlib 等）
- **x64** 正式包启用 **AVX2**（见下方 AOT / CI 说明）；**ARM64** 不使用 AVX2

### 安装 .NET 11 RC（Linux）

```bash
curl -fsSL https://dot.net/v1/dotnet-install.sh -o /tmp/dotnet-install.sh
bash /tmp/dotnet-install.sh --version 11.0.100-rc.1.26425.128 --install-dir "$HOME/.dotnet"
export DOTNET_ROOT="$HOME/.dotnet"
export PATH="$HOME/.dotnet:$PATH"
dotnet --list-sdks
```

## AOT 发布（关键）

```bash
git clone https://github.com/huiyuanai709/miniocr.git
cd miniocr
dotnet publish -c Release -r linux-x64 -o ./artifacts/linux-x64
```

产物：`./artifacts/linux-x64/MiniOcr`（单文件原生可执行程序 + 原生依赖如 `libSkiaSharp.so` / `libpdfium.so`）。

### 运行 AOT 二进制

```bash
cd artifacts/linux-x64
./MiniOcr --urls http://0.0.0.0:5080
```

或非 AOT 开发模式：

```bash
dotnet run -c Release --urls http://127.0.0.1:5080
```

### 配置文件（%APPDATA% / ApplicationData）

首次启动会在以下路径创建目录并写入示例 `config.json`（若不存在）：

| 平台 | 路径公式 |
| --- | --- |
| Windows | `%APPDATA%\MiniOcr\config.json`（即 `Environment.GetFolderPath(SpecialFolder.ApplicationData)\MiniOcr\config.json`） |
| Linux | 通常 `~/.config/MiniOcr/config.json`（同一 API） |
| macOS | 通常 `~/Library/Application Support/MiniOcr/config.json` |

示例内容：

```json
{
  "llm": {
    "enabled": true,
    "baseUrl": "https://api.openai.com",
    "apiKey": "",
    "model": "gpt-4o-mini",
    "timeoutSeconds": 120,
    "maxCharsPerRequest": 12000,
    "maxConcurrency": 4,
    "fallbackToHeuristics": true
  },
  "ocr": {
    "dpi": 96,
    "engines": null,
    "lineWorkers": null,
    "detThreads": null,
    "rasterWorkers": null,
    "useCls": false,
    "autoScaleFromCpu": true
  }
}
```

**优先级：**

- OCR：环境变量 `MINIOCR_*` **覆盖** 文件；文件中 `null` / 未写且 `autoScaleFromCpu: true` 时按 CPU 核数自动推算。
- LLM：主要读 AppData 文件；可用 `MINIOCR_LLM_API_KEY` / `MINIOCR_LLM_BASE_URL` / `MINIOCR_LLM_MODEL` / `MINIOCR_LLM_MAX_CONCURRENCY` 覆盖。**不会**把 `apiKey` 打进日志（仅显示 `(set)` / `(empty)`）。

#### 配置 OpenAI / 兼容接口（DeepSeek、Azure、本地）

1. 编辑上述 `config.json`，填入 `llm.apiKey`，按需改 `baseUrl` 与 `model`。
2. `baseUrl` 不要带 `/v1/...` 后缀；客户端会请求 `{baseUrl}/v1/chat/completions`。
3. 示例：
   - OpenAI：`https://api.openai.com` + `gpt-4o-mini`
   - DeepSeek：`https://api.deepseek.com` + `deepseek-chat`
   - 本地（如 Ollama 兼容层）：`http://127.0.0.1:11434` + 你的模型名
4. 或仅用环境变量：`export MINIOCR_LLM_API_KEY=sk-...`（其余仍可读文件）。
5. **并发批次**：`llm.maxConcurrency`（默认 **4**，范围 1–32）控制同时进行的 Chat Completions 批次数；环境变量 `MINIOCR_LLM_MAX_CONCURRENCY` 可覆盖。启动日志会打印 `maxConcurrency=…`；首次 NER 时也会记录 `batches` 与并发度。调高可缩短长文档 NER 墙钟时间，但请留意提供商 **RPM / TPM** 限流（过高易 429）；本地模型则受 GPU/CPU 吞吐约束。
6. `enabled: false` 或没有 key 时：若 `fallbackToHeuristics: true`（默认）则用启发式 NER；否则 `entities` 为空。

### 吞吐旋钮（文件 + 环境变量 / 请求）

| 变量 | 文件字段 | 默认（auto-scale，约 8 核） | 说明 |
| --- | --- | ---: | --- |
| `MINIOCR_ENGINES` | `ocr.engines` | **4**（`Clamp(cores/2, 1, min(16,cores))`） | 页级并行 `PaddleOcrAll` 实例数 |
| `MINIOCR_DPI` | `ocr.dpi` | **96** | 栅格化 DPI（也可在 JSON/`?dpi=` 覆盖） |
| `MINIOCR_LINE_WORKERS` | `ocr.lineWorkers` | 自动 | 页内 CLS/REC 并行 |
| `MINIOCR_DET_THREADS` | `ocr.detThreads` | 自动 | 检测图内卷积线程 |
| `MINIOCR_USE_CLS` | `ocr.useCls` | **false** | 是否启用方向分类 |
| `MINIOCR_RASTER_WORKERS` | `ocr.rasterWorkers` | 自动 | 并行 PDF 栅格生产者（封顶 8） |
| `MINIOCR_REC_BATCH` | — | **8** | `RecBatchLines` |
| `MINIOCR_DET_LIMIT_SIDE` | — | **960** | 检测 `LimitSideLength` |

请求体也可覆盖 DPI：`{"url":"...","dpi":150}` 或 `POST /ocr?dpi=150`。

#### CPU 自动扩缩（`autoScaleFromCpu`，默认 true）

旧公式 `engines = Clamp(cores/2, 4, 8)` 会在 **2 核机器上仍开 4 个引擎**。新曲线：

| 项 | 新公式 |
| --- | --- |
| `engines` | `Clamp(cores/2, 1, min(16, cores))` |
| `lineWorkers` / `detThreads` | 使 `engines × (line + det)` 约在 **1.0–1.5× cores**（目标约 1.25×） |
| `rasterWorkers` | `Clamp(min(engines, cores/2), 1, 8)` |

启动时日志打印 `ProcessorCount` 与选定的 engines/line/det/raster；`GET /health` 同样暴露这些字段及 `configPath` / LLM 状态（无 key）。

> 更快可降 `MINIOCR_DPI=45`；更高精度可设 `MINIOCR_DPI=150`、`MINIOCR_USE_CLS=1`。显式设置 env/文件中的 engines 等会关闭对该项的自动推算。

### 示例 curl

```bash
# 终端 A：提供示例 PDF
python3 -m http.server 8000 --directory samples

# 终端 B：调用 OCR API
curl -sS -X POST http://127.0.0.1:5080/ocr \
  -H 'Content-Type: application/json' \
  -d '{"url":"http://127.0.0.1:8000/sample-multipage.pdf"}' | jq .
```

健康检查：

```bash
curl -sS http://127.0.0.1:5080/health
```

### 响应字段（摘要）

| 字段 | 说明 |
| --- | --- |
| `ok` | 是否成功 |
| `pageCount` / `pdfBytes` | 页数 / PDF 字节数 |
| `dpi` | 实际栅格化 DPI |
| `downloadMode` | `parallel-ranges` / `single-presized` / `single-grow` / … |
| `timings.downloadMs` | 下载耗时 |
| `timings.rasterizeMs` | 各页栅格化合计 |
| `timings.ocrMs` | 各页 OCR 合计 |
| `timings.totalMs` | 端到端（含下载） |
| `pages[].text` | 该页识别文本 |
| `pages[].rasterizeMs` / `ocrMs` | 单页耗时 |
| `entities.companies[]` | 全局去重公司名：`name` / `pages` / `count` |
| `entities.persons[]` | 全局去重人名：`name` / `pages` / `count` |

### 实体抽取（LLM 优先 + 启发式回退）

全部页 OCR 完成后抽取 `entities`：

1. **LLM（推荐）**：若 `llm.enabled` 且配置了 `apiKey`，将页文本按 `maxCharsPerRequest`（默认 12000 字）分批，并以 `maxConcurrency`（默认 4）为上限**并行**调用 OpenAI 兼容 `POST {baseUrl}/v1/chat/completions`，提示词要求只返回严格 JSON `{"companies":["..."],"persons":["..."]}`（中英均可；禁止臆造正文没有的名字）。各批结果线程安全合并去重，再回扫各页文本填充 `pages` / `count`。
2. **启发式回退**：LLM 未启用、无 key、或请求失败且 `fallbackToHeuristics: true` 时，使用 `EntityExtractor`（Regex + 百家姓 HashSet，`[GeneratedRegex]`，无 ML 包，AOT 安全）。

| 类型 | 启发式规则（摘要，作回退） |
| --- | --- |
| 公司名 | 中文组织后缀；英文 Inc/Ltd/Corp/LLC/Co.；标签 `公司名称：` / `甲方：` 等 |
| 人名 | 百家姓 + 职称/标签；英文 `John Smith` 式 |

冒烟：`dotnet run -c Release --project tests/MiniOcr.EntitySmoke`（启发式）。无 API key 时服务仍可启动，自动走启发式。

**局限：** 启发式会漏/误；LLM 依赖模型与 OCR 文本质量，竞赛场景请复核关键实体。

## Native AOT 注意（avx2，仅 x64）

项目已设置：

```xml
<PublishAot>true</PublishAot>
<!-- 仅当 RuntimeIdentifier 含 x64 时启用；ARM64 不会设置 avx2 -->
<IlcInstructionSet Condition="$([System.String]::Copy('$(RuntimeIdentifier)').Contains('x64'))">avx2</IlcInstructionSet>
```

对 **x64**：**必须**保留 `IlcInstructionSet=avx2`。否则 ILC 按 SSE2 / 128-bit `Vector<T>` 基线编译，`Avx2.IsSupported` 会被折成 `false`，SimdPaddleOCR 的 AVX2 内核整段裁掉，OCR 会慢很多。

- **无 AVX2 的 x64 CPU**：不要下载/运行带 AVX2 的 x64 包（可能无法启动）。请自行去掉 `IlcInstructionSet` 后本地发布，或改用非 AOT。
- **ARM64**（`linux-arm64` / `osx-arm64`）：不设置 `IlcInstructionSet`（基线含 NEON），与 x64 AVX2 包无关。
- AOT 禁用反射密集 API；本项目使用 `JsonSerializerContext` + `WebApplication.CreateSlimBuilder`。

## 下载 CI 产物（GitHub Actions）

推送到 `main`、手动 `workflow_dispatch`，或发布 Release / 打 `v*` 标签时，工作流 [`.github/workflows/publish.yml`](.github/workflows/publish.yml) 会为各 RID 构建 Native AOT 并上传制品。

1. 打开仓库 **Actions** → 选中 **Publish Native AOT** 某次成功运行。
2. 在 **Artifacts** 下载对应平台 zip，名称形如：
   - `miniocr-win-x64` / `miniocr-linux-x64` / `miniocr-osx-x64`（**AVX2**）
   - `miniocr-osx-arm64` / `miniocr-linux-arm64`（**无 AVX2**）
3. 若通过 **Release** / `v*` 标签触发，zip 也会尽量挂到该 GitHub Release 上，可直接从 Releases 页下载。

解压后目录内含可执行文件与原生依赖（如 `libSkiaSharp` / `pdfium` 的 `.dll` / `.so` / `.dylib`），以及示例 PDF（若打包时存在）。在对应系统上直接运行即可（x64 包要求 CPU 支持 AVX2）。

## 架构与内存策略

| 环节 | 策略 |
| --- | --- |
| 下载 | `HttpClient`：若 `Accept-Ranges: bytes` 且已知 `Content-Length`，则并行 Range 写入预分配缓冲；否则单流写入预分配/可控增长缓冲。硬顶 **300 MB**。缓冲来自 `ArrayPool<byte>`。 |
| 栅格化 | PDFtoImage（PDFium + SkiaSharp），默认 **96 DPI**；每 worker **一次** `PdfDocument.Load` + `ToImages`（避免逐页 `ToImage` 重载）；`AntiAliasing=None` + `Grayscale`（仍输出 BGRA）；多生产者写入有界 Channel（窗口 ≈ `2 ×` OCR 引擎数），**绝不**同时持有全部页位图。 |
| OCR | 复用多个 `PaddleOcrAll`（ChineseV6Tiny，默认可关 CLS）；页级引擎池互斥租用；`LineWorkerCount` / `DetIntraOpThreads` 做页内并行。Skia **BGRA** 直接喂 OCR，无 ImageSharp 中间拷贝。 |
| 实体 | 优先 `LlmEntityExtractor`（Chat Completions 分批）；失败/关闭则 `EntityExtractor` 启发式。 |
| JSON | 源生成 `AppJsonContext`，AOT 友好。 |

### 峰值内存（量级，非承诺值）

- PDF 本体：最多约 **300 MB**（池化租用）
- 在途页位图：窗口内数页（DPI 越低越小）
- 模型 + 推理工作区：随引擎数上升；关闭 CLS 可明显降低
- 设计目标：2000 页时内存不随页数线性涨到「整本位图」，而随 **窗口 + 模型** 近似封顶

## 依赖

| 包 | 说明 |
| --- | --- |
| `Sdcb.SimdPaddleOCR` 1.4.1 | 纯托管 PP-OCRv6 |
| `Sdcb.SimdPaddleOCR.Models.ChineseV6Tiny` | 中文 tiny DET+REC（CLS 可选） |
| `PDFtoImage` 5.4.0 | PDFium 栅格化（SkiaSharp） |

## API

| 方法 | 路径 | 说明 |
| --- | --- | --- |
| `GET` | `/health` | 健康、模型与当前旋钮 |
| `POST` | `/ocr` | Body: `{"url":"https://.../file.pdf","dpi":96}` |

## 项目结构

```
miniocr/
  MiniOcr.csproj          # Web + PublishAot + IlcInstructionSet=avx2（仅 x64）
  .github/workflows/publish.yml  # 多平台 AOT 打包
  Program.cs              # SlimBuilder + /ocr /health
  AppJsonContext.cs       # AOT JSON
  Models/OcrModels.cs
  Services/
    AppConfigStore.cs     # %APPDATA%/MiniOcr/config.json
    OcrRuntimeConfig.cs   # 文件+环境变量+CPU 自动扩缩
    LlmEntityExtractor.cs # OpenAI 兼容 Chat Completions NER
    ParallelPdfDownloader.cs
    RentedBuffer.cs
    OcrEngine.cs
    PdfOcrPipeline.cs
    EntityExtractor.cs    # 启发式回退
  tests/MiniOcr.EntitySmoke/  # 实体抽取冒烟
  samples/sample-multipage.pdf
  README.md
```

## 实测：2000 页（AOT，请勿伪造）

以下数字来自本机对 `samples/sample-2000.pdf`（2000 页，中英混合文本）的真实测量（Asia/Shanghai）：

### 端到端 OCR（优化前，Native AOT）

| 项 | 旧默认 DPI 45 | DPI 96（栅格优化前） |
| --- | ---: | ---: |
| 日期 | 2026-09-20 23:52 CST | **2026-09-21 07:10–07:18 CST** |
| 配置 | DPI 45 / 引擎 4 / Line 4 / Det 2 / CLS off | **DPI 96 / 引擎 4 / Line 4 / Det 2 / CLS off / raster 4** |
| 模型 | ChineseV6Tiny | **ChineseV6Tiny** |
| `timings.totalMs` | 248,854.5（~4.15 min） | **456,691.7（~7.61 min）** |
| pages/sec | 8.037 | **4.379** |
| 是否 &lt; 5 min | 是 | **否** |
| peak RSS | ~417 MiB | **~1459 MiB** |
| downloadMs | 5.9 | **4.4** |
| rasterizeMs（页合计） | 32,902 | **46,902.7** |
| ocrMs（页合计） | 994,282 | **1,823,128.4** |
| 页尺寸（宽×高） | 372 × 526 | **793 × 1122** |

### 栅格化加速（2026-09-21，DPI 96）

瓶颈在 **native PDFium**（逐页 `Conversion.ToImage` 会 **每页重新 Load** PDF）。托管 SIMD 帮不上忙（无像素拷贝；Skia 已是 BGRA）。PDFium/Skia 自身已用原生 SIMD。

| 场景 | 页数 | workers | wall | 页合计（≈`rasterizeMs`） | ms/页（合计） |
| --- | ---: | ---: | ---: | ---: | ---: |
| 优化前 `ToImage`/页 | 2000 | 4 | 27,323 ms | 108,057 ms | 54.0 |
| **优化后** `ToImages`+AA=None+Gray | 2000 | 4 | **2,751 ms** | **10,866 ms** | **5.43** |
| 同上 | 2000 | 2 | 2,611 ms | 5,176 ms | 2.59 |
| 流水线冒烟（非 AOT，含 OCR） | 200 | 4 | total 22.7 s | **rasterizeMs 882** | 4.4 |

约 **~10×** 栅格 wall / **~4–5×** 相对端到端里旧的 `rasterizeMs≈46.9 s`（流水线与 OCR 争用下页合计更接近 ~11 s）。OCR 仍占主导，端到端总时长几乎不变。

**未换引擎：** Docnet / 直连 Pdfium / MuPDF 探针无必要——文档复用 + 关闭 AA 已吃掉主要浪费；PDFtoImage 已 AOT 友好。

更早基线（DPI 150 / 引擎 2 / CLS on）：`totalMs` 838,569.1（~14.0 min）。

冒烟（5 页 `sample-multipage.pdf`）仍可用于快速验证；大吞吐请以 2000 页表为准。

**精度 / 速度权衡：** 默认 DPI 96 相对 150 像素面积约 41%，相对旧默认 45 约 4.6×；中文细部明显好于 45。栅格默认 `AntiAliasing=None` + `Grayscale`（仍 BGRA，利于 OCR 锐利字形）。若需 &lt;5 min / 2000 页可降 `MINIOCR_DPI=45`；更高精度可设 `MINIOCR_DPI=150` 并视情况开启 `MINIOCR_USE_CLS=1`。

## 许可证

示例代码以仓库为准；Sdcb.SimdPaddleOCR 与模型包遵循其上游 Apache-2.0 等许可。
