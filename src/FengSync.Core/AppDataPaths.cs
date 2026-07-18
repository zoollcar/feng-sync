namespace FengSync.Core;

/// <summary>Central application-data root. Integration tests can isolate all durable state with FENGSYNC_DATA_DIR.</summary>
public static class AppDataPaths
{
    public static string Root
    {
        get
        {
            var overridden = Environment.GetEnvironmentVariable("FENGSYNC_DATA_DIR");
            return string.IsNullOrWhiteSpace(overridden)
                ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FengSync")
                : Path.GetFullPath(overridden);
        }
    }
}
