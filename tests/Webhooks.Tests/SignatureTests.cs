using Webhooks.Engine;

namespace Webhooks.Tests;

public class SignatureTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 12, 9, 0, 0, TimeSpan.Zero);
    private static readonly TimeSpan Tolerance = TimeSpan.FromMinutes(5);
    private const string Secret = "whsec_test";
    private const string Body = "{\"orderId\":42}";

    private static string Signed(DateTimeOffset at, string id = "evt_1", string body = Body, string secret = Secret) =>
        Signature.Sign(secret, id, at.ToUnixTimeSeconds(), body);

    private static VerifyResult Check(string? header, string id = "evt_1", string body = Body, params string[] secrets) =>
        Signature.Verify(header, id, body, secrets.Length == 0 ? [Secret] : secrets, Now, Tolerance);

    [Fact]
    public void A_correctly_signed_fresh_request_is_valid() =>
        Assert.Equal(VerifyResult.Valid, Check(Signed(Now)));

    [Fact]
    public void A_changed_body_is_rejected() =>
        Assert.Equal(VerifyResult.Mismatch, Check(Signed(Now), body: "{\"orderId\":43}"));

    [Fact]
    public void A_changed_event_id_is_rejected_so_a_captured_request_cannot_be_replayed_as_a_new_event() =>
        Assert.Equal(VerifyResult.Mismatch, Check(Signed(Now, id: "evt_1"), id: "evt_2"));

    [Fact]
    public void The_wrong_secret_is_rejected() =>
        Assert.Equal(VerifyResult.Mismatch, Check(Signed(Now, secret: "whsec_other")));

    [Fact]
    public void Rewriting_the_timestamp_to_look_fresh_breaks_the_signature()
    {
        var old = Signed(Now.AddHours(-2));
        var forged = old.Replace($"t={Now.AddHours(-2).ToUnixTimeSeconds()}", $"t={Now.ToUnixTimeSeconds()}");

        Assert.Equal(VerifyResult.Mismatch, Check(forged));
    }

    [Fact]
    public void A_genuine_but_old_request_is_a_replay()
    {
        Assert.Equal(VerifyResult.OutsideTolerance, Check(Signed(Now.AddMinutes(-6))));
        Assert.Equal(VerifyResult.OutsideTolerance, Check(Signed(Now.AddMinutes(6))));
    }

    [Fact]
    public void The_edge_of_the_tolerance_window_is_still_accepted()
    {
        Assert.Equal(VerifyResult.Valid, Check(Signed(Now.AddMinutes(-5))));
        Assert.Equal(VerifyResult.Valid, Check(Signed(Now.AddMinutes(5))));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void A_missing_header_is_reported_as_missing(string? header) =>
        Assert.Equal(VerifyResult.Missing, Check(header));

    [Theory]
    [InlineData("garbage")]
    [InlineData("t=abc,v1=00")]
    [InlineData("t=1760000000")]
    [InlineData("v1=abcdef")]
    [InlineData("t=1760000000,v1=zzzz")]
    public void A_malformed_header_is_reported_as_malformed(string header) =>
        Assert.Equal(VerifyResult.Malformed, Check(header));

    [Fact]
    public void During_a_rotation_a_request_signed_with_either_secret_is_accepted()
    {
        var signedWithOld = Signed(Now, secret: "old");
        var signedWithNew = Signed(Now, secret: "new");

        Assert.Equal(VerifyResult.Valid, Check(signedWithOld, secrets: ["new", "old"]));
        Assert.Equal(VerifyResult.Valid, Check(signedWithNew, secrets: ["new", "old"]));
        Assert.Equal(VerifyResult.Mismatch, Check(signedWithOld, secrets: ["new"]));
    }

    [Fact]
    public void A_header_may_carry_several_signatures_and_any_one_may_match()
    {
        var header = Signed(Now, secret: "stale") + "," + Signed(Now, secret: Secret).Split(',')[1];

        Assert.Equal(VerifyResult.Valid, Check(header));
    }
}
