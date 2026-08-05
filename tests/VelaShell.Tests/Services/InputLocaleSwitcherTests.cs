using VelaShell.Services;

namespace VelaShell.Tests.Services;

[TestClass]
public sealed class InputLocaleSwitcherTests
{
    [TestMethod]
    public void SelectEnglish_when_loaded_english_exists_activates_it_and_returns_prior_layout()
    {
        // Given a non-English current layout and a loaded English layout.
        var native = new FakeKeyboardLayoutNative(0x0411, [0x0411, 0x0409, 0x0809]);
        var switcher = new InputLocaleSwitcher(native);

        // When selecting the English input locale.
        bool switched = switcher.TrySelectEnglish(out nint priorLayout);

        // Then the loaded English handle is activated and the exact prior handle is reported.
        Assert.IsTrue(switched);
        Assert.AreEqual(0x0411, priorLayout);
        Assert.AreSequenceEqual([0x0409], native.ActivatedLayouts);
    }

    [TestMethod]
    public void SelectEnglish_when_only_en_gb_is_loaded_activates_en_gb()
    {
        // Given a non-English current layout and a loaded en-GB layout.
        var native = new FakeKeyboardLayoutNative(0x0411, [0x0411, 0x0809]);
        var switcher = new InputLocaleSwitcher(native);

        // When selecting the English input locale.
        bool switched = switcher.TrySelectEnglish(out _);

        // Then the en-GB HKL is accepted by its English primary LANGID.
        Assert.IsTrue(switched);
        Assert.AreSequenceEqual([0x0809], native.ActivatedLayouts);
    }

    [TestMethod]
    public void SelectEnglish_when_current_layout_is_english_is_a_no_op()
    {
        // Given an already-English current layout.
        var native = new FakeKeyboardLayoutNative(0x0409, [0x0409]);
        var switcher = new InputLocaleSwitcher(native);

        // When selecting the English input locale.
        bool switched = switcher.TrySelectEnglish(out nint priorLayout);

        // Then no activation occurs and the current handle is not treated as a restoration.
        Assert.IsFalse(switched);
        Assert.AreEqual(0x0409, priorLayout);
        Assert.IsEmpty(native.ActivatedLayouts);
    }

    [TestMethod]
    public void SelectEnglish_when_no_english_layout_is_loaded_is_a_no_op()
    {
        // Given only non-English loaded layouts.
        var native = new FakeKeyboardLayoutNative(0x0411, [0x0411, 0x0804]);
        var switcher = new InputLocaleSwitcher(native);

        // When selecting the English input locale.
        bool switched = switcher.TrySelectEnglish(out _);

        // Then no activation occurs.
        Assert.IsFalse(switched);
        Assert.IsEmpty(native.ActivatedLayouts);
    }

    [TestMethod]
    public void SelectEnglish_when_activation_fails_does_not_report_a_switch()
    {
        // Given a loaded English layout whose activation fails.
        var native = new FakeKeyboardLayoutNative(0x0411, [0x0409]) { ActivationResult = nint.Zero };
        var switcher = new InputLocaleSwitcher(native);

        // When selecting the English input locale.
        bool switched = switcher.TrySelectEnglish(out _);

        // Then the failed activation is not considered active.
        Assert.IsFalse(switched);
        Assert.AreSequenceEqual([0x0409], native.ActivatedLayouts);
    }

    [TestMethod]
    public void SelectEnglish_when_layout_changes_during_activation_restores_returned_prior_layout()
    {
        // Given a snapshot of one non-English layout, followed by a different native prior-layout result.
        var native = new FakeKeyboardLayoutNative(0x0411, [0x0411, 0x0409])
        {
            ActivationResult = 0x0804,
        };
        var switcher = new InputLocaleSwitcher(native);

        // When selecting English and restoring the resulting lease.
        bool switched = switcher.TrySelectEnglish(out nint priorLayout);
        switcher.Restore(priorLayout);

        // Then the returned native prior layout, not the stale snapshot, is restored.
        Assert.IsTrue(switched);
        Assert.AreEqual(0x0804, priorLayout);
        Assert.AreSequenceEqual([0x0409, 0x0804], native.ActivatedLayouts);
    }

    [TestMethod]
    public void Restore_after_selection_activates_exact_prior_layout()
    {
        // Given a successful switch from a specific prior layout.
        var native = new FakeKeyboardLayoutNative(0x0411, [0x0411, 0x0409]);
        var switcher = new InputLocaleSwitcher(native);
        Assert.IsTrue(switcher.TrySelectEnglish(out nint priorLayout));

        // When restoring after the focus owner releases its lease.
        switcher.Restore(priorLayout);

        // Then the exact prior layout is activated.
        Assert.AreSequenceEqual([0x0409, 0x0411], native.ActivatedLayouts);
    }

    [TestMethod]
    public void Restore_when_native_activation_fails_remains_a_safe_no_op()
    {
        // Given a prior layout and a native adapter that cannot activate it.
        var native = new FakeKeyboardLayoutNative(0x0411, [0x0409]) { ActivationResult = nint.Zero };
        var switcher = new InputLocaleSwitcher(native);

        // When restoring the prior layout.
        switcher.Restore(0x0411);

        // Then the failure is contained and no exception escapes.
        Assert.AreSequenceEqual([0x0411], native.ActivatedLayouts);
    }

    private sealed class FakeKeyboardLayoutNative(nint currentLayout, IReadOnlyList<nint> loadedLayouts) : IKeyboardLayoutNative
    {
        public nint CurrentLayout { get; } = currentLayout;
        public IReadOnlyList<nint> LoadedLayouts { get; } = loadedLayouts;
        public List<nint> ActivatedLayouts { get; } = [];
        public nint ActivationResult { get; set; } = currentLayout;

        public nint GetCurrentLayout() => CurrentLayout;

        public IReadOnlyList<nint> GetLoadedLayouts() => LoadedLayouts;

        public nint ActivateLayout(nint layout)
        {
            ActivatedLayouts.Add(layout);
            return ActivationResult;
        }
    }
}
