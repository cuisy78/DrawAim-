# DrawAim 0.1.1 构建与验证报告

报告日期：2026-08-03（Asia/Shanghai）

## 结论

- Release 全解决方案构建成功：`0 warning / 0 error`。
- 自动化测试：`78 / 78` 通过，最终一次总用时 `1.69 s`。
- `win-x64` 自包含单文件发布成功。
- 最终发布版 `DrawAim.exe` 完成两次启动的 GUI 冒烟，进程均正常退出，返回码为 `0`。
- 三种模式均在 `900×480` 紧凑窗口、`1400×850` 常规窗口下实际打开；首页另在主显示器工作区尺寸下检查。

## 验证环境

| 项目 | 实际值 |
|---|---|
| 操作系统 | Windows 10 Pro 22H2 x64 |
| 系统 Build | 19045.3803 |
| 主显示器桌面 | 1920×1080 |
| 主显示器工作区 | 1920×1040 |
| 系统 DPI | 96 DPI / 100% |
| .NET SDK | 10.0.302 |
| MSBuild | 18.6.11 |
| .NET Host / Desktop Runtime | 10.0.10 / 10.0.10 |
| 目标框架 | `net10.0-windows` |
| 发布 RID | `win-x64` |

## 执行命令

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\build.ps1
powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\test.ps1
powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\publish.ps1

powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\gui-smoke.ps1 `
  -ApplicationPath .\artifacts\publish\win-x64\DrawAim.exe
```

## 自动化测试结果

| 范围 | 数量 | 结果 |
|---|---:|---:|
| 几何与曲线 | 8 | 全部通过 |
| 0～100 笔迹稳定器 | 9 | 全部通过 |
| 确定性随机与题目生成 | 21 | 全部通过 |
| 模式一评分 | 9 | 全部通过 |
| 模式二最终几何评分 | 13 | 全部通过 |
| 颜色转换与评分 | 10 | 全部通过 |
| 设置、历史、恢复与日志 | 8 | 全部通过 |
| 合计 | 78 | 全部通过 |

关键覆盖包括：

- 固定 PCG32 与生成契约黄金向量、相同 Seed 可复现；
- 批量曲线不越界、不自交，方向和位置分布检查；
- 4 个 Seed、9 类常用／受限设置下共 36,828 个相邻题对的肉眼近似重复检查为 `0`，覆盖正常生成和确定性降级；
- 稳定器在 `0` 时完全旁路，并检查跨采样率一致性；
- 模式一完美、半程、偏移、反向、涂抹、单点和压力不变性；
- 模式二顺序、方向、采样密度、拆笔、合笔、重复描、多画、漏画和整体错位；
- 模式二 10 线、512 网格最终评分为 `68.7 ms`，通过性能预算；
- 相同颜色严格 100，感知距离增大时得分单调下降；
- HSV 饱和度／明度方向、色相环绕、灰色／近黑不可判定处理；
- 历史中的负色差和超过 100 的有限诊断值可无损 JSON 往返。

## 最终 GUI 冒烟

最终证据目录：[`artifacts/smoke/case-0563f7f5d42646179968af3f3ba02a9b`](./artifacts/smoke/case-0563f7f5d42646179968af3f3ba02a9b)

实际执行内容：

1. 完成首次引导，切换浅色主题并切回深色主题。
2. 首页在 `900×480`、`1400×850` 和约 `1904×1024` 主工作区窗口下截图。
3. 模式一确认 Seed 默认未锁定，并断言点击“新题”后 Seed 必定改变；把混合权重设为 `60/30/10`、方向设为 `45°～90°`、玩家笔宽设为 `12 DIP`、目标参考线宽设为 `3 DIP`，抬笔后在自动下一题前截图，确认两套线宽独立。
4. 模式二在浅色主题画一笔，原位切换深色；像素级断言白画布／黑笔变为深画布／浅笔，且已提交答案完成重绘。随后在 `900×480` 下检查两个等大方格，启用 `1～10` 随机数量范围，执行绘制、撤销、重做、清空、再次绘制和手动提交。
5. 模式三在 `900×480` 下检查色相环、HSV 方块和试色画布；从练习模式切换到下一题生效的测试模式，确认提交前显示“隐藏”，试色绘制后提交并显示全部维度差。
6. 正常关闭后再次启动同一发布 EXE，自动断言模式一权重／方向／玩家笔宽／目标线宽、模式二数量范围和模式三测试模式均已恢复。
7. 检查设置与历史 JSON：题目 Seed、实际生成器／评分器／稳定器版本、冻结设置指纹和有符号颜色维度均已落盘；设置元数据分别为 `LineGeneratorV2`、`MultiLineGeneratorV2`、`ColorGeneratorV1`。

代表截图：

- [900×480 首页](./artifacts/smoke/case-0563f7f5d42646179968af3f3ba02a9b/home-900x480.png)
- [900×480 模式一](./artifacts/smoke/case-0563f7f5d42646179968af3f3ba02a9b/mode1-900x480.png)
- [模式一 12 DIP 玩家笔迹／3 DIP 目标线](./artifacts/smoke/case-0563f7f5d42646179968af3f3ba02a9b/mode1-after-answer.png)
- [模式二浅色白画布／黑笔](./artifacts/smoke/case-0563f7f5d42646179968af3f3ba02a9b/mode2-light-theme-committed-stroke.png)
- [同一笔迹切换为深色画布／浅笔](./artifacts/smoke/case-0563f7f5d42646179968af3f3ba02a9b/mode2-dark-theme-recolored-stroke.png)
- [900×480 模式二](./artifacts/smoke/case-0563f7f5d42646179968af3f3ba02a9b/mode2-900x480.png)
- [模式二手动提交](./artifacts/smoke/case-0563f7f5d42646179968af3f3ba02a9b/mode2-after-submit.png)
- [模式三测试模式提交前](./artifacts/smoke/case-0563f7f5d42646179968af3f3ba02a9b/mode3-test-before-submit.png)
- [模式三颜色维度结果](./artifacts/smoke/case-0563f7f5d42646179968af3f3ba02a9b/mode3-after-submit.png)
- [重启后设置恢复](./artifacts/smoke/case-0563f7f5d42646179968af3f3ba02a9b/settings-restored-after-restart.png)

## 发布物

| 项目 | 值 |
|---|---|
| 文件 | `artifacts\publish\win-x64\DrawAim.exe` |
| 类型 | win-x64 自包含单文件 |
| 版本 | 0.1.1 |
| 大小 | 131,041,673 bytes |
| SHA-256 | `86BAAD8E73146AEE9D568FA36B220C20163F3A6CF398956099CB42825159CCFC` |

## 尚未实机验证的边界

以下项目没有伪装成已验证：

- Windows 11；
- 2560×1440 实体 2K 显示器；
- 125%、150%、200% 实际 DPI 和跨显示器 DPI 切换；
- 实体数位板、橡皮端、真实压感曲线及各厂商驱动；
- 主观输入延迟、显示器帧时间和长时间压力测试；
- 模式一人工连续 20 题，以及模式二分别固定 1／5／10 线的完整人工验收矩阵。

当前自动化以约 8 ms 间隔注入快速鼠标点，验证了输入、绘制、提交和退出闭环；这不能替代实体笔和专业延迟测量。

Windows 10 Home／Pro 已结束微软支持，微软当前 .NET 10 支持矩阵只列出仍受支持的 Windows 10 LTSC／Enterprise。这里的 Windows 10 Pro 22H2 结果仅表示本机实测可运行，不代表微软官方支持承诺。参见 [.NET on Windows 支持矩阵](https://learn.microsoft.com/en-us/dotnet/core/install/windows#supported-versions)。
