#nullable enable
using UnityEngine;

namespace Daro.Internal
{
    // Tracks the actual native image/video slot independently of the CTA.
    [AddComponentMenu("")]
    internal sealed class DaroNativeMediaDriver : MonoBehaviour
    {
        private DaroNativeAd? _ad;
        internal RectTransform? MediaArea { get; private set; }
        private readonly Vector3[] _corners = new Vector3[4];
        private Rect _lastRect;
        private bool _lastVisible;
        private bool _lastTouchEnabled;
        private Vector2Int _lastScreen;
        private bool _synced;

        internal static DaroNativeMediaDriver Attach(DaroNativeAd ad, RectTransform area)
        {
            var driver = area.GetComponent<DaroNativeMediaDriver>();
            if (driver == null) driver = area.gameObject.AddComponent<DaroNativeMediaDriver>();
            if (driver._ad != null && driver._ad != ad) driver._ad.UnwireMedia();
            driver.hideFlags = HideFlags.HideInInspector | HideFlags.DontSaveInEditor;
            driver._ad = ad;
            driver.MediaArea = area;
            driver._synced = false;
            driver.enabled = true;
            return driver;
        }

        internal void InvalidateSync() => _synced = false;

        internal void Detach()
        {
            _ad?.ClearMediaScreenRect();
            _ad = null;
            MediaArea = null;
            enabled = false; // Reusable when Unbind/Bind occurs within one frame.
        }

        private void LateUpdate()
        {
            if (_ad == null || _ad.IsDisposed || MediaArea == null) return;
            var canvas = MediaArea.GetComponentInParent<Canvas>();
            var camera = canvas != null && canvas.renderMode != RenderMode.ScreenSpaceOverlay
                ? canvas.worldCamera : null;
            MediaArea.GetWorldCorners(_corners);
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
            bool visible = _ad.UsesNativeMedia && _ad.IsReady && _ad.IsSlotViewActive &&
                _ad.Info?.AssetTypes.Contains(NativeAdAssetType.Media) == true &&
                canvas != null && canvas.isActiveAndEnabled && canvas.renderMode != RenderMode.WorldSpace &&
                IsVisible(MediaArea) && rect.width > 0 && rect.height > 0 &&
                rect.Overlaps(new Rect(0, 0, screen.x, screen.y));
            bool touchEnabled = visible && DaroNativeCtaDriver.CanReceiveRaycasts(MediaArea.gameObject);
            if (_synced && rect == _lastRect && visible == _lastVisible &&
                touchEnabled == _lastTouchEnabled && screen == _lastScreen) return;
            _ad.SetMediaScreenRect(rect, visible, touchEnabled);
            _lastRect = rect;
            _lastVisible = visible;
            _lastTouchEnabled = touchEnabled;
            _lastScreen = screen;
            _synced = true;
        }

        private static bool IsVisible(Transform area) =>
            area.gameObject.activeInHierarchy &&
            DaroNativeCtaDriver.AreCanvasGroupsVisible(area.gameObject);

        private void OnDisable()
        {
            _ad?.ClearMediaScreenRect();
            _synced = false;
        }

        private void OnDestroy() => _ad?.ClearMediaScreenRect();
    }
}
