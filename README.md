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

### 示例 curl

先起一个可访问的 PDF URL（本地文件可用临时静态服务）：

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
| 栅格化 | PDFtoImage（PDFium + SkiaSharp），150 DPI；生产者按页写入有界 Channel（窗口 ≈ `2 ×` OCR 引擎数），**绝不**同时持有全部页位图。 |
| OCR | 复用 1～2 个 `PaddleOcrAll`（ChineseV6Tiny）；页级有界并行；`LineWorkerCount` / `DetIntraOpThreads` 做页内并行。页图 OCR 后立即 Dispose。 |
| JSON | 源生成 `AppJsonContext`，AOT 友好。 |

### 峰值内存（量级，非承诺值）

- PDF 本体：最多约 **300 MB**（池化租用）
- 在途页位图：窗口内数页（150 DPI RGBA，视页面尺寸，通常每页数 MB～数十 MB）
- 模型 + 推理工作区：ChineseV6Tiny 约数百 MB 量级（随 `LineWorkerCount` / 引擎数上升）
- 设计目标：2000 页时内存不随页数线性涨到「整本位图」，而随 **窗口 + 模型** 近似封顶

## 依赖

| 包 | 说明 |
| --- | --- |
| `Sdcb.SimdPaddleOCR` 1.4.1 | 纯托管 PP-OCRv6 |
| `Sdcb.SimdPaddleOCR.Models.ChineseV6Tiny` | 中文 tiny DET+REC+CLS |
| `SixLabors.ImageSharp` 3.1.11 | RGBA32 连续像素 |
| `PDFtoImage` 5.4.0 | PDFium 栅格化 |

## API

| 方法 | 路径 | 说明 |
| --- | --- | --- |
| `GET` | `/health` | 健康与模型状态 |
| `POST` | `/ocr` | Body: `{"url":"https://.../file.pdf"}` |

## 项目结构

```
miniocr/
  MiniOcr.csproj          # Web + PublishAot + IlcInstructionSet=avx2
  Program.cs              # SlimBuilder + /ocr /health
  AppJsonContext.cs       # AOT JSON
  Models/OcrModels.cs
  Services/
    ParallelPdfDownloader.cs
    RentedBuffer.cs
    OcrEngine.cs
    PdfOcrPipeline.cs
  samples/sample-multipage.pdf
  README.md
```


## 实测冒烟（AOT 二进制，请勿伪造）

以下数字来自本仓库在开发机上对 **Native AOT** 产物的一次真实 `curl` 调用（Asia/Shanghai）：

| 项 | 数值 |
| --- | ---: |
| 日期 | 2026-09-20 23:02 CST |
| 二进制 | `artifacts/linux-x64/MiniOcr`（约 **83 MB**）+ `libSkiaSharp.so` / `libpdfium.so` |
| PDF | `samples/sample-multipage.pdf`，5 页，经由本地 `python3 -m http.server` URL |
| downloadMode | `single-presized`（该静态服务器 HEAD 未宣告 Range） |
| downloadMs | **6.2** |
| rasterizeMs | **218.4** |
| ocrMs | **764.4** |
| totalMs | **1179.7** |
| HTTP 端到端 | 约 **1.19 s** |

## 许可证

示例代码以仓库为准；Sdcb.SimdPaddleOCR 与模型包遵循其上游 Apache-2.0 等许可。ImageSharp 3.x 为 Six Labors Split License。
