//
//  DaroUnityLightPopup.h
//  Extern C declarations for DaroUnityLightPopup.mm.
//  Unity build system requires a header alongside each iOS native plugin .mm
//  for plugin discovery. NOT a public include — paired with .mm only.
//
//  Sketch: docs/dev/light-popup-ios/sketch-light-popup-ios.md §"DaroUnityLightPopup.h"
//
#pragma once

#ifdef __cplusplus
extern "C" {
#endif

void DaroUnity_CreateLightPopup(
    const char* adUnitId,
    const char* placement,
    float bgR,        float bgG,        float bgB,        float bgA,
    float containerR, float containerG, float containerB, float containerA,
    float adMarkTextR,float adMarkTextG,float adMarkTextB,float adMarkTextA,
    float adMarkBgR,  float adMarkBgG,  float adMarkBgB,  float adMarkBgA,
    float closeBtnR,  float closeBtnG,  float closeBtnB,  float closeBtnA,
    float titleR,     float titleG,     float titleB,     float titleA,
    float bodyR,      float bodyG,      float bodyB,      float bodyA,
    float ctaBgR,     float ctaBgG,     float ctaBgB,     float ctaBgA,
    float ctaTextR,   float ctaTextG,   float ctaTextB,   float ctaTextA,
    const char* closeButtonText);

void DaroUnity_LoadLightPopup(const char* adUnitId);
bool DaroUnity_IsLightPopupReady(const char* adUnitId);
void DaroUnity_ShowLightPopup(const char* adUnitId);
void DaroUnity_DestroyLightPopup(const char* adUnitId);

#ifdef __cplusplus
}
#endif
