namespace VelaShell.PluginSdk;

/// <summary>
/// 插件入口契约:一个插件包恰好包含一个实现(以 <see cref="VelaPluginAttribute" /> 标注)。
/// 实例由宿主创建(要求公开无参构造),生命周期为 Activate → (运行) → Deactivate。
/// </summary>
public interface IVelaPlugin
{
    /// <summary>
    /// 激活插件。<paramref name="context" /> 是插件获得一切宿主能力的唯一入口,
    /// 在 <see cref="DeactivateAsync" /> 完成前始终有效。
    /// 本方法应快速返回(宿主默认限时 10 秒);长任务请自行启动后台任务,
    /// 并用 <see cref="IPluginContext.Shutdown" /> 令牌响应停机。
    /// </summary>
    /// <param name="context">插件上下文,激活后可长期持有。</param>
    /// <param name="cancellationToken">激活超时或宿主停机时取消。</param>
    Task ActivateAsync(IPluginContext context, CancellationToken cancellationToken);

    /// <summary>
    /// 停用插件:释放自有资源、停止后台任务、落盘未保存状态。
    /// 必须快速完成(宿主默认限时约 2 秒,应用退出路径上超时即被放弃),
    /// 通过 SDK 注册的命令与事件订阅由宿主自动清理,无需手动注销。
    /// </summary>
    /// <param name="cancellationToken">超出停用时限时取消。</param>
    Task DeactivateAsync(CancellationToken cancellationToken);
}
