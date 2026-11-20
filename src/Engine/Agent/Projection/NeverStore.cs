using System.Text.RegularExpressions;

namespace OptimizationTool.Engine.Agent;

/// <summary>
/// The never-store check (m9 §6.11, review R4) run on every value an inventory item, the startup evidence or a check-up
/// entry keeps, right before it is stored or sent: one implementation, field-specific rules. A hit drops the field; it is
/// never cut down to pass. The identity names are checked by the caller with <see cref="IdentityFilter"/>.
/// <list type="bullet">
/// <item>A name or publisher (<see cref="RejectsName"/>): everything <see cref="Sanitize.LooksSensitive"/> rejects (paths,
/// IPv4/IPv6, MAC, SID, email, URL, GUID, credentials, long hex/base64 tokens), plus an IPv6 shape (<see cref="Ipv6Like"/>),
/// a MAC in dotted notation (<c>0011.2233.4455</c>, <see cref="DottedMacLike"/>), an IPv4 shape even with a port or a file
/// extension after it (<see cref="Ipv4Like"/>), a product key or grouped serial (<see cref="KeyLike"/>) and a serial run
/// (<see cref="SerialLike"/>).</item>
/// <item>A version (<see cref="RejectsVersion"/>): the same, except that a value that is WHOLLY a plain dotted quad such as
/// <c>1.2.3.4</c> is a version, not an IPv4 address (review S4: <c>192.168.1.20:8080</c> or <c>1.2.3.4 beta</c> is
/// rejected), and a value that is WHOLLY a plain dotted decimal number such as <c>1234.5678.9012</c> is a version, not a
/// dotted MAC (review S4b). Hex-ish parts such as <c>1.0.0.abcd</c> are not IPv6: that takes <c>::</c> or three colon-separated hex
/// groups.</item>
/// </list>
/// </summary>
public static class NeverStore
{
    private static readonly TimeSpan Budget = TimeSpan.FromMilliseconds(100);

    /// <summary><c>::</c> next to a hex digit (compressed, zoned and IPv4-mapped forms), or three colon-separated hex groups.</summary>
    private static readonly Regex Ipv6 = new(@"(?:[0-9a-f]::|::[0-9a-f])|(?<![0-9a-z])[0-9a-f]{1,4}(?::[0-9a-f]{1,4}){2,}",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, Budget);

    /// <summary>
    /// A product key or grouped serial: four or more dash-separated groups of 4–8 letters or digits, all upper-case or
    /// digits (<c>VK7JG-NPHTM-C97JM-9MPGT-3V66T</c>, <c>ABCD-1234-EF56-7890</c>), or a 5×5 key in any case.
    /// </summary>
    private static readonly Regex Key = new(@"(?<![A-Za-z0-9-])(?:[A-Z0-9]{4,8}(?:-[A-Z0-9]{4,8}){3,}|(?i:[a-z0-9]{5}(?:-[a-z0-9]{5}){4}))(?![A-Za-z0-9-])",
        RegexOptions.CultureInvariant, Budget);

    /// <summary>
    /// A MAC in dotted (Cisco) notation, any case: three dot-separated groups of four hex digits, whatever punctuation follows
    /// (review S4b: <c>0011.2233.4455.</c>, <c>0011.2233.4455.exe</c>, <c>(0011.2233.4455)</c>). Only a following hex digit
    /// stops a match; a preceding hex digit or hex-then-dot does, so a match never starts inside a longer dotted hex run
    /// (<c>12345.6789.0123</c> is not one) and a run of four or more such groups is matched at its first three.
    /// </summary>
    private static readonly Regex DottedMac = new(@"(?<![0-9a-f]|[0-9a-f]\.)[0-9a-f]{4}\.[0-9a-f]{4}\.[0-9a-f]{4}(?![0-9a-f])",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, Budget);

    /// <summary>A plain dotted decimal number and nothing else (<c>1234.5678.9012</c>, <c>10.0.26100.2</c>).</summary>
    private static readonly Regex PlainDecimal = new(@"^\d+(?:\.\d+)+$", RegexOptions.CultureInvariant, Budget);

    /// <summary>
    /// An IPv4 shape: four dot-separated groups of 1–3 digits that are not part of a longer dotted number, whatever text
    /// follows (<c>192.168.1.20:8080</c>, <c>192.168.1.20.exe</c>).
    /// </summary>
    private static readonly Regex Ipv4 = new(@"(?<!\d|\d\.)(?:\d{1,3}\.){3}\d{1,3}(?!\d|\.\d)", RegexOptions.CultureInvariant, Budget);

    /// <summary>The one IPv4-shaped value a version may be: four groups of digits and nothing else.</summary>
    private static readonly Regex PlainQuad = new(@"^\d{1,3}(?:\.\d{1,3}){3}$", RegexOptions.CultureInvariant, Budget);

    /// <summary>True when a name or publisher may not be stored.</summary>
    public static bool RejectsName(string? s) =>
        !string.IsNullOrEmpty(s) && (Sanitize.LooksSensitive(s) || Ipv6Like(s) || DottedMacLike(s) || Ipv4Like(s) || KeyLike(s) || SerialLike(s));

    /// <summary>
    /// True when a version may not be stored. A value that is wholly a plain dotted quad is a version here, not an IPv4
    /// address; any other value with an IPv4 shape (a port, surrounding text) is rejected (review S4). Likewise a value
    /// that is wholly a plain dotted decimal number is a version, not a dotted MAC (review S4b: <c>1234.5678.9012</c> is
    /// kept, as a real MAC with no a–f digit is rare and versions like that exist); a dotted MAC with any hex letter
    /// (<c>00a1.2233.4455</c>) or with anything around it (<c>0011.2233.4455.</c>, <c>v0011.2233.4455</c>) is rejected.
    /// Names, publishers and model fields reject the dotted MAC shape whatever its digits.
    /// </summary>
    public static bool RejectsVersion(string? s) =>
        !string.IsNullOrEmpty(s) && (Sanitize.LooksSensitiveVersion(s) || Ipv6Like(s) || DottedMacLike(s) && !WholePlainDecimal(s) || Ipv4Like(s) && !WholePlainQuad(s)
            || KeyLike(s) || SerialLike(s));

    /// <summary>
    /// True when a hardware MODEL field may not be stored or sent (review R4b): a value read from a dedicated model property
    /// (system model and family, board product, drive model, monitor name and product code, CPU and GPU names, memory part
    /// number), serial- or key-shaped by nature (<c>20XW004GUS</c>, <c>WDC WD10EZEX-08WN4A0</c>). As <see cref="RejectsName"/>
    /// without the serial-run and key-shape rules; the caller still applies the identity filter, path separators and length.
    /// </summary>
    public static bool RejectsModel(string? s) =>
        !string.IsNullOrEmpty(s) && (Sanitize.LooksSensitive(s) || Ipv6Like(s) || DottedMacLike(s) || Ipv4Like(s));

    /// <summary>A dotted MAC (see <see cref="DottedMac"/>). A regex timeout counts as a hit.</summary>
    public static bool DottedMacLike(string s) { try { return DottedMac.IsMatch(s); } catch (RegexMatchTimeoutException) { return true; } }

    /// <summary>An IPv4 shape anywhere (see <see cref="Ipv4"/>). A regex timeout counts as a hit.</summary>
    public static bool Ipv4Like(string s) { try { return Ipv4.IsMatch(s); } catch (RegexMatchTimeoutException) { return true; } }

    private static bool WholePlainQuad(string s) { try { return PlainQuad.IsMatch(s); } catch (RegexMatchTimeoutException) { return false; } }

    private static bool WholePlainDecimal(string s) { try { return PlainDecimal.IsMatch(s); } catch (RegexMatchTimeoutException) { return false; } }

    /// <summary>An IPv6 address shape (see <see cref="Ipv6"/>). A regex timeout counts as a hit.</summary>
    public static bool Ipv6Like(string s) { try { return Ipv6.IsMatch(s); } catch (RegexMatchTimeoutException) { return true; } }

    /// <summary>A product key or grouped serial (see <see cref="Key"/>). A regex timeout counts as a hit.</summary>
    public static bool KeyLike(string s) { try { return Key.IsMatch(s); } catch (RegexMatchTimeoutException) { return true; } }

    /// <summary>
    /// A token (split at spaces, commas, parentheses, semicolons and dots, so a file extension never hides one) of ≥ 10
    /// letters and digits with ≥ 4 digits, a letter and no run of three lower-case letters: a serial or ID, not a word
    /// (<c>WD-WCC4E1234567</c>, <c>E823_8FA6_BF53_0001</c>, <c>AB12CD34EF56GH.exe</c>). Separators inside a token do not
    /// count towards the 10 (<c>7z2408-x64</c> has 9).
    /// </summary>
    public static bool SerialLike(string s)
    {
        foreach (var token in s.Split([' ', ',', '(', ')', ';', '.'], StringSplitOptions.RemoveEmptyEntries))
        {
            if (token.Count(char.IsAsciiLetterOrDigit) < 10 || token.Count(char.IsAsciiDigit) < 4 || !token.Any(char.IsAsciiLetter)) continue;
            var run = 0;
            var word = false;
            foreach (var c in token)
            {
                run = char.IsAsciiLetterLower(c) ? run + 1 : 0;
                if (run >= 3) { word = true; break; }
            }
            if (!word) return true;
        }
        return false;
    }
}
