using System.Text.RegularExpressions;

namespace DesktopHost.Signatures;

/// <summary>
/// A publisher on macOS, named by the Apple developer team its Developer ID certificate was issued to: the ten-character team
/// identifier that appears as <c>TeamIdentifier</c> in a code signature and as the <c>OU</c> of the signing certificate's subject.
/// </summary>
/// <remarks>
/// A team identifier outlives the certificate that carries it: a renewal issues a new certificate to the same team, so a pin on the
/// team keeps passing across it, while the signature check itself still requires a certificate Apple issued for Developer ID.
/// </remarks>
public sealed partial record AppleTeam
{
    /// <summary>Names the team <paramref name="teamIdentifier"/>.</summary>
    /// <exception cref="ArgumentException">Not ten upper-case letters and digits — the identifier ends up inside a code
    /// requirement, so nothing else is accepted.</exception>
    public AppleTeam(string teamIdentifier)
    {
        ArgumentNullException.ThrowIfNull(teamIdentifier);
        if (!TeamIdentifierPattern().IsMatch(teamIdentifier))
            throw new ArgumentException("A team identifier is ten upper-case letters and digits.", nameof(teamIdentifier));
        TeamIdentifier = teamIdentifier;
    }

    /// <summary>The team identifier, exactly as Apple issued it.</summary>
    public string TeamIdentifier { get; }

    [GeneratedRegex("^[A-Z0-9]{10}$")]
    private static partial Regex TeamIdentifierPattern();
}
