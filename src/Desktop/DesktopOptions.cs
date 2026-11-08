using System.Text.RegularExpressions;

namespace OptimizationTool.Desktop;

/// <summary>Command line: --ui-dir &lt;path&gt;, --fixture-speed &lt;value&gt;, --sample-seconds &lt;1-600&gt;, --source windows|fixture, --dev-allow-any-host, --pipe-name &lt;name&gt;.</summary>
public sealed partial record DesktopOptions
{
    public string? UiDir { get; init; }
    public string? FixtureSpeed { get; init; }
    public int? SampleSeconds { get; init; }
    /// <summary>windows | fixture</summary>
    public string? Source { get; init; }
    public bool DevAllowAnyHost { get; init; }
    public string? PipeName { get; init; }

    public static DesktopOptions Parse(IReadOnlyList<string> args)
    {
        var o = new DesktopOptions();
        for (var i = 0; i < args.Count; i++)
        {
            var a = args[i];
            switch (a)
            {
                case "--ui-dir":
                    o = o with { UiDir = Path.GetFullPath(Value(args, ref i, a)) };
                    break;
                case "--fixture-speed":
                    var speed = Value(args, ref i, a);
                    if (!TokenPattern().IsMatch(speed)) throw new ArgumentException($"Invalid value for {a}.");
                    o = o with { FixtureSpeed = speed };
                    break;
                case "--sample-seconds":
                    var raw = Value(args, ref i, a);
                    if (raw.Length > 3 || !raw.All(char.IsAsciiDigit) || !int.TryParse(raw, System.Globalization.NumberStyles.None,
                            System.Globalization.CultureInfo.InvariantCulture, out var seconds) || seconds is < 1 or > 600)
                        throw new ArgumentException($"{a} must be an integer from 1 to 600.");
                    o = o with { SampleSeconds = seconds };
                    break;
                case "--source":
                    var source = Value(args, ref i, a);
                    if (source is not ("windows" or "fixture")) throw new ArgumentException($"{a} must be windows or fixture.");
                    o = o with { Source = source };
                    break;
                case "--dev-allow-any-host":
                    o = o with { DevAllowAnyHost = true };
                    break;
                case "--pipe-name":
                    var name = Value(args, ref i, a);
                    if (!TokenPattern().IsMatch(name) || name.Length > 200) throw new ArgumentException($"Invalid value for {a}.");
                    o = o with { PipeName = name };
                    break;
                default:
                    throw new ArgumentException($"Unknown argument {a}.");
            }
        }
        return o;
    }

    /// <summary>Arguments passed through to OptimizationTool.Host.exe when the shell launches it.</summary>
    public IReadOnlyList<string> HostArguments()
    {
        var args = new List<string>();
        if (FixtureSpeed is not null) args.AddRange(["--fixture-speed", FixtureSpeed]);
        if (SampleSeconds is { } s) args.AddRange(["--sample-seconds", s.ToString(System.Globalization.CultureInfo.InvariantCulture)]);
        if (Source is not null) args.AddRange(["--source", Source]);
        return args;
    }

    private static string Value(IReadOnlyList<string> args, ref int i, string name)
    {
        if (i + 1 >= args.Count) throw new ArgumentException($"{name} needs a value.");
        return args[++i];
    }

    [GeneratedRegex(@"^[A-Za-z0-9._\-]{1,200}$")]
    private static partial Regex TokenPattern();
}
