#nullable enable
using UnityEngine;

namespace Daro.Internal
{
    // Tracks the full ad area independently of the CTA's interactability.
    [AddComponentMenu("")]
    internal sealed class DaroNativeAdChoicesDriver : MonoBehaviour
    {
        private DaroNativeAd? _ad;
        internal RectTransform? AdArea { get; private set; }
        private readonly Vector3[] _corners = new Vector3[4];
        private Rect _lastRect;
        private bool _lastVisible;
        private Vector2Int _lastScreen;
        private bool _synced;

        internal static DaroNativeAdChoicesDriver Attach(DaroNativeAd ad, RectTransform area)
        {
            var driver = area.GetComponent<DaroNativeAdChoicesDriver>();
            if (driver == null) driver = area.gameObject.AddComponent<DaroNativeAdChoicesDriver>();
            if (driver._ad != null && driver._ad != ad) driver._ad.UnwireAdChoices();
            driver.hideFlags = HideFlags.HideInInspector | HideFlags.DontSaveInEditor;
            driver._ad = ad;
            driver.AdArea = area;
            driver._synced = false;
            driver.enabled = true;
            return driver;
        }

        internal void InvalidateSync() => _synced = false;

        internal void Detach()
        {
            _ad?.ClearAdChoicesScreenRect();
            _ad = null;
            AdArea = null;
            enabled = false; // Reusable when Unbind/Bind occurs within one frame.
        }

        private void LateUpdate()
        {
            if (_ad == null || _ad.IsDisposed || AdArea == null) return;
            var canvas = AdArea.GetComponentInParent<Canvas>();
            var camera = canvas != null && canvas.renderMode != RenderMode.ScreenSpaceOverlay
                ? canvas.worldCamera : null;
            AdArea.GetWorldCorners(_corners);
            var min = new Vector2(float.PositiveInfinity, float.PositiveInfinity);
            var max = new Vector2(float.NegativeInfinity, float.NegativeInfinity);
            foreach (var corner in _corners)
            {
                var point = RectTransformUtility.WorldToScreenPoint(camera, corner);
                min = Vector2.Min(min, point);
                max = Vector2.Max(max, point);
            }
            var rect = Rect.MinMaxRect(min.x, min.y, max.x, max.y);
            var screen = new Vector2Int(Screen.width, Screen.height);
            bool visible = _ad.IsReady && _ad.IsSlotViewActive &&
                canvas != null && canvas.renderMode != RenderMode.WorldSpace &&
                IsVisible(AdArea) && rect.width > 0 && rect.height > 0 &&
                rect.Overlaps(new Rect(0, 0, screen.x, screen.y));
            if (_synced && rect == _lastRect && visible == _lastVisible && screen == _lastScreen) return;
            _ad.SetAdChoicesScreenRect(rect, visible);
            _lastRect = rect;
            _lastVisible = visible;
            _lastScreen = screen;
            _synced = true;
        }

        private static bool IsVisible(Transform area)
        {
            for (var t = area; t != null; t = t.parent)
            {
                if (!t.gameObject.activeInHierarchy) return false;
                var canvas = t.GetComponent<Canvas>();
                if (canvas != null && !canvas.isActiveAndEnabled) return false;
                foreach (var group in t.GetComponents<CanvasGroup>())
                    if (group.alpha <= 0) return false;
            }
            return true;
        }

        private void OnDisable()
        {
            _ad?.ClearAdChoicesScreenRect();
            _synced = false;
        }

        private void OnDestroy() => _ad?.ClearAdChoicesScreenRect();
    }
}
