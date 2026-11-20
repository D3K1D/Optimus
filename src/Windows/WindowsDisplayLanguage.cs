using System.Runtime.InteropServices;
using OptimizationTool.Engine;

namespace OptimizationTool.Windows;

/// <summary>
/// Windows' display language for the user (design v59 settingsPath): GetUserDefaultUILanguage, the language Windows
/// shows Settings and Windows Security in. Read on each call (one cheap kernel32 call), so a change takes
/// effect without restarting Optimus. Not CultureInfo.CurrentUICulture: that is the process's own culture, cached at
/// start, settable by code and invariant under globalization-invariant mode, so it can disagree with what Windows shows.
/// English is the primary language LANG_ENGLISH (0x09) in any region (en-US, en-GB, …).
/// </summary>
public sealed partial class WindowsDisplayLanguage : IDisplayLanguage
{
    private const int LangEnglish = 0x09;

    [LibraryImport("kernel32.dll")]
    private static partial ushort GetUserDefaultUILanguage();

    public bool IsEnglish => IsEnglishLangId(GetUserDefaultUILanguage());

    /// <summary>True when the LANGID's primary language (its low 10 bits) is English.</summary>
    internal static bool IsEnglishLangId(ushort langId) => (langId & 0x3FF) == LangEnglish;
}
