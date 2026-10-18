using System.Security.Cryptography;
using System.Text;

namespace Webhooks.Engine;

public enum VerifyResult
{
    Valid,
    Missing,
    Malformed,
    Mismatch,
    OutsideTolerance,
}

/// <summary>
/// Signs and verifies webhook requests. The signed text is "{eventId}.{timestamp}.{body}", so the event id and the
/// timestamp are protected as well as the body: nobody can replay an old request under a new id or a new time.
/// The header looks like <c>t=1760000000,v1=ab12...</c>. It can carry several <c>v1</c> values, which is how a
/// secret is rotated without dropping events.
/// </summary>
public static class Signature
{
    public static string Sign(string secret, string eventId, long timestamp, string body) =>
        $"t={timestamp},v1={Compute(secret, eventId, timestamp, body)}";

    public static VerifyResult Verify(string? header, string eventId, string body, IEnumerable<string> secrets, DateTimeOffset now, TimeSpan tolerance)
    {
        if (string.IsNullOrWhiteSpace(header))
        {
            return VerifyResult.Missing;
        }

        long? timestamp = null;
        var candidates = new List<byte[]>();
        foreach (var part in header.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            var eq = part.IndexOf('=');
            if (eq <= 0)
            {
                return VerifyResult.Malformed;
            }

            var name = part[..eq];
            var value = part[(eq + 1)..];
            if (name == "t")
            {
                if (!long.TryParse(value, out var t))
                {
                    return VerifyResult.Malformed;
                }

                timestamp = t;
            }
            else if (name == "v1")
            {
                try
                {
                    candidates.Add(Convert.FromHexString(value));
                }
                catch (FormatException)
                {
                    return VerifyResult.Malformed;
                }
            }
        }

        if (timestamp is null || candidates.Count == 0)
        {
            return VerifyResult.Malformed;
        }

        var matched = false;
        foreach (var secret in secrets)
        {
            var expected = Convert.FromHexString(Compute(secret, eventId, timestamp.Value, body));
            foreach (var candidate in candidates)
            {
                // Constant-time comparison, so the check does not leak how many bytes matched.
                matched |= CryptographicOperations.FixedTimeEquals(expected, candidate);
            }
        }

        if (!matched)
        {
            return VerifyResult.Mismatch;
        }

        var age = Math.Abs((now - DateTimeOffset.FromUnixTimeSeconds(timestamp.Value)).TotalSeconds);
        return age > tolerance.TotalSeconds ? VerifyResult.OutsideTolerance : VerifyResult.Valid;
    }

    public static string NewSecret() => "whsec_" + Convert.ToHexString(RandomNumberGenerator.GetBytes(24)).ToLowerInvariant();

    private static string Compute(string secret, string eventId, long timestamp, string body) =>
        Convert.ToHexString(HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), Encoding.UTF8.GetBytes($"{eventId}.{timestamp}.{body}"))).ToLowerInvariant();
}
