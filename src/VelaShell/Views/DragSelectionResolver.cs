namespace VelaShell.Views;

/// <summary>Resolves the entries represented by a drag without depending on either pane's item type.</summary>
internal static class DragSelectionResolver
{
    public static void SynchronizeSelection<T>(
        IList<T> selection,
        IReadOnlyList<T> dragItems,
        IEqualityComparer<T>? comparer = null)
    {
        for (int i = selection.Count - 1; i >= 0; i--)
        {
            if (!dragItems.Contains(selection[i], comparer))
            {
                selection.RemoveAt(i);
            }
        }

        foreach (T item in dragItems)
        {
            if (!selection.Contains(item, comparer))
            {
                selection.Add(item);
            }
        }
    }

    public static IReadOnlyList<T> ResolveAtDragStart<T>(
        IEnumerable<T> selectionAtPress,
        IEnumerable<T> currentSelection,
        T source,
        Func<T, bool> isParent,
        bool usePressSnapshot = true,
        IEqualityComparer<T>? comparer = null)
    {
        var pressedItems = selectionAtPress
            .Where(item => !isParent(item))
            .ToList();
        IEnumerable<T> selection = usePressSnapshot && pressedItems.Contains(source, comparer)
            ? pressedItems
            : currentSelection;
        return Resolve(selection, source, isParent, comparer);
    }

    public static IReadOnlyList<T> Resolve<T>(
        IEnumerable<T> selected,
        T source,
        Func<T, bool> isParent,
        IEqualityComparer<T>? comparer = null)
    {
        var selectedItems = selected
            .Where(item => !isParent(item))
            .ToList();

        if (selectedItems.Contains(source, comparer))
        {
            return selectedItems;
        }

        return isParent(source) ? [] : [source];
    }
}
