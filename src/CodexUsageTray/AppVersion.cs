using System.Globalization;
using System.Reflection;

namespace CodexUsageTray;

public readonly record struct AppVersion : IComparable<AppVersion>
{
    public AppVersion(int major, int minor, int patch)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(major);
        ArgumentOutOfRangeException.ThrowIfNegative(minor);
        ArgumentOutOfRangeException.ThrowIfNegative(patch);

        Major = major;
        Minor = minor;
        Patch = patch;
    }

    public int Major { get; }

    public int Minor { get; }

    public int Patch { get; }

    public static AppVersion Current
    {
        get
        {
            var assembly = Assembly.GetExecutingAssembly();
            var informational = assembly
                .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
                .InformationalVersion;
            if (TryParseProductVersion(informational, out var parsed))
            {
                return parsed;
            }

            var version = assembly.GetName().Version;
            return version is null
                ? new AppVersion(0, 0, 0)
                : new AppVersion(
                    version.Major,
                    Math.Max(0, version.Minor),
                    Math.Max(0, version.Build));
        }
    }

    public static bool TryParseReleaseTag(string? value, out AppVersion version)
    {
        version = default;
        if (string.IsNullOrEmpty(value) ||
            value[0] != 'v')
        {
            return false;
        }

        return TryParseCore(value[1..], out version);
    }

    public static bool TryParseProductVersion(string? value, out AppVersion version)
    {
        version = default;
        if (string.IsNullOrEmpty(value))
        {
            return false;
        }

        var metadataSeparator = value.IndexOf('+');
        var core = value;
        if (metadataSeparator >= 0)
        {
            var metadata = value[(metadataSeparator + 1)..];
            if (!IsValidBuildMetadata(metadata))
            {
                return false;
            }

            core = value[..metadataSeparator];
        }

        return TryParseCore(core, out version);
    }

    private static bool TryParseCore(string value, out AppVersion version)
    {
        version = default;
        var parts = value.Split('.');
        if (parts.Length != 3)
        {
            return false;
        }

        Span<int> components = stackalloc int[3];
        for (var index = 0; index < parts.Length; index++)
        {
            var part = parts[index];
            if (part.Length == 0 ||
                part.Length > 1 && part[0] == '0' ||
                part.Any(character => !char.IsAsciiDigit(character)) ||
                !int.TryParse(
                    part,
                    NumberStyles.None,
                    CultureInfo.InvariantCulture,
                    out components[index]))
            {
                return false;
            }
        }

        version = new AppVersion(components[0], components[1], components[2]);
        return true;
    }

    private static bool IsValidBuildMetadata(string metadata)
    {
        return metadata.Length > 0 &&
               metadata[0] != '.' &&
               metadata[^1] != '.' &&
               !metadata.Contains("..", StringComparison.Ordinal) &&
               metadata.All(character =>
                   char.IsAsciiLetterOrDigit(character) ||
                   character is '-' or '.');
    }

    public int CompareTo(AppVersion other) =>
        (Major, Minor, Patch).CompareTo((other.Major, other.Minor, other.Patch));

    public override string ToString() => $"{Major}.{Minor}.{Patch}";
}
