using System.Security.Cryptography;
using Org.BouncyCastle.Bcpg;
using Org.BouncyCastle.Bcpg.OpenPgp;

namespace LocalServerManager.Infrastructure.Downloads;

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

    internal static async Task VerifyDetachedAsync(string path, byte[] detachedSignature, IReadOnlyList<byte[]> keyRings,
        IReadOnlySet<string> pinnedFingerprints, CancellationToken cancellationToken = default)
    {
        using var signatureInput = new MemoryStream(detachedSignature, false);
        using var decodedSignature = PgpUtilities.GetDecoderStream(signatureInput);
        var objects = new PgpObjectFactory(decodedSignature);
        var first = objects.NextPgpObject();
        if (first is PgpMarker) first = objects.NextPgpObject();
        if (first is not PgpSignatureList signatures || signatures.Count != 1 || objects.NextPgpObject() is not null)
            throw new InvalidDataException("The publisher's detached signature is not a single supported OpenPGP signature.");
        var signature = signatures[0];
        if (signature.SignatureType != PgpSignature.BinaryDocument || signature.HashAlgorithm is not HashAlgorithmTag.Sha256 and not HashAlgorithmTag.Sha384 and not HashAlgorithmTag.Sha512)
            throw new InvalidDataException("The archive signature uses an unsupported type or a hash weaker than SHA-256.");
        if (signature.CreationTime > DateTime.UtcNow.AddDays(1)) throw new InvalidDataException("The archive signature has an invalid future timestamp.");
        PgpPublicKey? signingKey = null;
        foreach (var encodedRing in keyRings)
        {
            using var ringInput = new MemoryStream(encodedRing, false);
            using var decodedRing = PgpUtilities.GetDecoderStream(ringInput);
            var bundle = new PgpPublicKeyRingBundle(decodedRing);
            foreach (PgpPublicKeyRing ring in bundle.GetKeyRings())
            {
                var candidate = ring.GetPublicKey(signature.KeyId);
                var primary = ring.GetPublicKey();
                if (candidate is null || !IsPinned(primary, pinnedFingerprints) || !IsPinned(candidate, pinnedFingerprints)) continue;
                if (!candidate.IsMasterKey && !HasValidSigningBinding(primary, candidate)) continue;
                if (candidate.HasRevocation()) throw new InvalidDataException("The archive was signed with a revoked Nginx key.");
                var validSeconds = candidate.GetValidSeconds();
                if (signature.CreationTime < candidate.CreationTime || (validSeconds > 0 && signature.CreationTime >= candidate.CreationTime.AddSeconds(validSeconds)))
                    throw new InvalidDataException("The archive signature falls outside its signing key's validity period.");
                signingKey = candidate;
                break;
            }
            if (signingKey is not null) break;
        }
        if (signingKey is null) throw new InvalidDataException("The archive signature does not match a pinned official Nginx signing key. Update the application before installing this release.");
        signature.InitVerify(signingKey);
        await using var input = File.OpenRead(path);
        var buffer = new byte[81920];
        int read;
        while ((read = await input.ReadAsync(buffer, cancellationToken)) != 0) signature.Update(buffer, 0, read);
        if (!signature.Verify()) throw new InvalidDataException("The archive's detached OpenPGP signature is invalid. It was not extracted or executed.");
    }

    private static bool IsPinned(PgpPublicKey key, IReadOnlySet<string> fingerprints) => fingerprints.Contains(Convert.ToHexString(key.GetFingerprint()));

    private static bool HasValidSigningBinding(PgpPublicKey primary, PgpPublicKey subkey)
    {
        foreach (PgpSignature binding in subkey.GetSignaturesOfType(PgpSignature.SubkeyBinding))
        {
            var packets = binding.GetHashedSubPackets();
            if (packets is null || (packets.GetKeyFlags() & 2) == 0) continue;
            binding.InitVerify(primary);
            if (binding.VerifyCertification(primary, subkey)) return true;
        }
        return false;
    }
}
