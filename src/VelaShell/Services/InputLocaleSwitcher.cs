using System.Runtime.InteropServices;

namespace VelaShell.Services;

internal interface IKeyboardLayoutNative
{
    nint GetCurrentLayout();

    IReadOnlyList<nint> GetLoadedLayouts();

    nint ActivateLayout(nint layout);
}

internal interface IInputLocaleSwitcher
{
    bool TrySelectEnglish(out nint priorLayout);

    void Restore(nint priorLayout);
}

internal sealed class InputLocaleSwitcher : IInputLocaleSwitcher
{
    private const ulong EnglishPrimaryLanguage = 0x0009;
    private readonly IKeyboardLayoutNative _native;

    internal InputLocaleSwitcher(IKeyboardLayoutNative native) => _native = native;

    public bool TrySelectEnglish(out nint priorLayout)
    {
        priorLayout = _native.GetCurrentLayout();
        if (priorLayout == nint.Zero || IsEnglish(priorLayout))
        {
            return false;
        }

        nint englishLayout = _native.GetLoadedLayouts()
            .FirstOrDefault(IsEnglish);
        if (englishLayout == nint.Zero)
        {
            return false;
        }

        nint activatedPriorLayout = _native.ActivateLayout(englishLayout);
        if (activatedPriorLayout == nint.Zero)
        {
            return false;
        }

        priorLayout = activatedPriorLayout;
        return true;
    }

    public void Restore(nint priorLayout)
    {
        if (priorLayout != nint.Zero)
        {
            _ = _native.ActivateLayout(priorLayout);
        }
    }

    private static bool IsEnglish(nint layout) =>
        (unchecked((ulong)layout.ToInt64()) & 0x03ffUL) == EnglishPrimaryLanguage;
}

internal sealed partial class WindowsKeyboardLayoutNative : IKeyboardLayoutNative
{
    public nint GetCurrentLayout()
    {
        if (!OperatingSystem.IsWindows())
        {
            return nint.Zero;
        }

        try
        {
            return GetKeyboardLayout(0);
        }
        catch
        {
            // Input locale switching is noncritical; unavailable native APIs are a safe no-op.
            return nint.Zero;
        }
    }

    public IReadOnlyList<nint> GetLoadedLayouts()
    {
        if (!OperatingSystem.IsWindows())
        {
            return [];
        }

        try
        {
            int count = GetKeyboardLayoutList(0, []);
            if (count <= 0)
            {
                return [];
            }

            nint[] layouts = new nint[count];
            int actualCount = GetKeyboardLayoutList(layouts.Length, layouts.AsSpan());
            return actualCount <= 0 ? [] : layouts[..Math.Min(actualCount, layouts.Length)];
        }
        catch
        {
            // Input locale switching is noncritical; unavailable native APIs are a safe no-op.
            return [];
        }
    }

    public nint ActivateLayout(nint layout)
    {
        if (!OperatingSystem.IsWindows())
        {
            return nint.Zero;
        }

        try
        {
            return ActivateKeyboardLayout(layout, 0);
        }
        catch
        {
            // Input locale switching is noncritical; unavailable native APIs are a safe no-op.
            return nint.Zero;
        }
    }

    [LibraryImport("user32.dll")]
    private static partial nint GetKeyboardLayout(uint threadId);

    [LibraryImport("user32.dll")]
    private static partial int GetKeyboardLayoutList(int count, Span<nint> layouts);

    [LibraryImport("user32.dll")]
    private static partial nint ActivateKeyboardLayout(nint layout, uint flags);
}
