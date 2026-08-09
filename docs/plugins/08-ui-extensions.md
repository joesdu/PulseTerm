# 08 · UI 扩展:贡献点渲染、VelaUI、专用表面

前提(D6):插件进程零 Avalonia 依赖,一切 UI 由宿主渲染。三个层次,
复杂度递增:

```text
L1 声明式贡献点   命令/菜单/状态栏/侧栏占位/设置页 —— manifest 静态声明 + 少量动态更新
L2 VelaUI        声明式控件树(虚拟 UI),插件发 patch、宿主渲染、事件回传 —— 承载面板与文档页
L3 专用表面      图像表面(共享内存位图)、音频输出、终端输出通道 —— 高带宽/特殊媒介
```

## 1. L1:贡献点渲染

- **命令**:进命令面板(含插件名前缀与图标);执行 → 若插件未激活先
  触发 `onCommand` 激活 → `ExecuteCommandAsync`。插件可动态改
  enabled/可见性(`ui/commandUpdate` 通知)。
- **菜单挂载点**(apiLevel 1 冻结清单):`commandPalette`、
  `sftp/item/context`、`localFiles/item/context`、`terminal/context`、
  `session/context`、`statusBar/item/context`、`view/title`。`when`
  表达式决定可见性,宿主求值,不唤醒插件。
- **状态栏项**:文本+图标+tooltip+点击命令;更新经通知(节流 4Hz——
  MP3 播放进度这种高频更新要求插件端自行降频,SDK 帮做)。
- **侧栏视图**:占位图标进现有 Sidebar(与 SidebarView 集成,遵守其
  ToolTip 右侧显示的既有约定);首次展开触发 `onView` 激活,内容区
  即一个 VelaUI 表面。
- **文档页**:插件文档类型注册进 VelaDock 的 `DockDocument` 模型,
  与终端 tab 平级参与拖拽分屏;内容为 VelaUI 表面或图像表面。关闭
  文档 → 通知插件(SurfaceClosed)。
- **设置页**:manifest `contributes.settings` 由宿主用现有设置 UI 风格
  自动生成表单(支持 string/number/boolean/enum/keybinding),复杂
  设置 UI 可声明 `"custom": true` 换成 VelaUI 表面。

## 2. L2:VelaUI 声明式界面树

### 2.1 模型

React 式单向数据流,但序列化边界在"元素树"而非 DOM:

```text
插件侧(SDK):  状态 → Build() 产出虚拟树 → 与上一棵树 diff → UiPatch[] 
宿主侧:        patch 应用到表面的元素树 → 映射/复用 Avalonia 控件 → 渲染
事件:          用户交互 → UiEvent(surfaceId, elementId, eventName, payload)→ 插件处理 → 新状态 → 循环
```

### 2.2 控件白名单(apiLevel 1)

布局:`StackPanel` `Grid` `WrapPanel` `ScrollViewer` `Border` `Expander` `TabControl`
文本:`TextBlock`(inline 样式子集)`SelectableText`
输入:`Button` `ToggleButton` `TextBox` `PasswordBox`(值不回传明文,配 secrets 用)
      `CheckBox` `RadioGroup` `ComboBox` `Slider` `DatePicker`
数据:`ListView`(虚拟化,增量数据协议)`TreeView` `ProgressBar` `Image`(小图,内联/资源 id)
      `Sparkline`(S6 仪表盘用的轻量折线)
特殊:`ImageSurfaceHost`(嵌 L3 图像表面)`Separator` `HyperlinkButton`

样式:不开放任意样式,只开放**语义化 token**(`accent` `danger`
`muted`、间距枚举、字号档位)——保证任何插件在五语言、明暗主题、DPI
缩放下观感与宿主一致,这是拿"不能自由定制"换"永不破相"的明确取舍。

### 2.3 协议要点

- 元素 id 由 SDK 分配(diff 稳定键);patch 类型:Insert/Remove/
  Replace/SetProps/ListSplice(列表专用,虚拟化配套)。
- 事件防抖:TextBox 文本变更默认 150ms 去抖;滚动/指针移动**不回传**
  (apiLevel 1 不支持逐帧交互,防止把 IPC 当游戏通道)。
- 表面掉线策略:插件崩溃/无响应 → 表面盖灰 + "插件无响应"横幅与重启
  按钮;重启后插件负责重建树(SDK 的状态层可自动重放最后一棵树)。
- 大小限制:单表面元素数上限 5000、单 patch 512KB;超限拒绝并计入
  插件质量信号(防误用,ListView 增量协议是正道)。

### 2.4 SDK 开发体验

```csharp
surface.SetBody(ui => ui.StackPanel(spacing: Space.M)
    .Children(
        ui.TextBlock(track.Title).FontSize(FontSize.L),
        ui.Slider(value: state.Position, max: track.Duration)
          .OnChanged(v => player.SeekAsync(v)),
        ui.StackPanel(Orientation.Horizontal).Children(
            ui.Button(Icon.Previous).OnClick(player.PrevAsync),
            ui.Button(state.Playing ? Icon.Pause : Icon.Play).OnClick(player.ToggleAsync),
            ui.Button(Icon.Next).OnClick(player.NextAsync))));
```

流式构建器 + `SetState` 触发重建/diff;远期可评估 C# 源生成器做
XAML-like DSL,v1 不做。

## 3. L3:专用表面

### 3.1 图像表面(S1)

```text
插件:CreateImageSurfaceAsync(w, h, format) → 宿主建 MemoryMappedFile(双缓冲)
插件:解码像素写入后缓冲 → Present(序号) RPC → 宿主交换缓冲 → WriteableBitmap 上屏
宿主:缩放/平移/适配窗口全部宿主侧手势处理(插件只出像素);
      窗口尺寸变化事件回传,插件可选择重解码更高分辨率
```

- 格式:BGRA8888(与 Avalonia WriteableBitmap 对齐,零转换);
  大图(>8K 边长)要求插件自行降采样。
- 共享内存段按需分配、表面关闭即回收;插件崩溃由宿主统一回收。

### 3.2 音频输出

见 07 §9(能力域),UI 侧只有状态栏/面板由插件用 L1/L2 自建。

### 3.3 终端输出通道

见 07 §4:插件专属只读伪终端文档页,复用现有终端渲染器(得到 ANSI
配色、选择、搜索等全部既有能力),适合自动化插件输出执行日志。

## 4. 主题与 i18n

- 主题:语义 token 由宿主解析到当前主题;`themeChanged` 事件推送,
  VelaUI 树无需重建(token 在宿主侧解引用)。
- i18n:静态贡献点走 NLS(03 §6);VelaUI 动态文本由插件自己出字符串,
  SDK 提供 `ctx.I18n.Locale` 与变更事件,官方模板内置五语言资源结构
  引导插件跟随宿主语言。

## 5. 开发计划(本分项)

| 任务 | 说明 | 依赖 | 估算 |
| --- | --- | --- | --- |
| U-1 | L1:命令/命令面板/菜单挂载点接线(含 when 求值接入) | M-4 | 4d |
| U-2 | L1:状态栏项 + 侧栏占位视图 + `onView` 激活链路 | U-1 | 3d |
| U-3 | L1:文档页类型接入 VelaDock(DockDocument 扩展、关闭/拖拽语义) | U-1 | 3d |
| U-4 | L1:设置页表单生成器 | U-1 | 3d |
| U-5 | L2:元素树模型 + patch 协议 + 宿主渲染器(白名单控件映射、控件复用池) | P-3 | 6d |
| U-6 | L2:事件回传、去抖、ListView 增量/虚拟化、限额与质量信号 | U-5 | 4d |
| U-7 | L2:SDK 构建器 + diff 器 + 状态层(重启重放) | U-5 | 4d |
| U-8 | L2:掉线遮罩/重启横幅(接 H-4 无响应信号) | U-5, H-4 | 2d |
| U-9 | L3:图像表面(mmf 双缓冲、Present、手势宿主化、回收) | P-6 | 4d |
| U-10 | 主题 token 体系 + themeChanged;五语言接入验证 | U-5 | 2d |

验收:图片查看器示例(L1 菜单 + L3 表面)与 MP3 播放器示例(L1 状态
栏 + L2 面板)不改宿主一行专用代码即可完整运行;VelaUI 1000 行 ListView
滚动流畅(虚拟化生效,IPC 流量与可视区成正比)。
