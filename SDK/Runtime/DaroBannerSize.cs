#nullable enable

namespace Daro
{
    /// <summary>
    /// Banner 광고 크기. 실제 native (Daro MAX variant) 가 지원하는 두 사이즈.
    /// 값은 Kotlin shim 으로 그대로 ordinal 전달 — 변경 시 양쪽 동시 수정 필요.
    /// </summary>
    public enum DaroBannerSize
    {
        /// <summary>320×50 dp — 표준 배너. native <c>DaroBannerSize.Banner</c> 매핑.</summary>
        Standard = 0,

        /// <summary>300×250 dp — 미디엄 직사각형. native <c>DaroBannerSize.MREC</c> 매핑.</summary>
        Mrec = 1,
    }
}
