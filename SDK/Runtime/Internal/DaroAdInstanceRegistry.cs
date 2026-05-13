#nullable enable

using System;
using System.Collections.Concurrent;

namespace Daro.Internal
{
    /// <summary>
    /// Thread-safe map from <c>(format, adUnitId)</c> to the live
    /// ad instance, used by the platform event plumbing in
    /// <c>DaroSdk.InitializeAsync</c> to route native callbacks to
    /// the correct ad object. See docs/overview.md and docs/features/native-bridge.md.
    /// </summary>
    /// <remarks>
    /// <para>Instances are stored as <see cref="WeakReference"/> so a
    /// consumer that drops the reference without calling
    /// <see cref="IDisposable.Dispose"/> doesn't keep the ad alive
    /// indefinitely. If a native callback arrives for a GC'd ad,
    /// <see cref="Find{T}"/> returns <c>null</c> and the callback
    /// becomes a silent no-op — which is correct behavior.</para>
    ///
    /// <para>The key is <c>(DaroAdFormat, string)</c>: two ad formats
    /// may share an adUnitId in principle (though this is rare),
    /// and the event-routing side can't rely on "one adUnitId = one
    /// instance across all formats".</para>
    ///
    /// <para><b>Instance replacement</b> (§2.4 rule): constructing a
    /// second instance with the same <c>(format, adUnitId)</c>
    /// replaces the prior registration. The prior instance's native
    /// handle is destroyed by the platform layer in its own
    /// <c>Create*</c> call; the registry simply overwrites the
    /// mapping here.</para>
    ///
    /// <para>For the Editor mock, all callbacks arrive on the
    /// main thread; native callbacks may
    /// originate on worker threads. The <see cref="ConcurrentDictionary{TKey,TValue}"/>
    /// plus the <see cref="WeakReference"/> wrapper cover both cases.</para>
    /// </remarks>
    internal static class DaroAdInstanceRegistry
    {
        private static readonly ConcurrentDictionary<(DaroAdFormat format, string adUnitId), WeakReference> _map
            = new ConcurrentDictionary<(DaroAdFormat, string), WeakReference>();

        /// <summary>
        /// Register (or replace) an instance under <paramref name="format"/>
        /// + <paramref name="adUnitId"/>. Last writer wins — matches §2.4's
        /// duplicate-construction-replaces rule.
        /// </summary>
        internal static void Register(DaroAdFormat format, string adUnitId, object instance)
        {
            if (adUnitId == null) throw new ArgumentNullException(nameof(adUnitId));
            if (instance == null) throw new ArgumentNullException(nameof(instance));

            _map[(format, adUnitId)] = new WeakReference(instance);
        }

        /// <summary>
        /// Look up a registered instance. Returns <c>null</c> if the key
        /// is not registered, the stored instance has been garbage-collected,
        /// or the registered instance isn't of type <typeparamref name="T"/>.
        /// </summary>
        internal static T? Find<T>(DaroAdFormat format, string adUnitId) where T : class
        {
            if (adUnitId == null) return null;
            if (!_map.TryGetValue((format, adUnitId), out var weak)) return null;

            // WeakReference.Target returns null once the GC has collected
            // the referenced object — exactly the "consumer forgot to Dispose
            // and dropped the reference" case. Callers no-op on null.
            return weak.Target as T;
        }

        /// <summary>
        /// Remove the registration for <paramref name="format"/> +
        /// <paramref name="adUnitId"/> if the stored WeakReference still
        /// points at <paramref name="instance"/>. A different instance
        /// (e.g. after a replace) must not be clobbered.
        /// </summary>
        internal static void Unregister(DaroAdFormat format, string adUnitId, object instance)
        {
            if (adUnitId == null || instance == null) return;
            if (!_map.TryGetValue((format, adUnitId), out var weak)) return;

            // Only remove when the stored reference is still this instance.
            // If the slot has already been replaced, leave the replacement alone.
            if (ReferenceEquals(weak.Target, instance))
            {
                // TryRemove with the exact KVP to avoid a TOCTOU clobber.
                ((System.Collections.Generic.ICollection<
                    System.Collections.Generic.KeyValuePair<
                        (DaroAdFormat, string), WeakReference>>)_map)
                    .Remove(new System.Collections.Generic.KeyValuePair<
                        (DaroAdFormat, string), WeakReference>(
                            (format, adUnitId), weak));
            }
        }

        /// <summary>
        /// Clear all registrations. Called by
        /// <c>DaroRuntimeBoot.Reset</c> on play-mode enter / build startup
        /// (§6.4).
        /// </summary>
        internal static void ResetStatics()
        {
            _map.Clear();
        }
    }
}
