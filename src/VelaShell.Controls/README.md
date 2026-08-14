# VelaShell.Controls

> 可复用控件库 + 设计 Token —— 应用的视觉基元与主题基础设施。

`VelaShell.Controls` 提供跨视图复用的自定义 Avalonia 控件，以及**设计 Token 化**的资源字典（颜色、间距、字体、图标）。它让全应用的外观从单一来源驱动，杜绝硬编码颜色，从而支持深色/浅色/系统主题与品牌定制的运行时切换。它是纯控件层，不引用任何其他 VelaShell 项目。

> 目前重心在 **Token 与图标基建**：控件本体只有图标与资源监视用的几个绘制控件，业务性视图（状态栏、标签条、面板等）仍在应用入口项目 `VelaShell` 内以 XAML 组合实现，引用本项目的 Token 与图标。设计稿要求的逐帧自定义控件库仍是持续演进方向（见 `docs/架构设计.md` §6）。

## 🗂️ 目录结构

| 路径 | 职责 |
|------|------|
| `Controls/LucideIcon.cs` | Lucide 图标控件：按名称从 `Icons.axaml` 取几何路径渲染矢量图标，随主题着色。 |
| `Controls/TimeSeriesChart.cs` | 时序图：定长滚动窗口（默认 60 采样点）的「面积 + 折线」，支持多曲线叠加与上下行镜像；每条曲线是 `ChartSeries` 子元素（必须进可视树，否则 DataContext 与 DynamicResource 都解析不到）。 |
| `Controls/UsageHeatGrid.cs` | 逻辑处理器热力网格：一次 Render 画完全部格子（128 核以上不逐格建控件），五级色阶、自适应列数、可点选；高度随行数增长，外套 ScrollViewer 即得滚动。 |
| `Controls/MeterBar.cs` | 容量 / 占用条：圆角轨道 + 填充，按 >70% / >90% 阈值自动转警告与危险色。 |
| `Themes/VelaTokens.axaml` `VelaShellTokens.axaml` | **设计 Token 定义**：颜色、间距、圆角、字体等语义化资源，主题切换的单一真源。 |
| `Themes/Icons.axaml` | 图标几何路径资源字典（Lucide，stroke 2 / 24×24 viewBox）。 |
| `Assets/Fonts/` | **内置字体**（随程序分发，三平台一致渲染，SIL OFL 1.1）：Cascadia Mono 四静态字重，终端默认字体。刻意不用连字版 —— 本终端按格钉排，连字无法正确呈现；CJK 走系统回退。由 `Program.BuildAvaloniaApp` 以 `fonts:VelaShell` 键注册为 `EmbeddedFontCollection`。 |
| `DependencyInjection/ControlsServiceCollectionExtensions.cs` | 控件相关服务的 DI 注册入口。 |
| `Properties/AssemblyInfo.cs` | Avalonia 主题程序集元数据。 |

## 🔑 核心思路

- **Token 化设计**：所有视觉常量以语义命名的资源形式集中在 `Themes/*.axaml`，界面引用 Token 而非字面值，实现「改一处、变全局」与运行时换肤。
- **无硬编码颜色**：控件模板一律绑定 Token，深色/浅色主题只需替换 Token 值集合。
- **框架级复用**：控件不含业务逻辑，只关注呈现，可被任意 ViewModel/视图消费。

## 🔗 依赖关系

- **引用**：`Avalonia`、`Avalonia.Themes.Fluent`。**不引用任何其他 VelaShell 项目**（连 `Core` 都不引用）。
- **被引用**：`VelaShell`（App）。

> 启用 `AvaloniaUseCompiledBindingsByDefault`。测试见 [`tests/VelaShell.Controls.Tests`](../../tests/VelaShell.Controls.Tests)。
