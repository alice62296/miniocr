# MiniOCR

基于 [Sdcb.SimdPaddleOCR](https://github.com/sdcb/SimdPaddleOCR) 的 **Native AOT** PDF OCR HTTP API（**.NET 11 RC / `net11.0`**）。

从 URL 并发下载 PDF（≤300 MB），按页流式栅格化 + OCR（最多约 2000 页），返回每页文本与耗时 JSON。模型为 PP-OCRv6 **ChineseV6Tiny**。

## 环境要求

- Linux x64（推荐；本仓库在 Debian 13 x64 上构建）
- [.NET 11 RC SDK](https://dotnet.microsoft.com/download/dotnet/11.0)
- Native AOT 需要本机 C 工具链（`gcc` / `clang` + zlib 等）
- CPU 需支持 **AVX2**（见下方 AOT 说明）

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

### 吞吐旋钮（环境变量 / 请求）

| 变量 | 默认（8 核） | 说明 |
| --- | ---: | --- |
| `MINIOCR_ENGINES` | **4** | 页级并行 `PaddleOcrAll` 实例数 |
| `MINIOCR_DPI` | **45** | 栅格化 DPI（也可在 JSON/`?dpi=` 覆盖） |
| `MINIOCR_LINE_WORKERS` | **4** | 页内 CLS/REC 并行（`LineWorkerCount`） |
| `MINIOCR_DET_THREADS` | **2** | 检测图内卷积线程（`DetIntraOpThreads`） |
| `MINIOCR_USE_CLS` | **false** | 是否启用方向分类（关闭可提速并少占内存） |
| `MINIOCR_RASTER_WORKERS` | **4** | 并行 PDF 栅格化生产者数 |
| `MINIOCR_REC_BATCH` | **8** | `RecBatchLines` |
| `MINIOCR_DET_LIMIT_SIDE` | **960** | 检测 `LimitSideLength` |

请求体也可覆盖 DPI：`{"url":"...","dpi":72}` 或 `POST /ocr?dpi=72`。

> 速度优先默认：**DPI 45 + 4 引擎 + 无 CLS**。精度优先可设 `MINIOCR_DPI=150`、`MINIOCR_USE_CLS=1`。

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

## Native AOT 注意（avx2）

项目已设置：

```xml
<PublishAot>true</PublishAot>
<IlcInstructionSet>avx2</IlcInstructionSet>
```

**必须**保留 `IlcInstructionSet=avx2`。否则 ILC 按 SSE2 / 128-bit `Vector<T>` 基线编译，`Avx2.IsSupported` 会被折成 `false`，SimdPaddleOCR 的 AVX2 内核整段裁掉，OCR 会慢很多。

- 无 AVX2 的 CPU：不要设置该项（或改用非 AOT / 更低指令集），否则进程可能无法启动。
- ARM64：一般不需要写 `IlcInstructionSet`（基线含 NEON）。
- AOT 禁用反射密集 API；本项目使用 `JsonSerializerContext` + `WebApplication.CreateSlimBuilder`。

## 架构与内存策略

| 环节 | 策略 |
| --- | --- |
| 下载 | `HttpClient`：若 `Accept-Ranges: bytes` 且已知 `Content-Length`，则并行 Range 写入预分配缓冲；否则单流写入预分配/可控增长缓冲。硬顶 **300 MB**。缓冲来自 `ArrayPool<byte>`。 |
| 栅格化 | PDFtoImage（PDFium + SkiaSharp），默认 **45 DPI**；多生产者写入有界 Channel（窗口 ≈ `2 ×` OCR 引擎数），**绝不**同时持有全部页位图。 |
| OCR | 复用多个 `PaddleOcrAll`（ChineseV6Tiny，默认可关 CLS）；页级引擎池互斥租用；`LineWorkerCount` / `DetIntraOpThreads` 做页内并行。Skia **BGRA** 直接喂 OCR，无 ImageSharp 中间拷贝。 |
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
| `POST` | `/ocr` | Body: `{"url":"https://.../file.pdf","dpi":45}` |

## 项目结构

```
miniocr/
  MiniOcr.csproj          # Web + PublishAot + IlcInstructionSet=avx2
  Program.cs              # SlimBuilder + /ocr /health
  AppJsonContext.cs       # AOT JSON
  Models/OcrModels.cs
  Services/
    OcrRuntimeConfig.cs   # 环境变量吞吐旋钮
    ParallelPdfDownloader.cs
    RentedBuffer.cs
    OcrEngine.cs
    PdfOcrPipeline.cs
  samples/sample-multipage.pdf
  README.md
```

## 实测：2000 页（AOT，请勿伪造）

以下数字来自本机 **Native AOT** 对 `samples/sample-2000.pdf`（2000 页，中英混合文本）的真实 `POST /ocr`（Asia/Shanghai）：

| 项 | 基线（旧） | **当前默认（新）** |
| --- | ---: | ---: |
| 日期 | 2026-09-20 23:09 CST | **2026-09-20 23:52 CST** |
| 配置 | DPI 150 / 引擎 2 / Line 4 / Det 8 / CLS on | **DPI 45 / 引擎 4 / Line 4 / Det 2 / CLS off** |
| `timings.totalMs` | 838,569.1（~14.0 min） | **248,854.5（~4.15 min）** |
| pages/sec | 2.385 | **8.037** |
| 是否 &lt; 5 min | 否 | **是** |
| peak RSS | ~608 MiB | **~417 MiB** |
| downloadMs | 6.0 | 5.9 |
| rasterizeMs（页合计） | 37,730 | 32,902 |
| ocrMs（页合计） | 780,500 | 994,282 |

冒烟（5 页 `sample-multipage.pdf`）仍可用于快速验证；大吞吐请以 2000 页表为准。

**精度权衡：** 默认 DPI 45 相对 150 像素面积约 9%，中文细部可能出现个别错字/漏字；需要更高准确率时请提高 `MINIOCR_DPI`（如 96/150）并视情况开启 `MINIOCR_USE_CLS=1`。

## 许可证

示例代码以仓库为准；Sdcb.SimdPaddleOCR 与模型包遵循其上游 Apache-2.0 等许可。
