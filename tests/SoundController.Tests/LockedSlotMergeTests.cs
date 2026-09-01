using SoundController.Config;
using Xunit;

namespace SoundController.Tests;

/// <summary>
/// Merge semantics for locked-device slots when the settings window saves:
/// untouched combos preserve their previous lock; only user-touched combos
/// take the UI value.
/// </summary>
public class LockedSlotMergeTests
{
    [Fact]
    public void Resolve_UntouchedSlot_KeepsPreviousLock()
    {
        // The saved device was missing from the device list at load time, so
        // the combo shows "(not locked)". Saving must not wipe the lock.
        var result = LockedSlotMerge.Resolve(previous: "device-a", uiValue: null, userTouched: false);

        Assert.Equal("device-a", result);
    }

    [Fact]
    public void Resolve_TouchedSlot_TakesUiValue()
    {
        var result = LockedSlotMerge.Resolve(previous: "device-a", uiValue: "device-b", userTouched: true);

        Assert.Equal("device-b", result);
    }

    [Fact]
    public void Resolve_TouchedToNotLocked_ClearsTheLock()
    {
        // "(not locked)" is a deliberate user choice when the combo was touched.
        var result = LockedSlotMerge.Resolve(previous: "device-a", uiValue: null, userTouched: true);

        Assert.Null(result);
    }

    [Fact]
    public void Resolve_UntouchedWithNoPreviousLock_StaysUnlocked()
    {
        var result = LockedSlotMerge.Resolve(previous: null, uiValue: "device-b", userTouched: false);

        Assert.Null(result);
    }
}
