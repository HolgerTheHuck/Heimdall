using System;
using System.Security.Cryptography;
using System.Text;

namespace Heimdall;

/// <summary>
/// Zeitkonstanter Vergleich zweier Strings (z. B. API-Key, Basic-Auth-Passwort).
/// Schützt vor Timing-Angriffen, die über die Dauer des String-Vergleichs
/// Zeichen für Zeichen das Geheimnis ausspionieren könnten.
///
/// Implementiert über <see cref="CryptographicOperations.FixedTimeEquals"/> auf
/// den UTF-8-Bytes. <c>null</c>-Argumente liefern <c>false</c>. Verschiedene
/// Längen werden sofort (und deterministisch) abgelehnt — ein minimales
/// Längen-Leak, das für typische Config-Secrets akzeptiert ist und dem
/// vollständigen Zeichen-Timing-Leak von <c>==</c>/<c>!=</c> überlegen ist.
///
/// Lebt bewusst in den Abstractions (nicht im Host/gRPC-Receiver), damit sowohl
/// die Host-Auth-Middleware als auch der gRPC-Receiver dieselbe Helferstelle
/// nutzen — ohne dass die Receiver den Host referenzieren.
/// </summary>
public static class SecretComparer
{
    /// <summary>
    /// Vergleicht <paramref name="a"/> und <paramref name="b"/> zeitkonstant.
    /// <c>true</c> nur bei exakter Übereinstimmung (inkl. gleicher Länge);
    /// <c>null</c>-Argumente → <c>false</c>.
    /// </summary>
    public static bool Equals(string? a, string? b)
    {
        if (a is null || b is null) return false;
        var ba = Encoding.UTF8.GetBytes(a);
        var bb = Encoding.UTF8.GetBytes(b);
        return CryptographicOperations.FixedTimeEquals(ba, bb);
    }
}