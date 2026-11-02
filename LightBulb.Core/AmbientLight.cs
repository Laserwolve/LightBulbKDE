using System;

namespace LightBulb.Core;

// Turns what a camera sees into a measure of the light in the room, the way a phone's light sensor
// reads it, and that measure into settings for the screen
public static class AmbientLight
{
    // How a camera's brightness relates to the light it caught, as the power that takes one to the
    // other. A webcam's tone curve is gentler than sRGB's 2.2 over the middle of its range, where
    // readings are taken, so this is a middle value for the cameras met so far; a reading leans on
    // it only for the small distance between a frame and the middle of the camera's range, since
    // the exposure carries the rest.
    public const double CameraGamma = 1.6;

    // The screen's light goes with its dimming, which KWin applies to light rather than to the
    // gamma-encoded brightness, so the glow the camera sees goes with the brightness to this power
    private const double DisplayGamma = 2.2;

    // One code value above black, so that a black frame still gives a finite reading
    private const double MinimumLuma = 1.0 / 255;

    // How much of a reading is kept at the least when the screen's own glow is taken out, so that
    // a glow estimate on the high side cannot black the room out
    private const double MinimumRemainingLight = 1.0 / 8;

    // The light that reached the camera, in stops: twice the light in the room adds one. The scale
    // has no fixed zero, since nothing about the camera's sensitivity is known, so a reading only
    // means something beside another from the same camera.
    public static double GetStops(double luma, TimeSpan exposure)
    {
        if (exposure <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(exposure));

        var linearLuma = Math.Pow(Math.Clamp(luma, MinimumLuma, 1), CameraGamma);
        return Math.Log2(linearLuma / exposure.TotalSeconds);
    }

    // The light the screen itself throws into the room, as the camera sees it, from the brightness
    // the screen is at: its dimming times how much the room lights up per unit of dimming
    public static double GetScreenGlow(double screenBrightness, double glowPerDimming) =>
        Math.Max(glowPerDimming, 0) * Math.Pow(Math.Clamp(screenBrightness, 0, 1), DisplayGamma);

    // A reading with the screen's own glow taken out
    public static double CompensateForScreen(
        double stops,
        double screenBrightness,
        double glowPerDimming
    )
    {
        var light = Math.Pow(2, stops);
        var remaining = Math.Max(
            light - GetScreenGlow(screenBrightness, glowPerDimming),
            light * MinimumRemainingLight
        );

        return Math.Log2(remaining);
    }

    // How much the room lights up per unit of the screen's dimming, from two readings of the same
    // room with the screen at two brightnesses; nothing when the brighter screen read no brighter
    public static double GetGlowPerDimming(
        double dimStops,
        double dimBrightness,
        double brightStops,
        double brightBrightness
    )
    {
        var dimmingDifference =
            Math.Pow(Math.Clamp(brightBrightness, 0, 1), DisplayGamma)
            - Math.Pow(Math.Clamp(dimBrightness, 0, 1), DisplayGamma);

        if (dimmingDifference <= 0)
            return 0;

        var lightDifference = Math.Pow(2, brightStops) - Math.Pow(2, dimStops);
        return Math.Max(lightDifference, 0) / dimmingDifference;
    }

    // Where the light sits between the darkest the room gets (0) and the brightest (1)
    public static double GetLevel(double stops, double darkStops, double brightStops)
    {
        // References at the same light leave only darker and brighter
        if (brightStops <= darkStops)
            return stops >= brightStops ? 1 : 0;

        return Math.Clamp((stops - darkStops) / (brightStops - darkStops), 0, 1);
    }

    // The screen's brightness for a level of light in the room: the level times the scale, so that
    // at a scale of 1 the screen is as bright as the room, held between the dark room's brightness
    // and the bright room's
    public static double GetBrightness(
        double level,
        double scale,
        double darkBrightness,
        double brightBrightness
    ) =>
        Math.Clamp(
            level * Math.Max(scale, 0),
            Math.Min(darkBrightness, brightBrightness),
            Math.Max(darkBrightness, brightBrightness)
        );
}
