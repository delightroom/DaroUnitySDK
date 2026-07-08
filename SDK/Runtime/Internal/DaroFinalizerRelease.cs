#nullable enable

using System;
using UnityEngine;

namespace Daro.Internal
{
    /// <summary>
    /// Finalizer-safe release scheduling for native resources.
    /// </summary>
    internal static class DaroFinalizerRelease
    {
        internal static void EnsureMainThreadReleaseTarget()
        {
            if (!Application.isPlaying) return;
            MainThreadDispatcher.EnsureCreated();
        }

        internal static void RunPlatformRelease(bool disposing, Action<IDaroPlatform> release)
        {
            if (release == null) throw new ArgumentNullException(nameof(release));

            if (disposing || MainThreadDispatcher.IsMainThread())
            {
                release(DaroPlatform.Current);
                return;
            }

            if (!DaroPlatform.TryGetCurrent(out var platform)) return;
            MainThreadDispatcher.Enqueue(() => release(platform));
        }

        internal static void RunRelease(bool disposing, Action release)
        {
            if (release == null) throw new ArgumentNullException(nameof(release));

            if (disposing || MainThreadDispatcher.IsMainThread())
            {
                release();
                return;
            }

            MainThreadDispatcher.Enqueue(release);
        }
    }
}
