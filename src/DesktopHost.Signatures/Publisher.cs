using System.Security.Cryptography.X509Certificates;

namespace DesktopHost.Signatures;

/// <summary>
/// A publisher a signed file is expected to come from, named by the subject of its code-signing certificate: the
/// organization (<c>O</c>), and as many of the country (<c>C</c>), the registration number an extended-validation
/// certificate carries (<c>serialNumber</c>) and the common name (<c>CN</c>) as the caller wants pinned. A field left null
/// is not compared; every field given must equal the certificate's exactly.
/// </summary>
/// <remarks>
/// A subject outlives the certificate that carries it: a renewal issues a new certificate — a new thumbprint, possibly a new
/// issuing authority — to the same organization, so a pin on the subject keeps passing across it, while the signature check
/// itself still requires a certificate a trusted authority issued for code signing. An extended-validation certificate's
/// subject names the organization by its registration (<c>serialNumber</c>, with its jurisdiction), which no other
/// organization can obtain; pin it when the certificate has one.
/// </remarks>
/// <param name="Organization">The certificate subject's organization (<c>O</c>), exactly as issued.</param>
public sealed record Publisher(string Organization)
{
    /// <summary>The subject's country (<c>C</c>), or null to leave it unchecked.</summary>
    public string? Country { get; init; }

    /// <summary>The subject's <c>serialNumber</c> attribute (an organization's registration number on an extended-validation
    /// certificate — not the certificate's own serial number), or null to leave it unchecked.</summary>
    public string? SerialNumber { get; init; }

    /// <summary>The subject's common name (<c>CN</c>), or null to leave it unchecked.</summary>
    public string? CommonName { get; init; }

    private const string OrganizationOid = "2.5.4.10";
    private const string CountryOid = "2.5.4.6";
    private const string SerialNumberOid = "2.5.4.5";
    private const string CommonNameOid = "2.5.4.3";

    /// <summary>Whether <paramref name="subject"/> names this publisher.</summary>
    public bool Matches(X500DistinguishedName subject)
    {
        ArgumentNullException.ThrowIfNull(subject);
        var attributes = new List<(string Oid, string Value)>();
        foreach (var rdn in subject.EnumerateRelativeDistinguishedNames())
        {
            // A multi-valued component cannot be read as one attribute; it names nothing this record compares.
            if (rdn.HasMultipleElements) continue;
            if (rdn.GetSingleElementValue() is { } value)
                attributes.Add((rdn.GetSingleElementType().Value ?? "", value));
        }

        return Has(OrganizationOid, Organization)
            && (Country is null || Has(CountryOid, Country))
            && (SerialNumber is null || Has(SerialNumberOid, SerialNumber))
            && (CommonName is null || Has(CommonNameOid, CommonName));

        // Exactly one attribute of the kind, equal to the expected value: a subject that repeats an attribute is not read as
        // naming any one of its values.
        bool Has(string oid, string expected)
        {
            var values = attributes.Where(a => a.Oid == oid).Select(a => a.Value).ToList();
            return values.Count == 1 && string.Equals(values[0], expected, StringComparison.Ordinal);
        }
    }
}
