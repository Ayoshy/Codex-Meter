using System.Reflection;

namespace CodexUsageTray;

public readonly record struct AppVersion(int Major, int Minor, int Patch, int Revision = 0)
    : IComparable<AppVersion>
{
    public static AppVersion Current
    {
        get
        {
            var assembly = Assembly.GetExecutingAssembly();
            var informational = assembly
                .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
                .InformationalVersion;
            if (TryParse(informational, out var parsed))
            {
                return parsed;
            }

            var version = assembly.GetName().Version;
            return version is null
                ? new AppVersion(0, 0, 0)
                : new AppVersion(
                    version.Major,
                    Math.Max(0, version.Minor),
                    Math.Max(0, version.Build),
                    Math.Max(0, version.Revision));
        }
    }

    public static bool TryParse(string? value, out AppVersion version)
    {
        version = default;
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        var normalized = value.Trim();
        if (normalized.StartsWith('v') || normalized.StartsWith('V'))
        {
            normalized = normalized[1..];
        }

        var suffix = normalized.IndexOfAny(['-', '+']);
        if (suffix >= 0)
        {
            normalized = normalized[..suffix];
        }

        var parts = normalized.Split('.');
        if (parts.Length is < 2 or > 4 ||
            parts.Any(part => !int.TryParse(part, out var number) || number < 0))
        {
            return false;
        }

        version = new AppVersion(
            int.Parse(parts[0]),
            int.Parse(parts[1]),
            parts.Length > 2 ? int.Parse(parts[2]) : 0,
            parts.Length > 3 ? int.Parse(parts[3]) : 0);
        return true;
    }

    public int CompareTo(AppVersion other)
    {
        var major = Major.CompareTo(other.Major);
        if (major != 0) return major;
        var minor = Minor.CompareTo(other.Minor);
        if (minor != 0) return minor;
        var patch = Patch.CompareTo(other.Patch);
        return patch != 0 ? patch : Revision.CompareTo(other.Revision);
    }

    public override string ToString() => Revision == 0
        ? $"{Major}.{Minor}.{Patch}"
        : $"{Major}.{Minor}.{Patch}.{Revision}";
}
