using System.Security.Cryptography;

namespace PhpDevTray.Infrastructure.Downloads;

internal static class ArtifactIntegrity
{
    internal static async Task VerifySha256Async(string path, string expected, CancellationToken cancellationToken = default)
    {
        if (expected.Length != 64 || !expected.All(Uri.IsHexDigit)) throw new InvalidDataException("The publisher did not provide a valid SHA-256 checksum.");
        await using var input = File.OpenRead(path);
        var actual = await SHA256.HashDataAsync(input, cancellationToken);
        if (!CryptographicOperations.FixedTimeEquals(actual, Convert.FromHexString(expected)))
            throw new InvalidDataException("The downloaded archive does not match the publisher's SHA-256 checksum. It was not extracted or executed.");
    }
}
