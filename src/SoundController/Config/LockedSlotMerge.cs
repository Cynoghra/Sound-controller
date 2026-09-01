namespace SoundController.Config;

/// <summary>
/// Pure decision for what to persist into one locked-device slot when the
/// settings window saves.
///
/// The settings window loads device lists at open time; a saved device that
/// is not currently available (unplugged, Sonar hiccup) leaves its combo
/// showing "(not locked)" even though a device IS locked. Saving that UI
/// state blindly would silently wipe the lock - observed in practice.
/// Untouched slots therefore keep their previous locked value; only slots
/// the user actually changed take the UI value (including a deliberate
/// "(not locked)" clear).
/// </summary>
public static class LockedSlotMerge
{
    /// <summary>
    /// Resolves the value to persist for one slot.
    /// </summary>
    /// <param name="previous">Value currently stored in settings (null = not locked).</param>
    /// <param name="uiValue">Value currently shown in the combo (null/empty = not locked).</param>
    /// <param name="userTouched">True when the user changed this combo after load.</param>
    public static string? Resolve(string? previous, string? uiValue, bool userTouched)
    {
        return userTouched ? uiValue : previous;
    }
}
