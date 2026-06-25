#nullable enable

using System;
using System.Collections.Concurrent;
using System.Threading;

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
    /// replaces the prior registration. The registry serializes create /
    /// destroy ownership per key so a stale finalizer cannot destroy a
    /// newer native handle.</para>
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

        private static readonly ConcurrentDictionary<(DaroAdFormat format, string adUnitId), long> _generations
            = new ConcurrentDictionary<(DaroAdFormat, string), long>();

        private static readonly ConcurrentDictionary<(DaroAdFormat format, string adUnitId), object> _keyLocks
            = new ConcurrentDictionary<(DaroAdFormat, string), object>();

        private static long _nextGeneration;

        // Teardown gate. Set by MarkShuttingDown on app-quit / Unity-runtime
        // teardown. Extends the existing "Find returns null → silent no-op"
        // contract — a callback arriving after MarkShuttingDown sees a null
        // ad and the public event never fires. See
        // docs/dev/native-object-lifecycle-cleanup/tasks/teardown-contract.md
        // §Cross-platform managed contract §1 (D-gate-c).
        //
        // volatile so a write on the main thread (OnApplicationQuit) is
        // immediately visible to Find calls on any thread. Reset to false
        // by ResetStatics so play-mode re-entry starts clean.
        private static volatile bool _isShuttingDown;

        private static object Gate((DaroAdFormat format, string adUnitId) key) =>
            _keyLocks.GetOrAdd(key, _ => new object());

        /// <summary>
        /// Reserve a new ownership token for <paramref name="format"/> +
        /// <paramref name="adUnitId"/> before platform Create replaces the
        /// previous native handle. This invalidates stale instances even if their
        /// short weak reference has already been cleared before finalization.
        /// </summary>
        internal static long ReserveGeneration(DaroAdFormat format, string adUnitId)
        {
            if (adUnitId == null) throw new ArgumentNullException(nameof(adUnitId));

            var key = (format, adUnitId);
            lock (Gate(key))
            {
                var generation = Interlocked.Increment(ref _nextGeneration);
                _generations[key] = generation;
                _map.TryRemove(key, out _);
                return generation;
            }
        }

        /// <summary>
        /// Register a constructed instance under a reserved generation. Last
        /// writer wins — matches §2.4's duplicate-construction-replaces rule.
        /// </summary>
        internal static void Register(DaroAdFormat format, string adUnitId, object instance, long generation)
        {
            if (adUnitId == null) throw new ArgumentNullException(nameof(adUnitId));
            if (instance == null) throw new ArgumentNullException(nameof(instance));

            var key = (format, adUnitId);
            lock (Gate(key))
            {
                _generations[key] = generation;
                _map[key] = new WeakReference(instance);
            }
        }

        /// <summary>
        /// Atomically replace the current owner, create the platform handle, and
        /// publish the new instance. If platform create throws, the prior owner
        /// is restored so constructor failure does not orphan the old handle.
        /// </summary>
        internal static long CreateAndRegister(
            DaroAdFormat format,
            string adUnitId,
            object instance,
            Action createPlatformHandle)
        {
            if (adUnitId == null) throw new ArgumentNullException(nameof(adUnitId));
            if (instance == null) throw new ArgumentNullException(nameof(instance));
            if (createPlatformHandle == null) throw new ArgumentNullException(nameof(createPlatformHandle));

            var key = (format, adUnitId);
            lock (Gate(key))
            {
                var hadGeneration = _generations.TryGetValue(key, out var previousGeneration);
                var hadWeak = _map.TryGetValue(key, out var previousWeak);

                var generation = Interlocked.Increment(ref _nextGeneration);
                _generations[key] = generation;
                _map.TryRemove(key, out _);

                try
                {
                    createPlatformHandle();
                    _map[key] = new WeakReference(instance);
                    return generation;
                }
                catch
                {
                    if (hadGeneration) _generations[key] = previousGeneration;
                    else _generations.TryRemove(key, out _);

                    if (hadWeak && previousWeak != null) _map[key] = previousWeak;
                    else _map.TryRemove(key, out _);

                    throw;
                }
            }
        }

        /// <summary>
        /// True while <paramref name="generation"/> is still the current owner
        /// for <paramref name="format"/> + <paramref name="adUnitId"/>.
        /// </summary>
        internal static bool IsCurrentGeneration(DaroAdFormat format, string adUnitId, long generation)
        {
            if (adUnitId == null || generation == 0) return false;
            return _generations.TryGetValue((format, adUnitId), out var current)
                && current == generation;
        }

        /// <summary>
        /// Look up a registered instance. Returns <c>null</c> if the key
        /// is not registered, the stored instance has been garbage-collected,
        /// or the registered instance isn't of type <typeparamref name="T"/>.
        /// </summary>
        internal static T? Find<T>(DaroAdFormat format, string adUnitId) where T : class
        {
            // Teardown gate: once SDK is shutting down, no callback should
            // reach a public event. Returning null here makes the existing
            // forwarder no-op path catch it.
            if (_isShuttingDown) return null;
            if (adUnitId == null) return null;
            if (!_map.TryGetValue((format, adUnitId), out var weak)) return null;

            // WeakReference.Target returns null once the GC has collected
            // the referenced object — exactly the "consumer forgot to Dispose
            // and dropped the reference" case. Callers no-op on null.
            return weak.Target as T;
        }

        /// <summary>
        /// Arm the teardown gate. After this call, <see cref="Find{T}"/>
        /// returns null for every key — silent no-op at the forwarder layer.
        /// Idempotent.
        /// </summary>
        /// <remarks>
        /// Called by <c>DaroSdk.MarkShuttingDown</c> on app-quit / Unity
        /// runtime teardown. Reset to false by <see cref="ResetStatics"/>
        /// on next play-mode enter / build startup so a fresh session does
        /// not inherit the gate.
        /// </remarks>
        internal static void MarkShuttingDown()
        {
            _isShuttingDown = true;
        }

        /// <summary>
        /// Read the teardown gate. Exposed for callback paths that bypass
        /// <see cref="Find{T}"/> — currently <see cref="DaroNativeAd"/>'s
        /// sink-routed Fire methods (CD-8 per-instance handle pattern does
        /// not register with this dict, so the Find gate cannot see those
        /// callbacks). Other format Fire paths come through the platform
        /// forwarder which already gates via <see cref="Find{T}"/>; they
        /// don't need this getter.
        /// </summary>
        internal static bool IsShuttingDown => _isShuttingDown;

        /// <summary>
        /// Remove the registration for <paramref name="format"/> +
        /// <paramref name="adUnitId"/> if the stored WeakReference still
        /// points at <paramref name="instance"/>. A different instance
        /// (e.g. after a replace) must not be clobbered.
        /// </summary>
        internal static void Unregister(DaroAdFormat format, string adUnitId, object instance, long generation)
        {
            if (adUnitId == null || instance == null) return;
            var key = (format, adUnitId);

            lock (Gate(key))
            {
                RemoveRegistrationNoLock(key, instance, generation);
            }
        }

        /// <summary>
        /// If <paramref name="generation"/> still owns the key, remove that
        /// owner and invoke <paramref name="destroyPlatformHandle"/> while the
        /// key lock is held. This closes the check-then-destroy race with a
        /// same-adUnit replacement constructor.
        /// </summary>
        internal static bool ReleasePlatformHandleIfCurrent(
            DaroAdFormat format,
            string adUnitId,
            object instance,
            long generation,
            Action destroyPlatformHandle)
        {
            if (adUnitId == null || instance == null || generation == 0) return false;
            if (destroyPlatformHandle == null) throw new ArgumentNullException(nameof(destroyPlatformHandle));

            var key = (format, adUnitId);
            lock (Gate(key))
            {
                if (!_generations.TryGetValue(key, out var current) || current != generation)
                    return false;

                RemoveRegistrationNoLock(key, instance, generation);
                destroyPlatformHandle();
                return true;
            }
        }

        private static void RemoveRegistrationNoLock(
            (DaroAdFormat format, string adUnitId) key,
            object instance,
            long generation)
        {
            var ownsGeneration = _generations.TryGetValue(key, out var current)
                && current == generation;

            // Only remove when the stored reference is still this instance or
            // this generation still owns the key. The generation check matters
            // on finalizer paths: short WeakReference.Target can already be
            // null by the time the finalizer runs.
            if (_map.TryGetValue(key, out var weak) && ReferenceEquals(weak.Target, instance))
            {
                // TryRemove with the exact KVP to avoid a TOCTOU clobber.
                ((System.Collections.Generic.ICollection<
                    System.Collections.Generic.KeyValuePair<
                        (DaroAdFormat, string), WeakReference>>)_map)
                    .Remove(new System.Collections.Generic.KeyValuePair<
                        (DaroAdFormat, string), WeakReference>(key, weak));
            }
            else if (ownsGeneration && weak != null)
            {
                ((System.Collections.Generic.ICollection<
                    System.Collections.Generic.KeyValuePair<
                        (DaroAdFormat, string), WeakReference>>)_map)
                    .Remove(new System.Collections.Generic.KeyValuePair<
                        (DaroAdFormat, string), WeakReference>(key, weak));
            }

            if (ownsGeneration)
            {
                ((System.Collections.Generic.ICollection<
                    System.Collections.Generic.KeyValuePair<
                        (DaroAdFormat, string), long>>)_generations)
                    .Remove(new System.Collections.Generic.KeyValuePair<
                        (DaroAdFormat, string), long>(key, current));
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
            _generations.Clear();
            // Reset teardown gate so play-mode re-entry behaves as if no
            // prior session ever shut down (mirrors MainThreadDispatcher
            // pattern at MainThreadDispatcher.cs:230).
            _isShuttingDown = false;
        }
    }
}
