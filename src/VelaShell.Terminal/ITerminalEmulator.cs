using Avalonia.Controls;
using Avalonia.Input;

namespace VelaShell.Terminal;

/// <summary>
/// 终端仿真器接口,可在多种实现之间切换
/// (AvaloniaTerminal、xterm.js 等)。
/// </summary>
public interface ITerminalEmulator : IDisposable
{
    /// <summary>
    /// 当前光标行位置
    /// </summary>
    int CursorRow { get; }

    /// <summary>
    /// 当前光标列位置
    /// </summary>
    int CursorCol { get; }

    /// <summary>
    /// 要保留的回滚行数
    /// </summary>
    int ScrollbackLines { get; set; }

    /// <summary>
    /// 要嵌入 UI 的 Avalonia 控件
    /// </summary>
    Control Control { get; }

    /// <summary>
    /// 当前列数
    /// </summary>
    int Columns { get; }

    /// <summary>
    /// 当前行数
    /// </summary>
    int Rows { get; }

    /// <summary>
    /// 回滚缓冲区,保存已滚出可视区域的历史行。
    /// </summary>
    ScrollbackBuffer ScrollbackBuffer { get; }

    /// <summary>
    /// 缓冲区中的总行数(回滚历史行 + 当前可视行)。
    /// </summary>
    int TotalLines { get; }

    /// <summary>
    /// 当前视口顶部在总行序列中的行索引(用于滚动定位)。
    /// </summary>
    int ViewportRow { get; }

    /// <summary>
    /// 把来自 SSH 流的原始字节喂入终端
    /// </summary>
    void Feed(byte[] data);

    /// <summary>
    /// 改变终端尺寸
    /// </summary>
    void Resize(int cols, int rows);

    /// <summary>
    /// 用户键入输入时触发(用于发送到 SSH)
    /// </summary>
    event Action<byte[]>? UserInput;

    /// <summary>
    /// 仅"用户产生"的输入(键盘/IME/鼠标上报/粘贴/程序化写入),不含终端的协议自动应答
    /// (光标位置报告、设备属性等也经 <see cref="UserInput" /> 发往 PTY,但并非用户键入)。
    /// 命令补全的行跟踪(plan.md #16)只应订阅此事件,否则会话初期的 ESC 应答会把
    /// 跟踪器永久打进未知态。
    /// </summary>
    event Action<byte[]>? TypedInput;

    /// <summary>
    /// 终端的字符单元格网格尺寸变化时触发(例如控件以新尺寸完成布局),
    /// 以便宿主 PTY 调整大小与之匹配。参数:(columns, rows)。
    /// </summary>
    event Action<int, int>? PtySizeChanged;

    /// <summary>
    /// shell 经 OSC 7 上报当前工作目录时触发(绝对路径)。用于「文件浏览器跟随终端目录」。
    /// 仅在 shell 发出 OSC 7 时有效(VelaShell 注入的 bash 提示符脚本会发)。来自 feed 线程。
    /// </summary>
    event Action<string>? WorkingDirectoryChanged;

    /// <summary>
    /// 以编程方式发送输入字节,如同用户键入一样
    /// </summary>
    void WriteInput(byte[] data);

    /// <summary>按终端自身编码和状态发送已提交文本。</summary>
    void WriteTextInput(string text);

    /// <summary>按终端自身模式编码并发送一个非文本按键。</summary>
    bool WriteKeyInput(Key key, KeyModifiers modifiers);

    /// <summary>按终端的 bracketed-paste 状态发送粘贴文本。</summary>
    void WritePasteInput(string text);

    /// <summary>
    /// 获取终端缓冲区中指定行的文本内容
    /// </summary>
    string GetBufferLine(int row);
}
