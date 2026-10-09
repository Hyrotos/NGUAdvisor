using System;

namespace NGUAdvisor.Managers
{
    // A PATH THE PERSON AT THE KEYBOARD CAN ACTUALLY OPEN.
    //
    // Under Proton the advisor lives inside Wine, so every path it knows is a Windows one --
    // C:\users\steamuser\AppData\... -- and that is what it printed. On a Linux desktop that string
    // opens nothing: the file is under the Wine prefix, at <prefix>/drive_c/users/steamuser/...
    //
    // Wine hands the Unix environment to the Windows process, so the prefix is readable from in here.
    // On real Windows neither variable is set and the path is returned exactly as it came in.
    //
    // FOR DISPLAY ONLY. Everything that OPENS a file -- including the paths handed to the companion,
    // which runs inside the same Wine prefix -- must keep the Windows path.
    public static class HostPath
    {
        // The Wine prefix this process runs in, or null on real Windows. Proton sets
        // STEAM_COMPAT_DATA_PATH to the compat-data folder, whose "pfx" child IS the prefix; a plain
        // Wine launch sets WINEPREFIX directly, and wins when both are present.
        public static string WinePrefix()
        {
            try
            {
                return PrefixFrom(Environment.GetEnvironmentVariable("WINEPREFIX"),
                                  Environment.GetEnvironmentVariable("STEAM_COMPAT_DATA_PATH"));
            }
            catch { return null; }
        }

        public static string PrefixFrom(string winePrefix, string steamCompatDataPath)
        {
            if (!string.IsNullOrEmpty(winePrefix)) return winePrefix.TrimEnd('/');
            if (!string.IsNullOrEmpty(steamCompatDataPath)) return steamCompatDataPath.TrimEnd('/') + "/pfx";
            return null;
        }

        public static string For(string windowsPath) => For(windowsPath, WinePrefix());

        // Drive C: is the prefix's drive_c, Z: is Wine's mapping of the Unix root, and any other
        // letter resolves through the prefix's dosdevices links. Anything that is not a drive path
        // (already Unix, relative, UNC) is left alone rather than guessed at.
        public static string For(string windowsPath, string prefix)
        {
            if (string.IsNullOrEmpty(windowsPath) || string.IsNullOrEmpty(prefix)) return windowsPath;
            if (windowsPath.Length < 3 || windowsPath[1] != ':' || (windowsPath[2] != '\\' && windowsPath[2] != '/'))
                return windowsPath;
            char drive = char.ToLowerInvariant(windowsPath[0]);
            if (drive < 'a' || drive > 'z') return windowsPath;

            string rest = windowsPath.Substring(3).Replace('\\', '/');
            if (drive == 'z') return "/" + rest;
            if (drive == 'c') return prefix + "/drive_c/" + rest;
            return prefix + "/dosdevices/" + drive + ":/" + rest;
        }
    }
}
