#nullable enable

#if UNITY_EDITOR
using UnityEngine;

namespace Daro.Internal
{
    /// <summary>
    /// Editor-only IMGUI placeholder for banner ad mock visualization. Drawn
    /// on Game view via <c>OnGUI</c> at the anchor position the consumer set.
    /// Editor-only via <c>#if UNITY_EDITOR</c> guard so player builds don't
    /// pull in this MonoBehaviour or its IMGUI dependency.
    ///
    /// No <c>UnityEngine.UI</c> dependency — IMGUI only (per sketch §1 KU-9
    /// boundary: "must provide Editor visual feedback without introducing UI
    /// package dependency").
    /// </summary>
    internal sealed class DaroEditorBannerView : MonoBehaviour
    {
        private DaroBannerSize     _size;
        private DaroBannerPosition _position;
        private bool               _visible;
        private string             _adUnitId = "";

        public void Configure(string adUnitId, DaroBannerSize size, DaroBannerPosition position)
        {
            _adUnitId = adUnitId;
            _size     = size;
            _position = position;
        }

        public void SetPosition(DaroBannerPosition position) => _position = position;
        public void Show() => _visible = true;
        public void Hide() => _visible = false;

        private void OnGUI()
        {
            if (!_visible) return;
            var (w, h) = SizeToPixels(_size);
            var (x, y) = PositionToCoords(_position, w, h);
            GUI.Box(new Rect(x, y, w, h),
                $"DaroBannerAd (mock)\n{_adUnitId}\n{_size} @ {_position}");
        }

        private static (int w, int h) SizeToPixels(DaroBannerSize size) => size switch
        {
            DaroBannerSize.Standard => (320, 50),
            DaroBannerSize.Mrec     => (300, 250),
            _                       => (320, 50),
        };

        private static (float x, float y) PositionToCoords(
            DaroBannerPosition pos, int w, int h)
        {
            var sw = Screen.width;
            var sh = Screen.height;
            return pos switch
            {
                DaroBannerPosition.TopLeft      => (0,            0),
                DaroBannerPosition.TopCenter    => ((sw - w) / 2f, 0),
                DaroBannerPosition.TopRight     => (sw - w,        0),
                DaroBannerPosition.BottomLeft   => (0,             sh - h),
                DaroBannerPosition.BottomCenter => ((sw - w) / 2f, sh - h),
                DaroBannerPosition.BottomRight  => (sw - w,        sh - h),
                _                               => ((sw - w) / 2f, sh - h),
            };
        }
    }
}
#endif
