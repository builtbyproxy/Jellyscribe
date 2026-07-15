using System;
using System.IO;
using System.Xml.Serialization;
using LetterboxdSync.Configuration;
using LetterboxdSync.Security;
using Xunit;

namespace LetterboxdSync.Tests;

/// <summary>
/// Isolates the Data Protection key ring to a temp directory per test via
/// SecretProtector.KeyDirectoryOverride, mirroring SyncHistoryStaticTests'
/// DataPathOverride pattern.
/// </summary>
public class SecretProtectorTests : IDisposable
{
    private readonly string _tempDir;

    public SecretProtectorTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "lbs-secrets-" + Guid.NewGuid().ToString("N"));
        SecretProtector.KeyDirectoryOverride = _tempDir;
        SecretProtector.ResetForTesting();
    }

    public void Dispose()
    {
        SecretProtector.KeyDirectoryOverride = null;
        SecretProtector.ResetForTesting();
        try { if (Directory.Exists(_tempDir)) Directory.Delete(_tempDir, true); } catch { }
    }

    [Fact]
    public void Protect_RoundTrips()
    {
        var protectedValue = SecretProtector.Protect("hunter2");
        Assert.NotNull(protectedValue);
        Assert.NotEqual("hunter2", protectedValue);
        Assert.StartsWith("enc:v1:", protectedValue);

        Assert.Equal("hunter2", SecretProtector.Unprotect(protectedValue));
    }

    [Fact]
    public void Protect_NullAndEmpty_PassThrough()
    {
        Assert.Null(SecretProtector.Protect(null));
        Assert.Equal(string.Empty, SecretProtector.Protect(string.Empty));
        Assert.Null(SecretProtector.Unprotect(null));
        Assert.Equal(string.Empty, SecretProtector.Unprotect(string.Empty));
    }

    [Fact]
    public void Unprotect_LegacyPlaintext_ReturnedAsIs()
    {
        // Values written before encryption was added have no "enc:v1:" prefix.
        Assert.Equal("plaintext-password", SecretProtector.Unprotect("plaintext-password"));
    }

    [Fact]
    public void Unprotect_CorruptCiphertext_ReturnsNull()
    {
        Assert.Null(SecretProtector.Unprotect("enc:v1:not-actually-valid-ciphertext"));
    }

    [Fact]
    public void Unprotect_CiphertextFromDifferentKeyRing_ReturnsNull()
    {
        var protectedValue = SecretProtector.Protect("hunter2")!;

        // Point at a fresh, empty key ring - simulates the key ring being lost/rotated.
        var otherDir = Path.Combine(Path.GetTempPath(), "lbs-secrets-other-" + Guid.NewGuid().ToString("N"));
        SecretProtector.KeyDirectoryOverride = otherDir;
        SecretProtector.ResetForTesting();
        try
        {
            Assert.Null(SecretProtector.Unprotect(protectedValue));
        }
        finally
        {
            try { if (Directory.Exists(otherDir)) Directory.Delete(otherDir, true); } catch { }
        }
    }

    [Fact]
    public void Account_XmlRoundTrip_StoresPasswordEncrypted_ReadsBackPlaintext()
    {
        var account = new Account
        {
            UserJellyfinId = "user1",
            LetterboxdUsername = "8bitproxy",
            LetterboxdPassword = "correct horse battery staple",
            RawCookies = "cf_clearance=abc123"
        };

        var xml = SerializeToString(account);

        Assert.DoesNotContain("correct horse battery staple", xml);
        Assert.DoesNotContain("cf_clearance=abc123", xml);
        Assert.Contains("<LetterboxdPassword>enc:v1:", xml);
        Assert.Contains("<RawCookies>enc:v1:", xml);

        var roundTripped = DeserializeFromString<Account>(xml);
        Assert.Equal("correct horse battery staple", roundTripped.LetterboxdPassword);
        Assert.Equal("cf_clearance=abc123", roundTripped.RawCookies);
    }

    [Fact]
    public void Account_Deserialize_LegacyPlaintextXml_MigratesTransparently()
    {
        // Simulates a LetterboxdSync.xml written before this change: plaintext element content.
        const string legacyXml = """
            <?xml version="1.0" encoding="utf-16"?>
            <Account xmlns:xsi="http://www.w3.org/2001/XMLSchema-instance" xmlns:xsd="http://www.w3.org/2001/XMLSchema">
              <UserJellyfinId>user1</UserJellyfinId>
              <LetterboxdUsername>8bitproxy</LetterboxdUsername>
              <LetterboxdPassword>plaintext-legacy-password</LetterboxdPassword>
              <RawCookies>plaintext-legacy-cookie</RawCookies>
            </Account>
            """;

        var account = DeserializeFromString<Account>(legacyXml);

        Assert.Equal("plaintext-legacy-password", account.LetterboxdPassword);
        Assert.Equal("plaintext-legacy-cookie", account.RawCookies);

        // Re-serializing (as happens on the next SaveConfiguration) upgrades it to encrypted.
        var reSerialized = SerializeToString(account);
        Assert.DoesNotContain("plaintext-legacy-password", reSerialized);
        Assert.Contains("<LetterboxdPassword>enc:v1:", reSerialized);
    }

    [Fact]
    public void SerializdAccount_XmlRoundTrip_StoresPasswordEncrypted_ReadsBackPlaintext()
    {
        var account = new SerializdAccount
        {
            UserJellyfinId = "user1",
            Email = "8bitproxy@example.com",
            Password = "correct horse battery staple",
        };

        var xml = SerializeToString(account);

        Assert.DoesNotContain("correct horse battery staple", xml);
        Assert.Contains("<SerializdPassword>enc:v1:", xml);

        var roundTripped = DeserializeFromString<SerializdAccount>(xml);
        Assert.Equal("correct horse battery staple", roundTripped.Password);
    }

    [Fact]
    public void SerializdAccount_Deserialize_LegacyPlaintextXml_MigratesTransparently()
    {
        // Simulates a LetterboxdSync.xml written before encryption was added: plaintext element content.
        const string legacyXml = """
            <?xml version="1.0" encoding="utf-16"?>
            <SerializdAccount xmlns:xsi="http://www.w3.org/2001/XMLSchema-instance" xmlns:xsd="http://www.w3.org/2001/XMLSchema">
              <UserJellyfinId>user1</UserJellyfinId>
              <Email>8bitproxy@example.com</Email>
              <SerializdPassword>plaintext-legacy-password</SerializdPassword>
            </SerializdAccount>
            """;

        var account = DeserializeFromString<SerializdAccount>(legacyXml);

        Assert.Equal("plaintext-legacy-password", account.Password);

        // Re-serializing (as happens on the next SaveConfiguration) upgrades it to encrypted.
        var reSerialized = SerializeToString(account);
        Assert.DoesNotContain("plaintext-legacy-password", reSerialized);
        Assert.Contains("<SerializdPassword>enc:v1:", reSerialized);
    }

    [Fact]
    public void PluginConfiguration_XmlRoundTrip_StoresJellyseerrApiKeyEncrypted()
    {
        var config = new PluginConfiguration
        {
            JellyseerrUrl = "http://192.168.1.122:5055",
            JellyseerrApiKey = "MTIzNDU2Nzg5MA=="
        };

        var xml = SerializeToString(config);

        Assert.DoesNotContain("MTIzNDU2Nzg5MA==", xml);
        Assert.Contains("<JellyseerrApiKey>enc:v1:", xml);

        var roundTripped = DeserializeFromString<PluginConfiguration>(xml);
        Assert.Equal("MTIzNDU2Nzg5MA==", roundTripped.JellyseerrApiKey);
    }

    [Fact]
    public void ProtectedShadowProperties_AreNotSerializedToJson()
    {
        // configPage.js does getPluginConfiguration -> mutate -> updatePluginConfiguration,
        // echoing the full JSON payload back verbatim. If the encrypted shadow properties
        // leaked into that JSON, a stale echoed ciphertext could clobber a freshly-typed
        // plaintext value on save (see Account.LetterboxdPasswordProtected's doc comment).
        var account = new Account { LetterboxdPassword = "secret", RawCookies = "cookie" };
        var json = System.Text.Json.JsonSerializer.Serialize(account);

        Assert.DoesNotContain("LetterboxdPasswordProtected", json);
        Assert.DoesNotContain("RawCookiesProtected", json);
        Assert.Contains("\"LetterboxdPassword\":\"secret\"", json);

        var config = new PluginConfiguration { JellyseerrApiKey = "key" };
        var configJson = System.Text.Json.JsonSerializer.Serialize(config);
        Assert.DoesNotContain("JellyseerrApiKeyProtected", configJson);

        var serializdAccount = new SerializdAccount { Password = "serializd-secret" };
        var serializdJson = System.Text.Json.JsonSerializer.Serialize(serializdAccount);
        Assert.DoesNotContain("SerializdPasswordProtected", serializdJson);
        Assert.Contains("\"Password\":\"serializd-secret\"", serializdJson);
    }

    private static string SerializeToString<T>(T value)
    {
        var serializer = new XmlSerializer(typeof(T));
        using var writer = new StringWriter();
        serializer.Serialize(writer, value);
        return writer.ToString();
    }

    private static T DeserializeFromString<T>(string xml)
    {
        var serializer = new XmlSerializer(typeof(T));
        using var reader = new StringReader(xml);
        return (T)serializer.Deserialize(reader)!;
    }
}
