using VelaShell.ViewModels;

namespace VelaShell.Tests.ViewModels;

/// <summary>
/// 批量传输冲突的粘性决定(<see cref="FileBrowserViewModel.DecideConflictAsync" />):
/// “全部覆盖/全部跳过”只问一次即沿用到本批次其余所有冲突,免去逐文件弹窗
/// (拖入上千文件的文件夹与服务端冲突时的关键)。
/// </summary>
[TestClass]
[TestCategory("FileBrowser")]
public class FileConflictResolutionTests
{
    private static Func<string, Task<FileConflictResolution>> Always(
        FileConflictResolution answer,
        Action onCall
    ) =>
        _ =>
        {
            onCall();
            return Task.FromResult(answer);
        };

    [TestMethod]
    public async Task OverwriteAll_AnsweredOnce_AppliesToRemainingWithoutReprompting()
    {
        var decision = new FileBrowserViewModel.BatchConflictDecision();
        int prompts = 0;
        Func<string, Task<FileConflictResolution>> confirm = Always(
            FileConflictResolution.OverwriteAll,
            () => prompts++
        );

        // 首个冲突弹一次窗;其余 999 个直接沿用,不再弹。
        bool first = await FileBrowserViewModel.DecideConflictAsync("/r/f0", confirm, decision);
        Assert.IsTrue(first);
        for (int i = 1; i < 1000; i++)
        {
            Assert.IsTrue(await FileBrowserViewModel.DecideConflictAsync($"/r/f{i}", confirm, decision));
        }
        Assert.AreEqual(1, prompts, "全部覆盖应只弹一次窗");
    }

    [TestMethod]
    public async Task SkipAll_AnsweredOnce_SkipsRemainingWithoutReprompting()
    {
        var decision = new FileBrowserViewModel.BatchConflictDecision();
        int prompts = 0;
        Func<string, Task<FileConflictResolution>> confirm = Always(
            FileConflictResolution.SkipAll,
            () => prompts++
        );

        bool first = await FileBrowserViewModel.DecideConflictAsync("/r/f0", confirm, decision);
        Assert.IsFalse(first);
        for (int i = 1; i < 1000; i++)
        {
            Assert.IsFalse(await FileBrowserViewModel.DecideConflictAsync($"/r/f{i}", confirm, decision));
        }
        Assert.AreEqual(1, prompts, "全部跳过应只弹一次窗");
    }

    [TestMethod]
    public async Task SingleOverwrite_DoesNotStick_RepromptsEachConflict()
    {
        var decision = new FileBrowserViewModel.BatchConflictDecision();
        int prompts = 0;
        Func<string, Task<FileConflictResolution>> confirm = Always(
            FileConflictResolution.Overwrite,
            () => prompts++
        );

        for (int i = 0; i < 5; i++)
        {
            Assert.IsTrue(await FileBrowserViewModel.DecideConflictAsync($"/r/f{i}", confirm, decision));
        }
        Assert.AreEqual(5, prompts, "单个覆盖不应设置粘性,应逐个询问");
        Assert.IsNull(decision.OverwriteAll);
    }

    [TestMethod]
    public async Task SingleSkip_DoesNotStick_RepromptsEachConflict()
    {
        var decision = new FileBrowserViewModel.BatchConflictDecision();
        int prompts = 0;
        Func<string, Task<FileConflictResolution>> confirm = Always(
            FileConflictResolution.Skip,
            () => prompts++
        );

        for (int i = 0; i < 5; i++)
        {
            Assert.IsFalse(await FileBrowserViewModel.DecideConflictAsync($"/r/f{i}", confirm, decision));
        }
        Assert.AreEqual(5, prompts, "单个跳过不应设置粘性,应逐个询问");
        Assert.IsNull(decision.OverwriteAll);
    }

    [TestMethod]
    public async Task NullConfirm_DefaultsToOverwrite_NeverPrompts()
    {
        var decision = new FileBrowserViewModel.BatchConflictDecision();
        // 无 UI 回调(理论边界):保持既有行为——默认覆盖,不阻塞。
        Assert.IsTrue(await FileBrowserViewModel.DecideConflictAsync("/r/f", null, decision));
        Assert.IsNull(decision.OverwriteAll);
    }

    // ---- 目标"同名但内容对不上" ----
    //
    // 续传起点核实失败意味着目标那半截并不是源文件的开头 —— 文件变了。这是一次普通的
    // 同名冲突,以前却以"请删除远端/本地文件后整份重传"收场,把本该由策略决定的事推给用户。
    // 现在按既有冲突策略处理,询问策略下弹的就是常规冲突对话框。

    [TestMethod]
    public async Task ChangedTarget_UnderAskPolicy_PromptsAndHonoursTheAnswer()
    {
        var decision = new FileBrowserViewModel.BatchConflictDecision();
        int prompts = 0;

        Assert.AreEqual(
            FileBrowserViewModel.ChangedTargetChoice.Overwrite,
            await FileBrowserViewModel.DecideChangedTargetAsync(
                "ask", "/r/changed.bin", Always(FileConflictResolution.Overwrite, () => prompts++), decision));

        Assert.AreEqual(
            FileBrowserViewModel.ChangedTargetChoice.Skip,
            await FileBrowserViewModel.DecideChangedTargetAsync(
                "ask", "/r/changed.bin", Always(FileConflictResolution.Skip, () => prompts++), decision));

        Assert.AreEqual(2, prompts, "询问策略下必须真的问用户,而不是直接失败。");
    }

    [TestMethod]
    public async Task ChangedTarget_ReusesBatchStickyDecision_WithoutRepromptingPerFile()
    {
        // 重传一个几百文件的目录、其中大批文件都改过时,不能逐个弹窗。
        var decision = new FileBrowserViewModel.BatchConflictDecision();
        int prompts = 0;
        Func<string, Task<FileConflictResolution>> confirm = Always(
            FileConflictResolution.SkipAll,
            () => prompts++
        );

        for (int i = 0; i < 200; i++)
        {
            Assert.AreEqual(
                FileBrowserViewModel.ChangedTargetChoice.Skip,
                await FileBrowserViewModel.DecideChangedTargetAsync("ask", $"/r/f{i}", confirm, decision));
        }
        Assert.AreEqual(1, prompts, "本批次的“全部跳过”应沿用到其余变化文件。");
    }

    // 期望值用名字传:枚举是 internal,不能出现在 public 测试方法的签名里。
    [TestMethod]
    [DataRow("overwrite", nameof(FileBrowserViewModel.ChangedTargetChoice.Overwrite))]
    [DataRow("skip", nameof(FileBrowserViewModel.ChangedTargetChoice.Skip))]
    [DataRow("rename", nameof(FileBrowserViewModel.ChangedTargetChoice.Rename))]
    public async Task ChangedTarget_UnderNonAskPolicies_DecidesWithoutPrompting(
        string policy,
        string expected
    )
    {
        var decision = new FileBrowserViewModel.BatchConflictDecision();
        int prompts = 0;
        Func<string, Task<FileConflictResolution>> confirm = Always(
            FileConflictResolution.Overwrite,
            () => prompts++
        );

        Assert.AreEqual(
            expected,
            (await FileBrowserViewModel.DecideChangedTargetAsync(policy, "/r/f", confirm, decision)).ToString());
        Assert.AreEqual(0, prompts, "非“询问”策略不该弹窗。");
    }
}
