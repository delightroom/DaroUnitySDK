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
    /// <para>Geometry is cleared while loading or while the Unity UI is hidden.
    /// Visible geometry uses <b>touchEnabled</b> = <c>isActiveAndEnabled &amp;&amp;
    /// IsInteractable() &amp;&amp; CanReceiveRaycasts(go)</c>.
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
    ///   <item><see cref="OnDisable"/> — clear geometry when the driver or
    ///   its ancestors become inactive, hiding native UI and disabling touch.</item>
    ///   <item><see cref="OnDestroy"/> — Button GameObject destroyed by
    ///   publisher. Calls <c>ClearCtaScreenRect</c> on a still-live ad
    ///   so the shim drops the overlay.</item>
    ///   <item><see cref="Detach"/> — explicit teardown from
    ///   <c>DaroNativeAd.UnwireCta</c> / <c>Dispose</c>. ClearCtaScreenRect
    ///   on live ad, then disable the reusable driver.</item>
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
        private bool                  _hasVisibleRect;

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
            // Detach disables instead of scheduling destruction, so an
            // immediate rebind can safely reuse this component.
            var driver = button.gameObject.GetComponent<DaroNativeCtaDriver>();
            if (driver == null)
            {
                driver = button.gameObject.AddComponent<DaroNativeCtaDriver>();
                driver.hideFlags = HideFlags.HideInInspector | HideFlags.DontSaveInEditor;
            }
            // Transfer ownership before reusing a Button for another ad.
            // Otherwise the previous ad's Dispose would detach this new binding.
            if (driver._ad != null && driver._ad != ad) driver._ad.UnwireCta();
            driver._ad          = ad;
            driver._button      = button;
            driver._rootCanvas  = button.GetComponentInParent<Canvas>();
            driver._uiCamera    = ResolveUICamera(driver._rootCanvas);
            driver._hasLastHash = false;   // force initial sync next LateUpdate
            driver.enabled = true;
            return driver;
        }

        /// <summary>
        /// Explicit teardown — call from <c>DaroNativeAd.UnwireCta</c> /
        /// <c>Dispose</c>. PInvokes <c>ClearCtaScreenRect</c> through the
        /// still-live ad's handle, then disable and release references. Idempotent.
        /// </summary>
        internal void Detach()
        {
            if (_ad != null && !_ad.IsDisposed)
            {
                _ad.ClearCtaScreenRect();
            }
            _ad     = null;
            _button = null;
            _rootCanvas = null;
            _uiCamera = null;
            // Destroy is deferred, and a same-frame Bind would retrieve the
            // doomed component via GetComponent. Keep an inert reusable driver.
            enabled = false;
        }

        // ── per-frame sync ─────────────────────────────────────────────

        private void LateUpdate()
        {
            if (_ad == null || _button == null || _ad.IsDisposed)
            {
                // Release references and stop syncing until the next attachment.
                Detach();
                return;
            }

            _rootCanvas = _button.GetComponentInParent<Canvas>();
            _uiCamera = ResolveUICamera(_rootCanvas);
            bool visible = _ad.IsReady && _ad.IsSlotViewActive
                && _button.gameObject.activeInHierarchy
                && _rootCanvas != null && _rootCanvas.isActiveAndEnabled
                && AreCanvasGroupsVisible(_button.gameObject);
            if (!visible)
            {
                if (!_hasLastHash || _hasVisibleRect) _ad.ClearCtaScreenRect();
                _hasLastHash = true;
                _hasVisibleRect = false;
                return;
            }

            bool touchEnabled = _button.isActiveAndEnabled && _button.IsInteractable()
                && CanReceiveRaycasts(_button.gameObject);

            // uGUI invariant: Selectable subclasses require RectTransform.
            Rect rect = ComputeScreenRect((RectTransform)_button.transform, _uiCamera);

            int hash = ComputeHash(rect, touchEnabled);
            // UIKit's Y conversion also depends on the current screen height.
            hash = unchecked((hash * 397 ^ Screen.width) * 397 ^ Screen.height);
            if (_hasLastHash && _hasVisibleRect && hash == _lastHash) return;
            _lastHash    = hash;
            _hasLastHash = true;
            _hasVisibleRect = true;

            _ad.SetCtaScreenRect(rect, touchEnabled);
        }

        private void OnDisable()
        {
            // No visible Unity anchor remains. Clear geometry rather than
            // only disabling clicks, which would leave vendor UI visible.
            if (_ad != null && !_ad.IsDisposed)
            {
                try { _ad.ClearCtaScreenRect(); }
                catch (Exception e)
                {
                    DaroLog.Warn("Native",
                        $"DaroNativeCtaDriver.OnDisable: ClearCtaScreenRect threw: {e}");
                }
            }
            _hasLastHash = false;
            _hasVisibleRect = false;
        }

        private void OnDestroy()
        {
            // Button GameObject was destroyed. If
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
            _rootCanvas = null;
            _uiCamera = null;
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

        private static bool AreCanvasGroupsVisible(GameObject go)
        {
            // Match CanvasGroup inheritance without treating a transparent
            // CTA Image (often used with visible child text) as a hidden ad.
            var t = go.transform;
            while (t != null)
            {
                t.GetComponents(s_canvasGroupScratch);
                bool ignoreParents = false;
                foreach (var group in s_canvasGroupScratch)
                {
                    if (!group.isActiveAndEnabled) continue;
                    if (group.alpha <= 0f) return false;
                    ignoreParents |= group.ignoreParentGroups;
                }
                if (ignoreParents) break;
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
