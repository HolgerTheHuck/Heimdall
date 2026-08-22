using Heimdall;
using Xunit;

namespace Heimdall.Tests;

/// <summary>
/// C2 — zeitkonstanter Secret-Vergleich (<see cref="SecretComparer"/>): gleiche
/// Strings → true, unterschiedliche → false, verschiedene Länge → false, null → false.
/// Verwendet <c>CryptographicOperations.FixedTimeEquals</c> über UTF-8-Bytes.
/// </summary>
public class SecretComparerTests
{
    [Fact]
    public void Equals_Gleiche_Strings_Liefert_True()
    {
        Assert.True(SecretComparer.Equals("secret-123", "secret-123"));
        Assert.True(SecretComparer.Equals("", ""));
    }

    [Fact]
    public void Equals_Unterschiedliche_Strings_Liefert_False()
    {
        Assert.False(SecretComparer.Equals("secret-123", "secret-124"));
        Assert.False(SecretComparer.Equals("abc", "abd"));
    }

    [Fact]
    public void Equals_Verschiedene_Laenge_Liefert_False()
    {
        Assert.False(SecretComparer.Equals("short", "shorter"));
        Assert.False(SecretComparer.Equals("longer-value", "longer"));
    }

    [Fact]
    public void Equals_Null_Liefert_False()
    {
        Assert.False(SecretComparer.Equals(null, "x"));
        Assert.False(SecretComparer.Equals("x", null));
        Assert.False(SecretComparer.Equals(null, null));
    }
}