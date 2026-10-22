using System;
using System.Diagnostics;
using System.Runtime.Versioning;
using System.Threading.Tasks;
using PowerKit;
using Tmds.DBus.Protocol;

namespace LightBulb.PlatformInterop.Kde;

internal readonly record struct NightLightState(
    bool IsEnabled,
    bool IsRunning,
    bool IsInhibited,
    bool IsConstantMode,
    int TargetTemperature
);

// KWin publishes Night Light's live state on the session bus, and PowerDevil announces every
// change of the brightness slider there.
[SupportedOSPlatform("linux")]
internal static class KdeBus
{
    private const string KWinService = "org.kde.KWin";
    private const string NightLightPath = "/org/kde/KWin/NightLight";
    private const string NightLightInterface = "org.kde.KWin.NightLight";
    private const string PropertiesInterface = "org.freedesktop.DBus.Properties";

    private const string BrightnessService = "org.kde.ScreenBrightness";
    private const string BrightnessPath = "/org/kde/ScreenBrightness";

    // Night Light's mode, as kwinrc and the D-Bus property number it: Constant is 0, DarkLight 1
    private const uint ConstantMode = 0;

    public static async Task<NightLightState?> TryGetNightLightStateAsync()
    {
        try
        {
            var connection = DBusConnection.Session;

            MessageBuffer CreateMessage()
            {
                using var writer = connection.GetMessageWriter();

                writer.WriteMethodCallHeader(
                    destination: KWinService,
                    path: NightLightPath,
                    @interface: PropertiesInterface,
                    member: "GetAll",
                    signature: "s"
                );

                writer.WriteString(NightLightInterface);

                return writer.CreateMessage();
            }

            var properties = await connection.CallMethodAsync(
                CreateMessage(),
                static (message, _) =>
                {
                    var reader = message.GetBodyReader();
                    return reader.ReadDictionaryOfStringToVariantValue();
                },
                null
            );

            return new NightLightState(
                properties["enabled"].GetBool(),
                properties["running"].GetBool(),
                properties["inhibited"].GetBool(),
                properties["mode"].GetUInt32() == ConstantMode,
                (int)properties["targetTemperature"].GetUInt32()
            );
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Failed to read Night Light's state: {ex.Message}");
            return null;
        }
    }

    public static async Task<IDisposable> WatchAsync(
        Action onBrightnessChanged,
        Action onNightLightChanged
    )
    {
        async Task<IDisposable> TryWatchSignalAsync(
            string sender,
            string path,
            string @interface,
            string signal,
            Action callback
        )
        {
            try
            {
                return await DBusConnection.Session.WatchSignalAsync(
                    sender,
                    path,
                    @interface,
                    signal,
                    (Notification notification) =>
                    {
                        // The handler also hears about the watch ending, which is not a signal
                        if (notification.Type == NotificationType.Value)
                            callback();
                    },
                    ObserverFlags.None,
                    false,
                    null
                );
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Failed to watch {@interface}.{signal}: {ex.Message}");
                return Disposable.Null;
            }
        }

        return Disposable.Merge(
            await TryWatchSignalAsync(
                BrightnessService,
                BrightnessPath,
                BrightnessService,
                "BrightnessChanged",
                onBrightnessChanged
            ),
            await TryWatchSignalAsync(
                KWinService,
                NightLightPath,
                PropertiesInterface,
                "PropertiesChanged",
                onNightLightChanged
            )
        );
    }
}
