# MiniOCR

基于 [Sdcb.SimdPaddleOCR](https://github.com/sdcb/SimdPaddleOCR) 的多页 PDF OCR 控制台基准工具，目标框架 **.NET 11 RC（`net11.0`）**。

将 PDF 每页栅格化为位图后，用 PP-OCRv6 **ChineseV6Tiny** 做检测 / 方向分类 / 识别，并输出模型加载、栅格化、OCR 耗时与文本预览。

## 环境要求

- Linux / Windows / macOS（本仓库在 Debian 13 x64 上实测）
- [.NET 11 RC SDK](https://dotnet.microsoft.com/download/dotnet/11.0)（本机使用 `11.0.100-rc.1.26425.128`）
- 无需单独安装 Paddle Inference / ONNX Runtime；PDF 栅格化依赖 PDFtoImage 自带的 PDFium 原生库

### 安装 .NET 11 RC（Linux）

```bash
curl -fsSL https://dot.net/v1/dotnet-install.sh -o /tmp/dotnet-install.sh
bash /tmp/dotnet-install.sh --version 11.0.100-rc.1.26425.128 --install-dir "$HOME/.dotnet"
export DOTNET_ROOT="$HOME/.dotnet"
export PATH="$HOME/.dotnet:$PATH"
dotnet --list-sdks
```

也可从 [Download .NET 11.0](https://dotnet.microsoft.com/download/dotnet/11.0) 下载对应平台二进制包解压后配置 `DOTNET_ROOT` / `PATH`。

## 依赖（NuGet）

| 包 | 说明 |
| --- | --- |
| `Sdcb.SimdPaddleOCR` | 纯托管 PP-OCRv6 推理（包内含 `net10.0` 与 `netstandard2.0`；在 `net11.0` 下可直接引用） |
| `Sdcb.SimdPaddleOCR.Models.ChineseV6Tiny` | 中文 tiny DET+REC+字典（含 CLS） |
| `SixLabors.ImageSharp` `3.1.11` | RGBA32 连续像素缓冲 |
| `PDFtoImage` | PDFium 栅格化（Linux 可用） |

> 说明：`Sdcb.SimdPaddleOCR` 1.4.1 的主 TFM 为 `net10.0`，本项目仍以 **`net11.0`** 为目标，在 .NET 11 RC 运行时上加载其 `net10.0` 程序集，`dotnet restore` / `build` 均成功。

## 快速运行

```bash
git clone https://github.com/huiyuanai709/miniocr.git
cd miniocr
dotnet run -c Release
# 或指定 PDF：
dotnet run -c Release -- path/to.pdf
```

未传参数时，使用内嵌示例多页 PDF：`samples/sample-multipage.pdf`（5 页，中英混合）。

## 输出说明

程序会打印：

- 模型加载耗时（ms）
- 每页栅格化耗时、OCR 耗时，以及约 200 字符文本预览
- 栅格化合计、OCR 合计、pipeline（栅格化 + OCR）
- OCR **ms/page**、**pages/sec**

## 实测基准（请勿伪造）

以下数字来自本仓库在 Grok Bot 开发机上的一次 **Release** 实测（CST / Asia/Shanghai）：

| 项 | 数值 |
| --- | ---: |
| 日期 | 2026-09-20 22:54 CST |
| 主机 | Debian 13 (trixie)，x86_64，8 核 Intel Xeon，约 15 GiB RAM |
| 运行时 | .NET 11.0.0-rc.1.26425.128 |
| SDK | 11.0.100-rc.1.26425.128 |
| 模型 | ChineseV6Tiny（嵌入资源） |
| PDF | `samples/sample-multipage.pdf`，**5 页**，150 DPI |
| 模型加载 | **162.6 ms** |
| 栅格化合计 | **594.5 ms**（0.594 s） |
| OCR 合计 | **1192.9 ms**（**1.193 s**） |
| OCR ms/page | **238.6 ms** |
| OCR pages/sec | **4.19** |
| 每页 OCR ms | 513.1, 174.6, 133.9, 208.3, 163.0 |

备注：第 1 页通常包含 JIT / 缓存预热，耗时明显高于后续页；对比吞吐时建议关注合计或去掉首页后的均值。

复现命令：

```bash
dotnet run -c Release -- samples/sample-multipage.pdf
```

## 项目结构

```
miniocr/
  MiniOcr.csproj
  Program.cs
  samples/sample-multipage.pdf
  README.md
  .gitignore
```

## 许可证

示例代码以仓库为准；Sdcb.SimdPaddleOCR 与模型包遵循其上游 Apache-2.0 等许可，详见上游仓库说明。
