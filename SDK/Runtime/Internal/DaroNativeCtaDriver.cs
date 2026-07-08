#nullable enable

using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;

namespace Daro.Internal
{
    /// <summary>
    /// Per-Button MonoBehaviour attached to a publisher CTA Button by
    /// <see cref="Daro.DaroNativeAd.WireCtaButton"/>. Each LateUpdate it
    /// computes the Button's screen-space rect + a composite
    /// touch-enabled signal, hashing both into an integer-quantized 5-tuple;
    /// PInvoke fires through <c>INativeAdHandle.SetCtaScreenRect</c> only
    /// when the hash changes.
    /// </summary>
    /// <remarks>
    /// <para><b>Composite touchEnabled</b> = <c>ad.IsReady &amp;&amp; IsInteractable() &amp;&amp;
    /// CanReceiveRaycasts(go) &amp;&amp; activeInHierarchy &amp;&amp;
    /// isActiveAndEnabled &amp;&amp; ad.IsSlotViewActive</c>.
    /// <see cref="Selectable.IsInteractable"/> walks ancestor CanvasGroup
    /// <c>interactable</c> chain but NOT <c>blocksRaycasts</c> —
    /// <see cref="CanReceiveRaycasts"/> covers the missing axis.</para>
    ///
    /// <para><b>Lifecycle</b>:
    /// <list type="bullet">
    ///   <item><see cref="Attach"/> — factory. Idempotent on the same
    ///   Button (returns existing driver via <c>GetComponent</c>).
    ///   <c>[DisallowMultipleComponent]</c> guards against accidental
    ///   double-add.</item>
    ///   <item><see cref="LateUpdate"/> — sync tick.</item>
    ///   <item><see cref="OnDisable"/> — driver GameObject or ancestor went
    ///   inactive. Pushes DISABLE_HOST (last known rect + touchEnabled=false)
    ///   so the overlay touch gate closes immediately; the frame is kept
    ///   intact for viewability-frame stability across refresh cycles.</item>
    ///   <item><see cref="OnDestroy"/> — Button GameObject destroyed by
    ///   publisher. Calls <c>ClearCtaScreenRect</c> on a still-live ad
    ///   so the shim drops the overlay.</item>
    ///   <item><see cref="Detach"/> — explicit teardown from
    ///   <c>DaroNativeAd.UnwireCta</c> / <c>Dispose</c>. ClearCtaScreenRect
    ///   on live ad + <c>Object.Destroy(this)</c>.</item>
    /// </list></para>
    ///
    /// <para>Internal only — never publish surface. Attribute set hides
    /// the component from Inspector and prevents serialization into scene
    /// assets.</para>
    /// </remarks>
    [DisallowMultipleComponent]
    [AddComponentMenu("")]   // hide from Component menu
    internal sealed class DaroNativeCtaDriver : MonoBehaviour
    {
        private Daro.DaroNativeAd?    _ad;
        private Button?               _button;
        private Canvas?               _rootCanvas;
        private Camera?               _uiCamera;       // null = ScreenSpaceOverlay
        private int                   _lastHash;
        private bool                  _hasLastHash;
        private Rect                  _lastRect;

        // Reused per-driver scratch (no per-frame alloc). GetWorldCorners
        // writes 4 elements; main-thread only, no contention.
        private readonly Vector3[] _cornerScratch = new Vector3[4];

        // Reused CanvasGroup list scratch — `GetComponents<T>(List<T>)` reuses
        // the buffer instead of allocating per call.
        private static readonly List<CanvasGroup> s_canvasGroupScratch = new(2);

        /// <summary>The Button this driver is wired to (returns null after
        /// <see cref="Detach"/>). Read by <c>DaroNativeAd.WireCtaButton</c>
        /// for idempotence (same-Button check).</summary>
        internal Button? Button => _button;

        /// <summary>
        /// Force the next active <see cref="LateUpdate"/> to re-send geometry even
        /// when the Button rect did not change. Native shims rebuild overlay hosts
        /// on each load, so a same-instance re-load needs a fresh sync.
        /// </summary>
        internal void InvalidateSync() => _hasLastHash = false;

        /// <summary>
        /// Factory — attach driver to <paramref name="button"/>'s GameObject.
        /// Returns the attached instance, or an existing one if the GameObject
        /// already has a driver (idempotent on re-wire of the same Button).
        /// Updates the driver's ad / canvas refs each call so re-wire with a
        /// different ad updates state without recreating the component.
        /// </summary>
        internal static DaroNativeCtaDriver Attach(Daro.DaroNativeAd ad, Button button)
        {
            // Unity fake-null check — `GetComponent` may return a
            // destroy-pending driver from a recent Detach (deferred to
            // end-of-frame). Treat fake-null as missing.
            var driver = button.gameObject.GetComponent<DaroNativeCtaDriver>();
            if (driver == null)
            {
                driver = button.gameObject.AddComponent<DaroNativeCtaDriver>();
                driver.hideFlags = HideFlags.HideInInspector | HideFlags.DontSaveInEditor;
            }
            driver._ad          = ad;
            driver._button      = button;
            driver._rootCanvas  = button.GetComponentInParent<Canvas>();
            driver._uiCamera    = ResolveUICamera(driver._rootCanvas);
            driver._hasLastHash = false;   // force initial sync next LateUpdate
            return driver;
        }

        /// <summary>
        /// Explicit teardown — call from <c>DaroNativeAd.UnwireCta</c> /
        /// <c>Dispose</c>. PInvokes <c>ClearCtaScreenRect</c> through the
        /// still-live ad's handle THEN destroys this component. Idempotent.
        /// </summary>
        internal void Detach()
        {
            if (_ad != null && !_ad.IsDisposed)
            {
                _ad.ClearCtaScreenRect();
            }
            _ad     = null;
            _button = null;
            // Unity's Destroy is deferred to end-of-frame; safe to call mid-update.
            if (this != null) UnityEngine.Object.Destroy(this);
        }

        // ── per-frame sync ─────────────────────────────────────────────

        private void LateUpdate()
        {
            if (_ad == null || _button == null || _ad.IsDisposed)
            {
                // Owning ad is gone — self-destruct. No PInvoke (handle gone).
                UnityEngine.Object.Destroy(this);
                return;
            }

            bool touchEnabled =
                _ad.IsReady &&
                _button.IsInteractable() &&
                CanReceiveRaycasts(_button.gameObject) &&
                _button.gameObject.activeInHierarchy &&
                _button.isActiveAndEnabled &&
                _ad.IsSlotViewActive;

            // uGUI invariant: Selectable subclasses require RectTransform.
            Rect rect = ComputeScreenRect((RectTransform)_button.transform, _uiCamera);

            int hash = ComputeHash(rect, touchEnabled);
            if (_hasLastHash && hash == _lastHash) return;
            _lastHash    = hash;
            _hasLastHash = true;
            _lastRect    = rect;

            _ad.SetCtaScreenRect(rect, touchEnabled);
        }

        private void OnDisable()
        {
            // Driver GameObject (or an ancestor) went inactive. Push
            // DISABLE_HOST using the last known rect so the overlay's touch
            // gate closes immediately. The frame is kept intact to preserve
            // MAX viewability-frame stability across refresh cycles. Force
            // re-sync on next active LateUpdate by clearing the hash.
            if (_ad != null && !_ad.IsDisposed)
            {
                try { _ad.SetCtaScreenRect(_lastRect, touchEnabled: false); }
                catch (Exception e)
                {
                    DaroLog.Warn("Native",
                        $"DaroNativeCtaDriver.OnDisable: SetCtaScreenRect(false) threw: {e}");
                }
            }
            _hasLastHash = false;
        }

        private void OnDestroy()
        {
            // Button GameObject was destroyed (or Detach destroyed us). If
            // the ad is still live, clear its overlay — accidental-click
            // guard.
            if (_ad != null && !_ad.IsDisposed)
            {
                try { _ad.ClearCtaScreenRect(); }
                catch (Exception e)
                {
                    DaroLog.Warn("Native",
                        $"DaroNativeCtaDriver.OnDestroy: ClearCtaScreenRect threw: {e}");
                }
            }
            _ad     = null;
            _button = null;
        }

        // ── helpers ────────────────────────────────────────────────────

        /// <summary>
        /// CanvasGroup chain walk. <see cref="Selectable.IsInteractable"/>
        /// covers ancestor CanvasGroup <c>interactable</c> via its
        /// <c>m_GroupsAllowInteraction</c>, but NOT <c>blocksRaycasts</c>.
        /// This helper closes the gap: walk ancestors, on each gather all
        /// <see cref="CanvasGroup"/> components, and reject if any has
        /// <c>blocksRaycasts=false</c> or <c>interactable=false</c>. Stops
        /// at <c>ignoreParentGroups=true</c> (Unity's documented semantic).
        /// </summary>
        internal static bool CanReceiveRaycasts(GameObject go)
        {
            if (go == null) return false;
            var t = go.transform;
            while (t != null)
            {
                t.GetComponents(s_canvasGroupScratch);
                bool shouldBreak = false;
                for (int i = 0; i < s_canvasGroupScratch.Count; i++)
                {
                    var g = s_canvasGroupScratch[i];
                    if (!g.blocksRaycasts) return false;
                    if (!g.interactable)   return false;   // belt + suspenders
                    if (g.ignoreParentGroups) shouldBreak = true;
                }
                if (shouldBreak) break;
                t = t.parent;
            }
            return true;
        }

        private Rect ComputeScreenRect(RectTransform rt, Camera? camera)
        {
            rt.GetWorldCorners(_cornerScratch);
            float minX = float.PositiveInfinity, minY = float.PositiveInfinity;
            float maxX = float.NegativeInfinity, maxY = float.NegativeInfinity;
            for (int i = 0; i < 4; i++)
            {
                Vector2 sp = RectTransformUtility.WorldToScreenPoint(camera, _cornerScratch[i]);
                if (sp.x < minX) minX = sp.x;
                if (sp.y < minY) minY = sp.y;
                if (sp.x > maxX) maxX = sp.x;
                if (sp.y > maxY) maxY = sp.y;
            }
            return new Rect(minX, minY, maxX - minX, maxY - minY);
        }

        private static Camera? ResolveUICamera(Canvas? canvas)
        {
            if (canvas == null) return null;
            if (canvas.renderMode == RenderMode.ScreenSpaceOverlay) return null;
            if (canvas.renderMode == RenderMode.ScreenSpaceCamera)  return canvas.worldCamera;
            // WorldSpace rejected at WireCtaButton time (NotSupportedException);
            // defensive: treat as overlay if we somehow reach here.
            return null;
        }

        // Hash 5-tuple → int. Quantize floats to whole pixels (sub-pixel
        // jitter doesn't need a PInvoke). touchEnabled in low bit.
        internal static int ComputeHash(Rect r, bool touchEnabled)
        {
            unchecked
            {
                int h = Mathf.RoundToInt(r.x);
                h = (h * 397) ^ Mathf.RoundToInt(r.y);
                h = (h * 397) ^ Mathf.RoundToInt(r.width);
                h = (h * 397) ^ Mathf.RoundToInt(r.height);
                h = (h * 397) ^ (touchEnabled ? 1 : 0);
                return h;
            }
        }
    }
}
