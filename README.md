# hdrshot — HDR 截图转换工具（M1）

把 Windows / NVIDIA 在 HDR 显示器下截出的 `.jxr`（scRGB 浮点）无损转换成可分享的 HDR 格式：

- **HDR PNG**：16-bit、BT.2020 + PQ（10-bit 数据装在 16-bit 容器）、`cICP` + `cLLi` 元数据 —— Chrome/Edge 117+ 直接以真 HDR 显示
- **HDR AVIF**：10-bit PQ BT.2020（YUV444，体积约为 PNG 的 1/28；经内置 avifenc 1.4.2）

与参考实现 [ledoge/jxr_to_png](https://github.com/ledoge/jxr_to_png) 做过逐像素比对：

- 5 个微软官方 FP16 样本：99.96% 像素完全一致，其余差异全部为 1 个 10-bit 量化步长（浮点舍入级）；MaxCLL/MaxFALL 逐位一致
- 真实 NVIDIA FP32 截图（3840×1600，`128bppRGBAFloat`）：**100% 逐位一致（0 差异）**

## 用法

```
hdrshot <文件或通配符...> [-f png|avif|both] [-o 输出目录] [-q 60] [--avifenc 路径]
```

也可以直接把 `.jxr` 拖到 `hdrshot.exe` 图标上。

示例：

```
hdrshot screenshot.jxr
hdrshot *.jxr -o D:\share -f png
```

## 支持的输入

| 来源 | WIC 像素格式 | 位深 |
|---|---|---|
| NVIDIA App / GeForce Experience（Alt+F1） | `128bppRGBAFloat`（scRGB FP32） | 32-bit 浮点 |
| Win11 截图工具 / Xbox Game Bar | `64bppRGBAHalf`（scRGB FP16） | 16-bit 半浮点 |
| 其他 scRGB 浮点变体（RGBFloat / 96bppRGBFloat / 预乘 P 变体） | 同族 GUID | — |

scRGB 语义按 Microsoft Advanced Color 规范处理：BT.709 原色、线性、**1.0 = 80 nits**、负值裁 0（与 libplacebo/KWin 惯例一致）、>1.0 高亮按 PQ（ST 2084，上限 10000 nits）编码。

## 构建

需要 .NET 8 SDK（Windows）：

```
dotnet build src/hdrshot -c Release
dotnet publish src/hdrshot -c Release -r win-x64 --self-contained -p:PublishSingleFile=true
```

运行时零第三方依赖（解码走系统 WIC，PNG 编码纯 .NET）。AVIF 出口需要捆绑 `tools\avifenc.exe` 及其 DLL（MSYS2 mingw-w64-libavif）。

## 设计文档

见 [docs/01-调研与设计方案.md](docs/01-调研与设计方案.md)。

## 已知限制（M1）

- 负值（广色域）直接裁剪，不做完整 gamut mapping（社区主流做法，截图场景影响可忽略）
- UltraHDR JPEG 出口与资源管理器右键集成待 M2
