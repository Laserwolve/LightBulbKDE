using System;
using System.IO;
using System.Linq;
using System.Runtime.Versioning;
using System.Text;
using System.Threading;

namespace LightBulb.PlatformInterop;

// The freedesktop.org autostart entry that Linux desktops, KDE Plasma included, start at login:
// a .desktop file in ~/.config/autostart, the counterpart of the Run key in the Windows registry
[SupportedOSPlatform("linux")]
public class XdgAutoStartSwitch(string name, string executableFilePath, string arguments)
{
    private readonly Lock _lock = new();

    private string FilePath =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "autostart",
            $"{name}.desktop"
        );

    private string ExecLine => $"Exec={Quote(executableFilePath)} {arguments}".TrimEnd();

    public bool IsSet
    {
        get
        {
            using (_lock.EnterScope())
            {
                // Only an entry that starts this copy of the app counts, as with the registry value
                return File.Exists(FilePath) && File.ReadLines(FilePath).Contains(ExecLine);
            }
        }
        set
        {
            using (_lock.EnterScope())
            {
                // Avoid unnecessary changes
                if (IsSet == value)
                    return;

                if (value)
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
                    File.WriteAllText(
                        FilePath,
                        $"""
                        [Desktop Entry]
                        Type=Application
                        Name={name}
                        {ExecLine}
                        Terminal=false

                        """
                    );
                }
                else
                {
                    File.Delete(FilePath);
                }
            }
        }
    }

    // The Desktop Entry spec quotes an argument in double quotes with a backslash before any ",
    // `, $ or \ in it, and then applies its general string escaping, which doubles every backslash
    private static string Quote(string argument)
    {
        var quoted = new StringBuilder("\"");

        foreach (var c in argument)
        {
            if (c is '"' or '`' or '$' or '\\')
                quoted.Append('\\');

            quoted.Append(c);
        }

        quoted.Append('"');

        return quoted.ToString().Replace("\\", "\\\\");
    }
}
