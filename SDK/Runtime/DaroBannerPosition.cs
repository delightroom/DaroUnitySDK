#nullable enable

namespace Daro
{
    /// <summary>
    /// Banner overlay 의 화면상 anchor 위치. v1 은 6-gravity preset 만 — pixel-exact
    /// <c>Custom(x, y)</c> 는 v2 OOS. Android Kotlin shim 에서
    /// <c>android.view.Gravity</c> 비트마스크로 매핑.
    /// </summary>
    public enum DaroBannerPosition
    {
        TopLeft      = 0,
        TopCenter    = 1,
        TopRight     = 2,
        BottomLeft   = 3,
        BottomCenter = 4,
        BottomRight  = 5,
    }
}
