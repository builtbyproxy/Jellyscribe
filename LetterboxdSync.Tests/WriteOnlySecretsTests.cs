using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Security.Claims;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Extensions.Json;
using LetterboxdSync.Api;
using LetterboxdSync.Configuration;
using LetterboxdSync.Security;
using LetterboxdSync.Serializd;
using MediaBrowser.Controller.Collections;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Playlists;
using MediaBrowser.Model.Plugins;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Xunit;

namespace LetterboxdSync.Tests;

/// <summary>
/// Stored secrets are write-only: no response (the plugin's own endpoints or Jellyfin's plugin
/// configuration GET) carries a password, cookie string or Seerr key; an empty field
/// on save keeps the stored value, a new one replaces it, and the clear flag drops cookies.
/// </summary>
[Collection("Plugin")]
public class WriteOnlySecretsTests : IDisposable
{
    private const string UserId = "aabbccddeeff00112233445566778899";
    private const string OtherUserId = "99887766554433221100ffeeddccbbaa";
    private const string LbUser = "8bitproxy";
    private const string Password = "hunter2-letterboxd";
    private const string Cookies = "cf_clearance=abc123; letterboxd.user.CURRENT=xyz";
    private const string SzEmail = "me@example.com";
    private const string SzPassword = "hunter2-serializd";
    private const string ApiKey = "seerr-api-key-123";
    private const string SeerrUrl = "http://127.0.0.1:1";

    private static readonly string[] Secrets = { Password, Cookies, SzPassword, ApiKey };

    private readonly string _keyDir;

    public WriteOnlySecretsTests()
    {
        _keyDir = Path.Combine(Path.GetTempPath(), "lbs-wos-" + Guid.NewGuid().ToString("N"));
        SecretProtector.KeyDirectoryOverride = _keyDir;
        SecretProtector.ResetForTesting();
    }

    public void Dispose()
    {
        LetterboxdController.VerifyApiLoginForTesting = null;
        LetterboxdController.VerifyWebsiteLoginForTesting = null;
        LetterboxdServiceFactory.OverrideForTesting = null;
        SerializdController.VerifyOverrideForTesting = null;
        SecretProtector.KeyDirectoryOverride = null;
        SecretProtector.ResetForTesting();
        try { if (Directory.Exists(_keyDir)) Directory.Delete(_keyDir, true); } catch { }
    }

    private static void Seed(PluginConfiguration config, string owner = UserId)
    {
        config.Accounts.Add(new Account
        {
            UserJellyfinId = owner,
            LetterboxdUsername = LbUser,
            LetterboxdPassword = Password,
            RawCookies = Cookies,
            Enabled = true
        });
        config.SerializdAccounts.Add(new SerializdAccount
        {
            UserJellyfinId = owner,
            Email = SzEmail,
            Password = SzPassword,
            Enabled = true
        });
        config.JellyseerrUrl = SeerrUrl;
        config.JellyseerrApiKey = ApiKey;
    }

    /// <summary>
    /// What Jellyfin's GET /Plugins/{id}/Configuration returns: an ActionResult&lt;BasePluginConfiguration&gt;
    /// written by MVC's System.Text.Json formatter (runtime type, Jellyfin's PascalCase options).
    /// </summary>
    private static string JellyfinGet(BasePluginConfiguration config)
        => JsonSerializer.Serialize(config, config.GetType(), JsonDefaults.PascalCaseOptions);

    /// <summary>What Jellyfin's POST does: deserialize a fresh object with JsonDefaults.Options, then UpdateConfiguration.</summary>
    private static void JellyfinPost(string json)
    {
        var incoming = (BasePluginConfiguration)JsonSerializer.Deserialize(json, typeof(PluginConfiguration), JsonDefaults.Options)!;
        Plugin.Instance!.UpdateConfiguration(incoming);
    }

    private static void AssertNoSecret(string json)
    {
        foreach (var secret in Secrets)
            Assert.DoesNotContain(secret, json);
    }

    private static string Json(ActionResult result) => JsonSerializer.Serialize(Assert.IsType<OkObjectResult>(result).Value);

    private static void SignIn(ControllerBase controller, string userId, bool admin = false)
    {
        var claims = new[] { new Claim("Jellyfin-UserId", userId) }.ToList();
        if (admin) claims.Add(new Claim(ClaimTypes.Role, "Administrator"));
        controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext { User = new ClaimsPrincipal(new ClaimsIdentity(claims, "Test")) },
            RouteData = new RouteData()
        };
    }

    private static SerializdController SerializdControllerFor(string userId, bool admin = false)
    {
        var controller = new SerializdController(NullLogger<SerializdController>.Instance,
            new SerializdSyncRunner(NullLoggerFactory.Instance, Substitute.For<ILibraryManager>(),
                Substitute.For<IUserManager>(), Substitute.For<IUserDataManager>()),
            new SerializdWatchlistSyncRunner(NullLoggerFactory.Instance, Substitute.For<ILibraryManager>(),
                Substitute.For<IUserManager>(), Substitute.For<ICollectionManager>(), Substitute.For<IPlaylistManager>()),
            Substitute.For<IUserManager>());
        SignIn(controller, userId, admin);
        return controller;
    }

    // ----- Jellyfin's plugin configuration endpoint -----

    [Fact]
    public void JellyfinConfigGet_ContainsNoSecret_ButSaysWhichAreSaved()
    {
        using var h = new ControllerTestHarness(UserId);
        Seed(h.Config);

        var json = JellyfinGet(h.Config);

        AssertNoSecret(json);
        Assert.Contains("\"HasPassword\":true", json);
        Assert.Contains("\"HasRawCookies\":true", json);
        Assert.Contains("\"HasJellyseerrApiKey\":true", json);
        Assert.DoesNotContain("ClearRawCookies", json);
        AssertNoSecret(JsonSerializer.Serialize(h.Config, h.Config.GetType(), JsonDefaults.Options));
    }

    [Fact]
    public void JellyfinConfigRoundTrip_KeepsEverySecret()
    {
        using var h = new ControllerTestHarness(UserId);
        Seed(h.Config);

        JellyfinPost(JellyfinGet(h.Config));

        var account = h.Config.Accounts.Single();
        Assert.Equal(Password, account.LetterboxdPassword);
        Assert.Equal(Cookies, account.RawCookies);
        Assert.Equal(SzPassword, h.Config.SerializdAccounts.Single().Password);
        Assert.Equal(ApiKey, h.Config.JellyseerrApiKey);
    }

    [Fact]
    public void JellyfinConfigPost_NewValuesReplace_AndClearFlagsClear()
    {
        using var h = new ControllerTestHarness(UserId);
        Seed(h.Config);

        var body = JsonNode.Parse(JellyfinGet(h.Config))!;
        body["Accounts"]![0]!["LetterboxdPassword"] = "new-lb-password";
        body["Accounts"]![0]!["ClearRawCookies"] = true;
        body["SerializdAccounts"]![0]!["Password"] = "new-sz-password";
        body["JellyseerrApiKey"] = "new-seerr-key";
        JellyfinPost(body.ToJsonString());

        var account = h.Config.Accounts.Single();
        Assert.Equal("new-lb-password", account.LetterboxdPassword);
        Assert.Null(account.RawCookies);
        Assert.Equal("new-sz-password", h.Config.SerializdAccounts.Single().Password);
        Assert.Equal("new-seerr-key", h.Config.JellyseerrApiKey);
    }

    [Fact]
    public void JellyfinConfigPost_RenamedLogin_DoesNotInheritTheOldSecrets()
    {
        using var h = new ControllerTestHarness(UserId);
        Seed(h.Config);

        var body = JsonNode.Parse(JellyfinGet(h.Config))!;
        body["Accounts"]![0]!["LetterboxdUsername"] = "someone-else";
        body["SerializdAccounts"]![0]!["Email"] = "other@example.com";
        JellyfinPost(body.ToJsonString());

        Assert.Equal(string.Empty, h.Config.Accounts.Single().LetterboxdPassword);
        Assert.Null(h.Config.Accounts.Single().RawCookies);
        Assert.Equal(string.Empty, h.Config.SerializdAccounts.Single().Password);
    }

    [Fact]
    public void LegacyPlaintextJsonPost_StillSetsTheSecrets()
    {
        using var h = new ControllerTestHarness(UserId);

        JellyfinPost("{\"Accounts\":[{\"UserJellyfinId\":\"" + UserId + "\",\"LetterboxdUsername\":\"" + LbUser +
            "\",\"LetterboxdPassword\":\"" + Password + "\",\"RawCookies\":\"" + Cookies + "\"}]}");

        Assert.Equal(Password, h.Config.Accounts.Single().LetterboxdPassword);
        Assert.Equal(Cookies, h.Config.Accounts.Single().RawCookies);
    }

    [Fact]
    public void XmlOnDisk_StaysEncrypted_AndHasNoWriteOnlyHelpers()
    {
        var config = new PluginConfiguration();
        Seed(config);

        var serializer = new System.Xml.Serialization.XmlSerializer(typeof(PluginConfiguration));
        using var writer = new StringWriter();
        serializer.Serialize(writer, config);
        var xml = writer.ToString();

        AssertNoSecret(xml);
        Assert.DoesNotContain("Input", xml);
        Assert.DoesNotContain("<Has", xml);
        using var reader = new StringReader(xml);
        var loaded = (PluginConfiguration)serializer.Deserialize(reader)!;
        Assert.Equal(Password, loaded.Accounts.Single().LetterboxdPassword);
    }

    // ----- The plugin's own endpoints -----

    [Fact]
    public void UserGetEndpoints_ContainNoSecret()
    {
        using var h = new ControllerTestHarness(UserId);
        Seed(h.Config);

        var accounts = Json(h.Controller.GetAccounts());
        var serializd = Json(SerializdControllerFor(UserId).GetAccounts());

        foreach (var json in new[] { accounts, serializd })
        {
            AssertNoSecret(json);
            Assert.Contains("\"hasPassword\":true", json);
        }
        Assert.Contains("\"hasCookies\":true", accounts);
    }

    [Fact]
    public void PutAccounts_EmptyKeeps_NewReplaces_ClearClears()
    {
        using var h = new ControllerTestHarness(UserId);
        Seed(h.Config);

        h.Controller.PutAccounts(new AccountsUpdateRequest { Accounts = { new AccountUpdateRequest { LetterboxdUsername = LbUser, Enabled = true } } });
        Assert.Equal(Password, h.Config.Accounts.Single().LetterboxdPassword);
        Assert.Equal(Cookies, h.Config.Accounts.Single().RawCookies);

        h.Controller.PutAccounts(new AccountsUpdateRequest
        {
            Accounts = { new AccountUpdateRequest { LetterboxdUsername = LbUser, LetterboxdPassword = "new", RawCookies = "new-cookies" } }
        });
        Assert.Equal("new", h.Config.Accounts.Single().LetterboxdPassword);
        Assert.Equal("new-cookies", h.Config.Accounts.Single().RawCookies);

        h.Controller.PutAccounts(new AccountsUpdateRequest { Accounts = { new AccountUpdateRequest { LetterboxdUsername = LbUser, ClearRawCookies = true } } });
        Assert.Equal("new", h.Config.Accounts.Single().LetterboxdPassword);
        Assert.Null(h.Config.Accounts.Single().RawCookies);
    }

    [Fact]
    public void SerializdPutAccounts_EmptyKeeps_NewReplaces()
    {
        using var h = new ControllerTestHarness(UserId);
        Seed(h.Config);
        var controller = SerializdControllerFor(UserId);

        controller.PutAccounts(new SerializdController.AccountsUpdateRequest { Accounts = new() { new SerializdController.AccountItem { Email = SzEmail } } });
        Assert.Equal(SzPassword, h.Config.SerializdAccounts.Single().Password);

        controller.PutAccounts(new SerializdController.AccountsUpdateRequest { Accounts = new() { new SerializdController.AccountItem { Email = SzEmail, Password = "new" } } });
        Assert.Equal("new", h.Config.SerializdAccounts.Single().Password);
    }

    // ----- Verify / test flows fall back to the stored secret -----

    [Fact]
    public async Task VerifyLogin_EmptyFields_UseTheStoredSecrets()
    {
        using var h = new ControllerTestHarness(UserId);
        Seed(h.Config);
        string? password = null, cookies = null;
        LetterboxdController.VerifyApiLoginForTesting = (_, _) => Task.FromException(new Exception("api down"));
        LetterboxdController.VerifyWebsiteLoginForTesting = (_, p, c, _) => { password = p; cookies = c; return Task.CompletedTask; };

        var result = await h.Controller.VerifyLogin(new LetterboxdVerifyRequest { LetterboxdUsername = LbUser });

        Assert.IsType<OkObjectResult>(result);
        Assert.Equal((Password, Cookies), (password, cookies));
    }

    [Fact]
    public async Task VerifyLogin_OtherUsersStoredAccount_OnlyForAdmins()
    {
        using var h = new ControllerTestHarness(UserId);
        Seed(h.Config, OtherUserId);
        string? password = null;
        LetterboxdController.VerifyApiLoginForTesting = (_, p) => { password = p; return Task.CompletedTask; };
        var request = new LetterboxdVerifyRequest { LetterboxdUsername = LbUser, UserJellyfinId = OtherUserId };

        Assert.IsType<BadRequestObjectResult>(await h.Controller.VerifyLogin(request));
        Assert.Null(password);

        SignIn(h.Controller, UserId, admin: true);
        Assert.IsType<OkObjectResult>(await h.Controller.VerifyLogin(request));
        Assert.Equal(Password, password);
    }

    [Fact]
    public async Task SerializdVerify_EmptyPassword_UsesTheStoredOne()
    {
        using var h = new ControllerTestHarness(UserId);
        Seed(h.Config);
        string? password = null;
        SerializdController.VerifyOverrideForTesting = (_, _, p) => { password = p; return Task.FromResult<string?>("me"); };

        var result = await SerializdControllerFor(UserId).Verify(new SerializdController.VerifyRequest { Email = SzEmail });

        Assert.IsType<OkObjectResult>(result);
        Assert.Equal(SzPassword, password);
    }

    [Fact]
    public async Task TestJellyseerr_StoredKey_OnlyForTheStoredUrl()
    {
        using var h = new ControllerTestHarness(UserId);
        Seed(h.Config);

        var elsewhere = Assert.IsType<BadRequestObjectResult>(
            await h.Controller.TestJellyseerr(new JellyseerrTestRequest { Url = "https://attacker.example" }));
        Assert.Contains("URL and API key are required", JsonSerializer.Serialize(elsewhere.Value));

        // The stored URL refuses connections, so getting that far proves the stored key was used.
        var stored = Assert.IsType<BadRequestObjectResult>(
            await h.Controller.TestJellyseerr(new JellyseerrTestRequest { Url = SeerrUrl + "/" }));
        Assert.DoesNotContain("URL and API key are required", JsonSerializer.Serialize(stored.Value));
    }
}
