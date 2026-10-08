using System;
using System.Threading.Tasks;
using Grid.Auth.Features;

namespace Grid.Auth.Tests;

public static class AlgorithmsTests
{
    public static void Register()
    {
        TestRunner.Add("pkce", "RFC 7636 S256 vector", async () =>
        {
            // https://www.rfc-editor.org/rfc/rfc7636#appendix-B
            const string verifier = "dBjftJeZ4CVP-mB92K27uhbUJU1p1r_wW1gFWFOEjXk";
            const string challenge = "E9Melhoa2OwvFrEMTJguCHaoeK1t8URWbuGJSstw-cM";
            var computed = Pkce.ComputeChallenge(verifier);
            Assert.Equal(challenge, computed, "PKCE S256 challenge matches RFC 7636 vector");
            await Task.CompletedTask;
        });

        TestRunner.Add("pkce", "generated state/verifier/verifier lengths", async () =>
        {
            var state = Pkce.GenerateState();
            var verifier = Pkce.GenerateVerifier();
            var challenge = Pkce.ComputeChallenge(verifier);
            Assert.True(Pkce.IsValidState(state), "generated state is valid (length/alphabet)");
            Assert.True(state.Length >= Pkce.StateMinLength && state.Length <= Pkce.StateMaxLength, "state length bound");
            Assert.True(Pkce.IsValidVerifier(verifier), "generated verifier is valid");
            Assert.True(verifier.Length >= Pkce.VerifierMinLength && verifier.Length <= Pkce.VerifierMaxLength, "verifier length bound");
            Assert.True(Pkce.IsValidChallenge(challenge), "challenge is 43 chars base64url");
            await Task.CompletedTask;
        });

        TestRunner.Add("pkce", "invalid inputs rejected", async () =>
        {
            Assert.True(!Pkce.IsValidState("short"), "short state rejected");
            Assert.True(!Pkce.IsValidVerifier("short"), "short verifier rejected");
            Assert.True(!Pkce.IsValidChallenge("E9Melhoa2OwvFrEMTJguCHaoeK1t8URWbuGJSstw-cM+"), "non-base64url challenge rejected");
            await Task.CompletedTask;
        });

        TestRunner.Add("base64url", "RFC 4648 vectors round-trip", async () =>
        {
            // RFC 4648 §10: 0xFB 0xFF -> base64url "-_8" (unpadded).
            var vector = new byte[] { 0xFB, 0xFF };
            var encoded = Base64Url.Encode(vector);
            Assert.Equal("-_8", encoded, "RFC 4648 fb/ff => -_8");
            var decoded = Base64Url.Decode(encoded);
            Assert.True(decoded.Length == 2 && decoded[0] == 0xFB && decoded[1] == 0xFF, "decode round-trips");
            await Task.CompletedTask;
        });

        TestRunner.Add("base64url", "encode strips padding and is stable", async () =>
        {
            var bytes = new byte[] { 0xDE, 0xAD, 0xBE, 0xEF, 0x12, 0x34 };
            var encoded = Base64Url.Encode(bytes);
            Assert.True(!encoded.Contains('='), "no padding");
            Assert.True(!encoded.Contains('+') && !encoded.Contains('/'), "url-safe alphabet");
            var decoded = Base64Url.Decode(encoded);
            Assert.Equal(bytes.Length, decoded.Length, "round-trip length");
            for (var i = 0; i < bytes.Length; i++) Assert.Equal(bytes[i], decoded[i], $"byte {i} round-trips");
            await Task.CompletedTask;
        });

        TestRunner.Add("base64url", "IsBase64Url rejects unsafe alphabet", async () =>
        {
            Assert.True(Base64Url.IsBase64Url("Ab-9_z"), "ok alphabet accepted");
            Assert.True(!Base64Url.IsBase64Url("a+b"), "'+' rejected");
            Assert.True(!Base64Url.IsBase64Url("a=b"), "padding rejected");
            Assert.True(!Base64Url.IsBase64Url(""), "empty rejected");
            await Task.CompletedTask;
        });

        TestRunner.Add("identity-labels", "provider id to label mapping", async () =>
        {
            Assert.Equal("Microsoft", IdentityLabels.ForProviderId("microsoft"), "microsoft label");
            Assert.Equal("GitHub", IdentityLabels.ForProviderId("github"), "github label");
            Assert.Equal("Google", IdentityLabels.ForProviderId("google"), "google label");
            Assert.Equal("Magic email", IdentityLabels.ForProviderId("email"), "email label");
            Assert.Equal("Unknown account", IdentityLabels.ForProviderId(null), "null label");
            await Task.CompletedTask;
        });
    }
}