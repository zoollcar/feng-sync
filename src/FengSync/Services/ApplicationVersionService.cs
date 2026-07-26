using System.Diagnostics;
using System.Reflection;
using FengSync.Core.Updates;

namespace FengSync.Services;

public sealed class ApplicationVersionService
{
    private readonly Assembly _assembly;
    public ApplicationVersionService(Assembly? assembly = null) => _assembly = assembly ?? Assembly.GetEntryAssembly() ?? typeof(ApplicationVersionService).Assembly;
    public ReleaseVersion CurrentVersion
    {
        get
        {
            var info = _assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
            if (ReleaseVersion.TryParse(info, out var version)) return version;
            var product = string.IsNullOrEmpty(_assembly.Location) ? null : FileVersionInfo.GetVersionInfo(_assembly.Location).ProductVersion;
            if (ReleaseVersion.TryParse(product, out version)) return version;
            return new ReleaseVersion(_assembly.GetName().Version?.Major ?? 0, _assembly.GetName().Version?.Minor ?? 0, Math.Max(0, _assembly.GetName().Version?.Build ?? 0));
        }
    }
    public string DisplayVersion => CurrentVersion.ToString().Split('+')[0];
}
