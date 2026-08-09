# 04 · PluginHost 进程设计

PluginHost 是随主程序分发的独立可执行(`VelaShell.PluginHost`,
`OutputType=Exe`,net11.0),职责单一:**在一个隔离进程里装载一个插件,
把它接到宿主的 RPC 通道上,并保证自己死得干净**。

## 1. 进程拉起与参数

宿主以如下方式启动 PluginHost(参数经环境变量传递,避免命令行泄漏到
进程列表):

```text
VELA_PLUGIN_ID        = acme.image-viewer
VELA_PLUGIN_DIR       = <installed/<id>/<ver>/ 绝对路径>
VELA_PLUGIN_DATA_DIR  = <data/<id>/ 绝对路径>
VELA_PIPE_NAME        = velashell-plugin-<随机 GUID>
VELA_AUTH_TOKEN       = <一次性 256bit 随机令牌>
VELA_HOST_PID         = <宿主进程 id>
VELA_API_LEVEL        = 1
VELA_LOCALE           = zh-Hans
```

- **认证**:管道名含随机 GUID + 首帧必须回传 AUTH_TOKEN,双重防止本机
  其它进程抢连(详见 05 §3)。Windows 上管道 ACL 额外限定当前用户 SID;
  Unix 上 socket 文件权限 0600。
- **孤儿自杀**:PluginHost 持续 watch 宿主 PID(进程句柄等待),宿主
  消失(崩溃/被杀)后 2s 内自我退出——保证不会留下孤儿插件进程。
  反向地,宿主也在退出路径上主动终止全部子进程(含异常退出的
  finalizer 兜底,Windows 上用 Job Object `KILL_ON_JOB_CLOSE` 强保证)。

## 2. 程序集装载(收集式 ALC)

```text
PluginHost 默认 ALC:PluginHost 自身 + PluginProtocol + PluginSdk + StreamJsonRpc 等共享层
      │
      └── PluginLoadContext : AssemblyLoadContext(isCollectible: true)
             以 entry 程序集的 .deps.json 为解析依据(AssemblyDependencyResolver)
             装载插件及其全部私有依赖
```

解析规则:

- **契约类型上钻**:`PluginProtocol` / `PluginSdk` / `StreamJsonRpc` 及其
  传递闭包永远解析到默认 ALC(否则类型不同一,RPC 桥接直接失败)。
  维护一份显式的共享程序集白名单,装载器对白名单命中项返回 null 让
  默认 ALC 接管。
- 其余依赖一律从插件目录解析(插件自带),与 PluginHost 自身依赖版本
  互不干扰——这是 D2 的核心收益:插件可以用和宿主不同版本的
  Newtonsoft.Json、SkiaSharp 等。
- 原生依赖(`runtimes/<rid>/native`)经 `AssemblyDependencyResolver`
  的原生解析路径支持;文档明确警告:**含原生代码的插件无法热卸载干净**
  (native dll 无法从进程卸载),此类插件的"重载"直接走进程重启。

**为什么进程独占还要收集式 ALC?** 两个目的:(1)开发模式热重载——
`vela-plugin dev` 监视构建输出,变更时卸载旧 ALC、装新程序集,保留进程
与连接,内环迭代秒级;(2)为远期 shared 宿主模式(多个轻量可信插件共
进程)保留实现路径。生产环境的卸载/升级不依赖 ALC 卸载成功——**进程退
出是最终兜底**,因此 ALC 卸不干净(常见的事件订阅残留)不构成正确性问题。

## 3. 宿主内的运行结构

```text
Main()
 ├─ 建立管道连接 + 认证 + 握手(见 05)
 ├─ PluginLoadContext 装载 entry → 反射发现 [VelaPlugin] 类型 → 实例化
 ├─ JsonRpc 双向挂接:
 │    · 本地暴露 IPluginEndpoint(activate/deactivate/command/uiEvent/ping...)
 │    · 远端代理 IHostEndpoint(全部能力调用出口)
 ├─ 构造 IPluginContext(能力代理指向远端)交给插件 ActivateAsync
 └─ 阻塞等待:连接断开 / 停机指令 / 宿主 PID 消失 → 有序清理 → 退出
```

线程与调度:

- PluginHost **无 UI 线程**;插件回调默认在线程池执行,SDK 不提供
  SynchronizationContext。文档明确:插件代码须是异步友好的,长计算请
  自行开线程——即便插件把自己的进程堵死,宿主与其它插件不受影响
  (这正是隔离的意义),但会触发心跳失联处置(§4)。
- 插件抛出的未处理异常:RPC 调用内的异常序列化回宿主记日志并回给调用
  方;后台线程未观察异常经 `AppDomain.UnhandledException` 记日志后进程
  退出,走崩溃恢复路径。

## 4. 健康监控与故障处置(宿主侧 PluginSupervisor)

| 信号 | 检测 | 处置 |
| --- | --- | --- |
| 进程退出(任何原因) | Process.Exited | 记录退出码与日志尾部 → 崩溃恢复流程 |
| 心跳丢失 | 宿主每 5s `ping`,连续 3 次无响应(15s) | 标记 Unresponsive:UI 表面盖"无响应"遮罩 + 提供"等待/终止"选择;60s 仍无响应且无用户处置 → 强杀进入崩溃恢复 |
| 协议错误 | RPC 反序列化失败/未知方法风暴 | 视为崩溃,强杀 |
| 内存超限 | 周期采样 WorkingSet,超过 manifest `resources.maxMemoryMB`(默认 512) | 先 GC 通知一次,10s 后复测仍超 → 按崩溃处置;管理页显示内存曲线 |
| CPU 长期满载 | 采样窗口内 >90% 持续 5min | 不自动杀(可能是合法长任务),管理页黄标提示 + 通知用户 |

崩溃恢复:退避 1s → 5s → 30s,10 分钟窗口内最多 3 次;超限进入
Faulted(不再自动拉起,管理页红标,一键查看日志/手动重启)。**恢复后
不自动重放未完成的调用**——能力调用的失败以异常形式如实回给当时的调用
方,状态恢复是插件自己的责任(SDK 提供 `Activation.IsRestart` 标志与
Storage 能力协助)。

资源控制的实现分层(诚实声明能做到什么):

- v1:**监测 + 处置**(上表),Windows 上进程挂 Job Object 附带
  `KILL_ON_JOB_CLOSE` 与内存上限硬限制;mac/Linux v1 仅监测。
- v2(与 12 的沙箱路线合并):Linux cgroups v2、macOS
  `posix_spawn` 资源属性,详见 [12-security-threat-model.md](12-security-threat-model.md)。

## 5. 停机与卸载序列

```text
宿主发 shutdown(reason, timeoutMs=5000)
 → PluginHost 触发 context.Shutdown 取消令牌
 → 调插件 DeactivateAsync(限时)
 → flush 日志、断开 RPC、进程退出码 0
超时未退 → 宿主 Kill(entireProcessTree: true) → 记录"不体面退出"计数(管理页可见,作为插件质量信号)
```

## 6. 开发模式(dev loop)

`vela-plugin dev --project .` 做三件事:

1. 以 `--watch` 构建插件项目;
2. 通知宿主(本机管理接口,仅 debug 构建开启)以"开发插件"身份装载
   构建输出目录(跳过签名校验,权限授权走正常流程但标注 DEV);
3. 文件变更 → 宿主指挥 PluginHost 卸载 ALC → 重载 → 重放
   Activate(保留权限授予与 UI 占位,不重启进程)。

调试:`vela-plugin dev --wait-debugger` 让 PluginHost 在装载后等待调试器
附加;VS/Rider 直接 Attach 到 PluginHost 进程即可断点(模板自带
launchSettings 配置)。

## 7. 开发计划(本分项)

| 任务 | 说明 | 依赖 | 估算 |
| --- | --- | --- | --- |
| H-1 | PluginHost 可执行骨架:参数、认证连接、孤儿自杀、退出清理 | A-2 | 3d |
| H-2 | PluginLoadContext:deps.json 解析、共享白名单上钻、原生依赖;卸载往返测试 | H-1 | 3d |
| H-3 | 宿主侧进程管理:拉起、Job Object(Win)、停机序列、强杀兜底 | H-1 | 3d |
| H-4 | PluginSupervisor:心跳、崩溃检测、退避重启、Faulted 流转;与 03 状态机对接 | H-3, M-3 | 4d |
| H-5 | 资源监测(内存/CPU 采样与处置)+ 管理页数据源 | H-4 | 2d |
| H-6 | 日志管线:stdout/stderr 落盘、结构化日志通道、日志查看入口 | H-1 | 2d |
| H-7 | 开发模式:dev 装载、ALC 热重载、wait-debugger | H-2, 依赖 09 的 CLI 骨架 | 3d |
| H-8 | 混沌测试组:杀进程/堵死线程/吃内存/协议乱字节 四类故障注入插件 + 自动化验收(主程序零影响) | H-4 | 3d |

验收(对应 G1):H-8 的四类故障插件在 CI 全绿——任一故障发生时,主进
程 UI 线程无阻塞(帧率探针)、其它插件 RPC 延迟无劣化、故障插件按状态
机预期流转。
