#nullable enable

using UnityEngine;

namespace Daro.Internal
{
    /// <summary>
    /// Single entry point that resets all Daro SDK static state on runtime startup
    /// (Editor play-mode enter or device build launch). See
    /// native-bridge-architecture.md §6.4.
    /// </summary>
    /// <remarks>
    /// Without this, a second Editor play session reuses the destroyed-GameObject
    /// reference from the previous play session and <c>NullReferenceException</c>s
    /// on the first native callback. <c>SubsystemRegistration</c> fires before any
    /// scene or MonoBehaviour <c>Awake</c> — a safe point to wipe C# references.
    ///
    /// <para>The attribute fires in <b>both Editor and device builds</b>, so this
    /// method must not assume Editor-only semantics. Against already-null statics
    /// (first run, device build) all reset calls are benign no-ops.</para>
    /// </remarks>
    internal static class DaroRuntimeBoot
    {
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void Reset()
        {
            // Order is not semantically significant — each ResetStatics wipes a
            // disjoint set of references — but listing in layer order (dispatcher →
            // facade → platform → notifier) reads top-down from the most-foundational
            // piece outward.
            MainThreadDispatcher.ResetStatics();
            DaroSdk.ResetStatics();
            DaroPlatform.ResetStatics();
            DaroAdInstanceRegistry.ResetStatics();
            DaroAppStateNotifier.ResetStatics();
        }
    }
}
