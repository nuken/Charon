using System.IO;

namespace Charon.Config;

public static class AppPaths
{
    public static string BinDirectory => AppContext.BaseDirectory;

    public static string DataDirectory
    {
        get
        {
            string dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Charon");
            Directory.CreateDirectory(dir);
            return dir;
        }
    }

    public static string ConfigFilePath => Path.Combine(DataDirectory, "config.json");

    public static string ProfilesDirectory
    {
        get
        {
            string dir = Path.Combine(DataDirectory, "Profiles");
            Directory.CreateDirectory(dir);
            return dir;
        }
    }

    public static string ProvidersDirectory
    {
        get
        {
            string dir = Path.Combine(DataDirectory, "Providers");
            Directory.CreateDirectory(dir);
            return dir;
        }
    }

    public static string FfmpegPath => Path.Combine(BinDirectory, "ffmpeg.exe");

    /// <summary>
    /// Copies bundled provider templates from the install folder into %LocalAppData%\Charon\Providers
    /// on first run. Existing channels.json and config files are preserved across app updates.
    /// </summary>
    public static void EnsureDefaultProvidersSeeded()
    {
        string bundledProviders = Path.Combine(BinDirectory, "Providers");
        if (!Directory.Exists(bundledProviders))
            return;

        foreach (string sourceProviderDir in Directory.GetDirectories(bundledProviders))
        {
            string providerName = Path.GetFileName(sourceProviderDir);
            string targetProviderDir = Path.Combine(ProvidersDirectory, providerName);
            Directory.CreateDirectory(targetProviderDir);

            foreach (string sourceFile in Directory.GetFiles(sourceProviderDir))
            {
                string fileName = Path.GetFileName(sourceFile);
                string targetFile = Path.Combine(targetProviderDir, fileName);

                // Never overwrite the user's customized channels.json if it already exists!
                // Always update style.css and tune.js if needed, or only seed missing files.
                if (fileName.Equals("channels.json", StringComparison.OrdinalIgnoreCase) && File.Exists(targetFile))
                    continue;

                File.Copy(sourceFile, targetFile, overwrite: true);
            }
        }
    }
}