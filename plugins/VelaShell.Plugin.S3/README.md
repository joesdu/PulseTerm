# VelaShell.Plugin.S3

S3 兼容对象存储的官方插件:Amazon S3、MinIO、Ceph RGW、阿里云 OSS、腾讯云 COS、Cloudflare R2 等。

- 设计与取舍:[`docs/S3协议插件化设计.md`](../../docs/S3协议插件化设计.md)
- 协议能力域用法:[`docs/plugins/dev-guide.md`](../../docs/plugins/dev-guide.md) §5.13

## 它做了什么

| 面 | 内容 |
| --- | --- |
| 文件管理器 | 实现 SDK 的 `IProtocolFileSystem`,把平的键空间翻译成目录树;宿主的双栏浏览、传输队列、限速、拖放、冲突策略**零改动**复用 |
| 桶管理器 | 概览 + 对象版本 + 未完成分片上传 + **22 项桶配置**(表单 / JSON 双形态编辑器) |
| 对象检视器 | 详情 / 标签 / 权限 / 保留与合法保留 / 存储与加密 / S3 Select / 预签名 URL |

S3 协议的 116 个操作里,除 4 个客户端无法或不应调用的(见设计文档 §九)之外全部可达。

## 文件导览

| 文件 | 说明 |
| --- | --- |
| `S3Plugin.cs` | 入口:注册协议描述(页签、连接表单、右键动作、能力位) |
| `S3ProtocolFileSystem.cs` | 协议实现。内部以 `Guid` 为会话键,宿主的不透明字符串键只在 `IProtocolFileSystem` 的**显式实现**那一层映射 |
| `S3ManagementService.cs` | 文件管理器之外的近百个操作;与文件系统共用同一条会话与同一个客户端 |
| `S3ObjectPath.cs` | 路径 ↔「桶 + 键」的双向翻译 —— 整个后端都架在这层映射上 |
| `S3Interop.cs` | AWSSDK 异常 → `VelaS3*` 异常族。**AWSSDK 的类型不越过插件边界** |
| `S3ConfigKind.cs` | 22 项桶配置的枚举与元数据表;桶管理器的导航由它驱动 |
| `Ui/` | 两个面板(不引 ReactiveUI,理由见设计文档 §5.3) |
| `ThrottledStream.cs` | 宿主同名实现的副本 —— 限速上限仍由宿主统一给出 |

## 开发

`plugins/Directory.Build.targets` 会在构建后把输出镜像到 `src/VelaShell/bin/<配置>/net11.0/plugins/velashell-s3/`,
直接 F5 主程序即可加载。协议页签在**发现期**就出现在连接配置页(不装载本程序集),
用户点到它才触发 `onProtocol:velashell.s3` 惰性激活。

测试:`tests/VelaShell.Plugin.S3.Tests`(环回 S3 服务器会重算 SigV4 并比对 —— 守的是
"客户端配置有没有被正确送上线",详见设计文档 §8.1)。
