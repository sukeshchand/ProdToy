namespace ProdToy.Plugins.ClaudeIntegration;

/// <summary>What the window does with a notification that just arrived.</summary>
enum ArrivalAction
{
    /// <summary>Display the new item and bring the window up.</summary>
    Show,

    /// <summary>The item on screen is still New: leave it alone; only the chips, the tree and the counter change.</summary>
    Queue,

    /// <summary>No pop-up at all (snoozed, suppressed, notifications off or balloon only); the item is still New.</summary>
    Silent,
}

/// <summary>
/// The rules of the New / Read queue, with no window in them. An item is New from the moment it arrives until OK is pressed
/// while it is on screen; nothing else (the pager, a chip, the tree, Esc, closing, snoozing) marks it Read.
/// </summary>
sealed class UnreadQueue
{
    private readonly ReadStore _store;

    public UnreadQueue(ReadStore store) => _store = store;

    public ReadStore Store => _store;

    public int Count => _store.Count;

    public bool IsNew(string? itemKey) => _store.IsUnread(itemKey);

    /// <summary>
    /// A saved notification arrived: it is New, and the answer says whether the window shows it.
    /// </summary>
    /// <param name="itemKey">The new item; null when nothing was saved (history off): it is shown as before.</param>
    /// <param name="popupAllowed">False while snoozed, suppressed, with notifications off or in balloon-only mode.</param>
    /// <param name="windowShown">The window is on screen and not minimised.</param>
    /// <param name="shownKey">The item the window displays now, if any.</param>
    public ArrivalAction Arrive(string? itemKey, bool popupAllowed, bool windowShown, string? shownKey)
    {
        if (string.IsNullOrEmpty(itemKey)) return popupAllowed ? ArrivalAction.Show : ArrivalAction.Silent;

        _store.MarkUnread(itemKey);
        return Decide(popupAllowed, windowShown, shownKey != null && shownKey != itemKey && _store.IsUnread(shownKey));
    }

    /// <summary>The pop-up rules: hidden = show the new one; visible on a New item = queue; visible on a Read item = show.</summary>
    public static ArrivalAction Decide(bool popupAllowed, bool windowShown, bool shownIsNew)
    {
        if (!popupAllowed) return ArrivalAction.Silent;
        if (!windowShown) return ArrivalAction.Show;
        return shownIsNew ? ArrivalAction.Queue : ArrivalAction.Show;
    }

    /// <summary>
    /// OK on the item on screen: it becomes Read. Answers the item to show next (the newest New one), or null when nothing is
    /// left and the window closes.
    /// </summary>
    /// <param name="timeOf">An item's time; null for a key the history does not know (it is skipped).</param>
    public string? Ok(string? shownKey, Func<string, DateTime?> timeOf)
    {
        _store.MarkRead(shownKey);
        return NewestFirst(timeOf).FirstOrDefault();
    }

    public void MarkRead(string itemKey) => _store.MarkRead(itemKey);

    public void MarkAllRead() => _store.MarkAllRead();

    /// <summary>The New items the history knows, newest first: the order of the chips.</summary>
    public IReadOnlyList<string> NewestFirst(Func<string, DateTime?> timeOf)
        => _store.Unread
            .Select(k => (Key: k, Time: timeOf(k)))
            .Where(p => p.Time != null)
            .OrderByDescending(p => p.Time)
            .Select(p => p.Key)
            .ToList();
}
