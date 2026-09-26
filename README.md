# hdrshot — HDR 截图转换工具

把 Windows / NVIDIA 在 HDR 显示器下截出的 `.jxr`（scRGB 浮点）无损转换成可分享的 HDR 格式：

## 效果对比

> 以下对比图均为上下结构：**上半部分是原图**（高光直接映射，天空与霓虹溢出成死白），**下半部分是 hdrshot 转换后**（亮度比软拐点压缩，云层结构与灯牌细节完整保留）。素材为《赛博朋克 2077》HDR 截图（3840×1600）。

![对比1：上为原图，下为转换后](https://tuchuang.heiweilu.top/PixPin_2026-09-26_12-16-44.png)

![对比2：上为原图，下为转换后](https://tuchuang.heiweilu.top/PixPin_2026-09-26_12-17-15.png)

## 图形界面

双击即用，拖入 `.jxr` 即可转换；峰值亮度与缩放可直接调：

![hdrshot GUI](https://tuchuang.heiweilu.top/PixPin_2026-09-26_12-14-58.png)

## 输出格式

- **UltraHDR JPEG**（默认输出）：SDR 兜底基底 + gain map（ISO 21496-1）——Chrome/Edge/Safari 26/Android 14+ 显示真 HDR，其他地方显示正常 SDR JPEG
- **HDR PNG**：16-bit、BT.2020 + PQ（10-bit 数据装在 16-bit 容器）、`cICP` + `cLLi` 元数据 —— Chrome/Edge 117+ 直接以真 HDR 显示
- **HDR AVIF**：10-bit PQ BT.2020（YUV444，体积约为 PNG 的 1/28；经内置 avifenc 1.4.2）

与参考实现 [ledoge/jxr_to_png](https://github.com/ledoge/jxr_to_png) 做过逐像素比对：

- 5 个微软官方 FP16 样本：99.96% 像素完全一致，其余差异全部为 1 个 10-bit 量化步长（浮点舍入级）；MaxCLL/MaxFALL 逐位一致
- 真实 NVIDIA FP32 截图（3840×1600，`128bppRGBAFloat`）：**100% 逐位一致（0 差异）**

UltraHDR JPEG 验证：合成彩虹 100% 保色；`libultrahdr` 自解码回读通过；ISO 21496-1 元数据写入确认；SDR 基底逐分位数与源色度一致。

## 用法

```
hdrshot <文件或通配符...> [-f jpeg|png|avif|all] [-o 输出目录] [--sdr-white nits]
hdrshot --gui              # 拖拽图形界面
hdrshot --watch <目录>     # 监视文件夹，自动转换新截图
hdrshot --install-menus    # 添加资源管理器右键"转换为 HDR"（HKCU，免管理员）
```

也可以直接把 `.jxr` 拖到 `hdrshot.exe` 图标上，或运行 `hdrshot --gui` 后在窗口内拖放。

示例：

```
hdrshot screenshot.jxr                # -> screenshot.jpg (UltraHDR)
hdrshot *.jxr -f all -o D:\share
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
dotnet publish src/hdrshot -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:EnableCompressionInSingleFile=true
```

运行时零第三方依赖（解码走系统 WIC，PNG 编码纯 .NET）。UltraHDR/AVIF 出口需要 `tools\` 下的编码器组件（ultrahdr_app.exe / avifenc.exe 及 DLL），可用 `scripts/fetch-tools.ps1` 从 MSYS2 自动拉取组装。

## 已知限制

- UltraHDR 的 SDR 兜底基底按 `--sdr-white`（默认自适应 2×MaxFALL）映射，Windows 原生 App 看 UltraHDR 仍是 SDR 观感（生态限制）
- 负值（广色域）直接裁剪，不做完整 gamut mapping（社区主流做法，截图场景影响可忽略）
- GUI 需要交互桌面会话；微信/QQ 走普通图片消息会被压成 SDR——分享请勾"原图"或发文件
