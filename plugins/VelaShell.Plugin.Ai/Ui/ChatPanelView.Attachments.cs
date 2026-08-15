using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Platform.Storage;
using Microsoft.Extensions.AI;

namespace VelaShell.Plugin.Ai.Ui;

/// <summary>
/// 本地附件:拖到面板上,或点工具条左边的 <c>+</c> 选。图片按视觉输入发给模型,
/// 文本文件把内容原样附在消息后面。
/// </summary>
/// <remarks>
/// 与 <c>@</c> 引用是两回事:那个读的是<b>远端服务器</b>上的文件(走 SFTP),
/// 这里是本机的。运维场景里"把这张监控截图/这份本地配置发给 AI 看"很常见,
/// 之前完全没有路径。
/// </remarks>
public partial class ChatPanelView
{
    /// <summary>一条消息最多带几个本地附件。</summary>
    private const int MaxLocalAttachments = 4;

    /// <summary>单个附件的字节上限。图片要 base64 进请求体,再大就是给自己找麻烦。</summary>
    private const int MaxAttachmentBytes = 5 * 1024 * 1024;

    /// <summary>一个待发送的本地附件。</summary>
    /// <param name="Name">文件名(给用户看,也写进消息)。</param>
    /// <param name="MediaType">MIME 类型;非图片则为空。</param>
    /// <param name="Bytes">图片的原始字节;文本附件为空。</param>
    /// <param name="Text">文本附件的内容;图片为空。</param>
    private sealed record LocalAttachment(string Name, string MediaType, byte[] Bytes, string Text)
    {
        public bool IsImage => MediaType.Length > 0;
    }

    private readonly List<LocalAttachment> _attachments = [];

    /// <summary>接上拖放与 <c>+</c> 按钮。</summary>
    private void SetUpAttachments()
    {
        DragDrop.SetAllowDrop(this, true);
        AddHandler(DragDrop.DragOverEvent, OnDragOver);
        AddHandler(DragDrop.DropEvent, OnDrop);
        AttachButton.Click += (_, _) => _ = PickFilesAsync();
    }

    private void OnDragOver(object? sender, DragEventArgs e)
    {
        e.DragEffects = e.DataTransfer.TryGetFiles() is { Length: > 0 } ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private void OnDrop(object? sender, DragEventArgs e)
    {
        if (e.DataTransfer.TryGetFiles() is { Length: > 0 } items)
        {
            e.Handled = true;
            _ = AddFilesAsync([.. items.OfType<IStorageFile>()]);
        }
    }

    private async Task PickFilesAsync()
    {
        if (TopLevel.GetTopLevel(this)?.StorageProvider is not { } storage)
        {
            return;
        }
        IReadOnlyList<IStorageFile> picked = await storage.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = _loc["AttachLocal"],
            AllowMultiple = true
        });
        await AddFilesAsync(picked);
    }

    private async Task AddFilesAsync(IReadOnlyList<IStorageFile> files)
    {
        foreach (IStorageFile file in files)
        {
            if (_attachments.Count >= MaxLocalAttachments)
            {
                StatusText.Text = _loc.F("AttachLimit", MaxLocalAttachments);
                break;
            }
            try
            {
                await AddOneAsync(file);
            }
            catch (Exception ex)
            {
                _context.Log.Warn($"Attaching '{file.Name}' failed: {ex.Message}");
                StatusText.Text = $"{file.Name}: {_loc["AttachFailed"]}";
            }
        }
        RenderAttachmentChips();
    }

    private async Task AddOneAsync(IStorageFile file)
    {
        await using Stream stream = await file.OpenReadAsync();
        using var buffer = new MemoryStream();
        await stream.CopyToAsync(buffer);
        if (buffer.Length > MaxAttachmentBytes)
        {
            StatusText.Text = $"{file.Name}: {_loc.F("AttachTooBig", MaxAttachmentBytes / 1024 / 1024)}";
            return;
        }
        byte[] bytes = buffer.ToArray();
        string mediaType = ImageMediaType(file.Name);
        if (mediaType.Length > 0)
        {
            _attachments.Add(new LocalAttachment(file.Name, mediaType, bytes, ""));
            return;
        }
        // 不是图片就当文本读;二进制会解出一堆乱码,那对模型毫无用处,直接挡掉
        string text = System.Text.Encoding.UTF8.GetString(bytes);
        if (text.Contains('\0'))
        {
            StatusText.Text = $"{file.Name}: {_loc["AttachBinary"]}";
            return;
        }
        _attachments.Add(new LocalAttachment(file.Name, "", [], text));
    }

    /// <summary>按扩展名判断图片类型;不是图片返回空串。</summary>
    private static string ImageMediaType(string name) => Path.GetExtension(name).ToLowerInvariant() switch
    {
        ".png" => "image/png",
        ".jpg" or ".jpeg" => "image/jpeg",
        ".gif" => "image/gif",
        ".webp" => "image/webp",
        ".bmp" => "image/bmp",
        _ => ""
    };

    /// <summary>把待发附件画成一排可删的芯片(输入框上方)。</summary>
    private void RenderAttachmentChips()
    {
        AttachmentBar.Children.Clear();
        foreach (LocalAttachment attachment in _attachments)
        {
            var row = new StackPanel { Orientation = Avalonia.Layout.Orientation.Horizontal, Spacing = 4 };
            row.Children.Add(MakeIcon(attachment.IsImage ? "Icon.file-text" : "Icon.file", "VelaAccent", 10));
            row.Children.Add(new TextBlock
            {
                Classes = { "refChipText" },
                Text = attachment.Name,
                VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center
            });
            var chip = new Border { Classes = { "refChip" }, Child = row, Cursor = new Cursor(StandardCursorType.Hand) };
            ToolTip.SetTip(chip, _loc["RemoveAttachment"]);
            chip.PointerPressed += (_, e) =>
            {
                e.Handled = true;
                _attachments.Remove(attachment);
                RenderAttachmentChips();
            };
            AttachmentBar.Children.Add(chip);
        }
        AttachmentBar.IsVisible = AttachmentBar.Children.Count > 0;
    }

    /// <summary>
    /// 把待发附件并进这一轮的用户消息:图片作为视觉输入,文本附在正文之后。
    /// 调用后清空待发列表。
    /// </summary>
    private List<AIContent> BuildUserContents(string modelText)
    {
        var contents = new List<AIContent>();
        var text = new System.Text.StringBuilder(modelText);
        foreach (LocalAttachment attachment in _attachments.Where(a => !a.IsImage))
        {
            text.AppendLine().AppendLine().AppendLine($"--- {attachment.Name} ---").Append(attachment.Text);
        }
        contents.Add(new TextContent(text.ToString()));
        foreach (LocalAttachment image in _attachments.Where(a => a.IsImage))
        {
            contents.Add(new DataContent(image.Bytes, image.MediaType));
        }
        return contents;
    }

    /// <summary>这一轮附件在历史里的留痕(图片进不了纯文本历史,至少留个名)。</summary>
    private string AttachmentTrace()
    {
        if (_attachments.Count == 0)
        {
            return "";
        }
        return "\n" + string.Join(" ", _attachments.Select(a => $"[{a.Name}]"));
    }

    private void ClearAttachments()
    {
        _attachments.Clear();
        RenderAttachmentChips();
    }
}
