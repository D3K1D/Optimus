using System.Text;

namespace OptimizationTool.Engine.Agent;

/// <summary>
/// One name of this PC that no stored or sent field may name: the user, PC or domain name. <see cref="TokenOnly"/> marks
/// the domain name, which matches only as a whole token (<see cref="IdentityFilter"/>). A plain string converts to a
/// user/PC-kind identity.
/// </summary>
public sealed record LocalIdentity(string Value, bool TokenOnly = false)
{
    public static implicit operator LocalIdentity(string? value) => new(value ?? "");
}

/// <summary>
/// The one test for "does this text name this PC's user, PC or domain" (m9 review R3, R3b), used before an inventory item
/// is stored (InventoryKit) and again before the check-up prompt is built (<see cref="CheckupPolicy"/>). A match drops the
/// field; it is never cut down to pass.
/// <para>The rule:</para>
/// <list type="bullet">
/// <item>Normalisation (<see cref="Fold"/>), the same for text and identity: Unicode NFKC (full-width and compatibility
/// forms fold to their plain form), then every character that is not a letter or digit is removed (spaces, separators,
/// punctuation, zero-width and other format characters, marks), then lower-case invariant.</item>
/// <item>A user or PC name whose folded form is 3+ characters matches by plain containment anywhere in the folded text,
/// with no word boundary: <c>jamieBackup</c>, <c>my-jamie</c>, <c>ｊａｍｉｅ</c>, <c>j.a.m.i.e</c> and <c>ja-mie</c> all
/// match <c>jamie</c>.</item>
/// <item>A user or PC name whose folded form is 1–2 characters (an account such as <c>Al</c> or <c>JD</c>) would match
/// nearly everything by containment, so it matches as a whole token instead: the NFKC text is split at every character
/// that is not a letter or digit, at letter↔digit transitions and at lower→upper case transitions (camelCase), and each
/// token is compared folded. <c>Al Tools</c>, <c>AlBackup</c>, <c>backup-al</c> and <c>JD2</c> match; <c>Realtek</c> and
/// <c>Calculator</c> do not.</item>
/// <item>The domain name (<see cref="LocalIdentity.TokenOnly"/>) matches as a whole token at every length, never by
/// containment: domains are often short common words (CORP, HOME, LAB, OFFICE), so domain <c>CORP</c> drops
/// <c>CORP Tools</c> and <c>corp-backup</c> but keeps <c>Microsoft Corporation</c>. A domain equal to the PC name once
/// folded (a PC outside a domain reports its own name) counts as the PC name and matches by containment.</item>
/// <item>An identity that folds to nothing is ignored. Nothing else is excluded by length.</item>
/// </list>
/// Text that cannot be normalised (malformed UTF-16) counts as a match, so it is dropped.
/// </summary>
public static class IdentityFilter
{
    /// <summary>This PC's user, PC and domain names as Windows reports them, tagged by <see cref="Local(string?, string?, string?)"/>.</summary>
    public static IReadOnlyList<LocalIdentity> Local()
    {
        static string? Safe(Func<string> read) { try { return read(); } catch (Exception ex) when (ex is InvalidOperationException or PlatformNotSupportedException) { return null; } }
        return Local(Safe(() => Environment.UserName), Safe(() => Environment.MachineName), Safe(() => Environment.UserDomainName));
    }

    /// <summary>
    /// The user and PC names, and the domain name as token-only unless it folds to the PC name (then it is the PC name).
    /// Only a missing, empty or whitespace name is left out; a 1-character name is kept. The result is <see cref="Prepare"/>d.
    /// </summary>
    public static IReadOnlyList<LocalIdentity> Local(string? user, string? pc, string? domain)
    {
        var list = new List<LocalIdentity>();
        if (!string.IsNullOrWhiteSpace(user)) list.Add(new(user));
        if (!string.IsNullOrWhiteSpace(pc)) list.Add(new(pc));
        if (!string.IsNullOrWhiteSpace(domain)) list.Add(new(domain, TokenOnly: Fold(domain) != Fold(pc)));
        return Prepare(list);
    }

    /// <summary>NFKC, letters and digits only, lower-case invariant. Null/empty or malformed in → "".</summary>
    public static string Fold(string? s) => Nfkc(s) is { } n ? Keep(n) : "";

    /// <summary>
    /// The identities folded once, empty ones left out, duplicates removed (first order kept). When one folded name comes
    /// both token-only and not, the containment kind (the stricter) is kept.
    /// </summary>
    public static IReadOnlyList<LocalIdentity> Prepare(IEnumerable<LocalIdentity?> identities)
    {
        var list = new List<LocalIdentity>();
        foreach (var id in identities)
        {
            if (id is null || Fold(id.Value) is not { Length: > 0 } f) continue;
            var at = list.FindIndex(x => x.Value == f);
            if (at < 0) list.Add(new(f, id.TokenOnly));
            else if (!id.TokenOnly) list[at] = new(f);
        }
        return list;
    }

    /// <summary>
    /// True when <paramref name="text"/> names one of <paramref name="identities"/> under the rule above. The identities
    /// may be raw or <see cref="Prepare"/>d (folding is idempotent).
    /// </summary>
    public static bool Contains(string? text, IReadOnlyList<LocalIdentity> identities)
    {
        if (string.IsNullOrEmpty(text) || identities.Count == 0) return false;
        if (Nfkc(text) is not { } nfkc) return true;
        var folded = Keep(nfkc);
        List<string>? tokens = null;
        foreach (var identity in identities)
        {
            var id = Fold(identity.Value);
            if (id.Length == 0) continue;
            if (id.Length >= 3 && !identity.TokenOnly)
            {
                if (folded.Contains(id, StringComparison.Ordinal)) return true;
                continue;
            }
            tokens ??= Tokens(nfkc);
            if (tokens.Contains(id, StringComparer.Ordinal)) return true;
        }
        return false;
    }

    private static string? Nfkc(string? s)
    {
        if (string.IsNullOrEmpty(s)) return "";
        try { return s.Normalize(NormalizationForm.FormKC); }
        catch (ArgumentException) { return null; }
    }

    private static string Keep(string nfkc)
    {
        var sb = new StringBuilder(nfkc.Length);
        foreach (var c in nfkc)
            if (char.IsLetterOrDigit(c)) sb.Append(char.ToLowerInvariant(c));
        return sb.ToString();
    }

    /// <summary>Folded tokens: split at non-letter-or-digit, letter↔digit and lower→upper.</summary>
    private static List<string> Tokens(string nfkc)
    {
        var tokens = new List<string>();
        var sb = new StringBuilder();
        var prev = '\0';
        foreach (var c in nfkc)
        {
            if (!char.IsLetterOrDigit(c)) { Flush(); prev = '\0'; continue; }
            if (sb.Length > 0 && (char.IsDigit(c) != char.IsDigit(prev) || char.IsLower(prev) && char.IsUpper(c))) Flush();
            sb.Append(char.ToLowerInvariant(c));
            prev = c;
        }
        Flush();
        return tokens;

        void Flush()
        {
            if (sb.Length > 0) tokens.Add(sb.ToString());
            sb.Clear();
        }
    }
}
