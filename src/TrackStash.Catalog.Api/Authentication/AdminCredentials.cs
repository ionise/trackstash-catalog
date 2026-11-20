using System.Buffers.Binary;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Options;

namespace TrackStash.Catalog.Api.Authentication;

internal sealed class AdminCredentials(string username, string hash)
{
    internal const int MinPasswordLength = 16;
    internal const int MaxPasswordLength = 256;
    internal const int MaxUsernameLength = 128;
    private const int Iterations = 210_000;

    internal static PasswordHasher<object> CreateHasher() =>
        new(Options.Create(new PasswordHasherOptions { IterationCount = Iterations }));

    private static readonly PasswordHasher<object> Hasher = CreateHasher();
    private static readonly object Administrator = new();

    internal static AdminCredentials? FromConfiguration(IConfiguration configuration)
    {
        var username = configuration["CatalogApi:AdminUsername"];
        var hash = configuration["CatalogApi:AdminPasswordHash"];
        if (username is null && hash is null) return null;
        if (username is null || username.Length is 0 or > MaxUsernameLength
            || username.Any(c => char.IsWhiteSpace(c) || char.IsControl(c))
            || hash is null || !IsSupportedHash(hash))
            throw new InvalidOperationException(
                "Set both CatalogApi:AdminUsername and CatalogApi:AdminPasswordHash; use hash-admin-password to provision the hash.");
        return new AdminCredentials(username, hash);
    }

    private static bool IsSupportedHash(string hash)
    {
        // Identity V3: marker, PRF, iteration count, salt length, 16-byte salt, 32-byte subkey.
        // Reject malformed, weak or excessively expensive configuration before serving requests.
        Span<byte> bytes = stackalloc byte[61];
        return hash.Length == 84 && Convert.TryFromBase64String(hash, bytes, out var length) && length == bytes.Length
            && Convert.ToBase64String(bytes) == hash && bytes[0] == 1
            && BinaryPrimitives.ReadUInt32BigEndian(bytes[1..]) == 2 // HMAC-SHA512
            && BinaryPrimitives.ReadUInt32BigEndian(bytes[5..]) is >= Iterations and <= 1_000_000
            && BinaryPrimitives.ReadUInt32BigEndian(bytes[9..]) == 16;
    }

    internal bool Verify(string suppliedUsername, string password)
    {
        var passwordMatches = Hasher.VerifyHashedPassword(Administrator, hash, password) != PasswordVerificationResult.Failed;
        return passwordMatches && string.Equals(username, suppliedUsername, StringComparison.Ordinal);
    }
}
