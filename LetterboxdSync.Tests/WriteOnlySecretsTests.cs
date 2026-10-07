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
    private const string SeerrUrl = "http://seerr.local:5055";

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
        LetterboxdController.SeerrTestHandlerForTesting = null;
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

    /// <summary>What Jellyfin's MVC JSON formatter writes for a controller result (JsonDefaults.Options).</summary>
    private static string Json(object? value) => JsonSerializer.Serialize(value, JsonDefaults.Options);

    private static string Json(ActionResult result) => Json(Assert.IsType<OkObjectResult>(result).Value);

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
            Substitute.For<IUserManager>(), Substitute.For<ILibraryManager>());
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
        Assert.DoesNotContain("ClearJellyseerrApiKey", json);
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
        Assert.DoesNotContain("PasswordInput", xml);
        Assert.DoesNotContain("CookiesInput", xml);
        Assert.DoesNotContain("ApiKeyInput", xml);
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
        SignIn(h.Controller, UserId, admin: true);
        var seerr = new FakeSeerr(ApiKey);
        LetterboxdController.SeerrTestHandlerForTesting = seerr;

        var elsewhere = Assert.IsType<BadRequestObjectResult>(
            await h.Controller.TestJellyseerr(new JellyseerrTestRequest { Url = "https://seerr-elsewhere.example" }));
        Assert.Contains("URL and API key are required", Json(elsewhere.Value));
        Assert.Empty(seerr.Requests);

        var stored = Assert.IsType<OkObjectResult>(
            await h.Controller.TestJellyseerr(new JellyseerrTestRequest { Url = SeerrUrl + "/" }));
        Assert.Contains("\"linkedToCurrentUser\":true", Json(stored.Value));
        Assert.Equal(new (string, string?)[] { ("seerr.local", ApiKey) }, seerr.Requests);
    }

    [Fact]
    public void JellyfinConfigPost_ClearSeerrKey_DropsIt_ButATypedKeyWins()
    {
        using var h = new ControllerTestHarness(UserId);
        Seed(h.Config);

        var body = JsonNode.Parse(JellyfinGet(h.Config))!;
        body["ClearJellyseerrApiKey"] = true;
        JellyfinPost(body.ToJsonString());
        Assert.Null(h.Config.JellyseerrApiKey);

        body = JsonNode.Parse(JellyfinGet(h.Config))!;
        body["JellyseerrApiKey"] = "rotated-key";
        body["ClearJellyseerrApiKey"] = true;
        JellyfinPost(body.ToJsonString());
        Assert.Equal("rotated-key", h.Config.JellyseerrApiKey);
    }

    [Fact]
    public async Task TypedCookies_WinOverTheClearFlag_OnSaveAndVerify()
    {
        using var h = new ControllerTestHarness(UserId);
        Seed(h.Config);

        h.Controller.PutAccounts(new AccountsUpdateRequest
        {
            Accounts = { new AccountUpdateRequest { LetterboxdUsername = LbUser, RawCookies = "typed=1", ClearRawCookies = true } }
        });
        Assert.Equal("typed=1", h.Config.Accounts.Single().RawCookies);

        string? cookies = "unset";
        LetterboxdController.VerifyApiLoginForTesting = (_, _) => Task.FromException(new Exception("api down"));
        LetterboxdController.VerifyWebsiteLoginForTesting = (_, _, c, _) => { cookies = c; return Task.CompletedTask; };
        await h.Controller.VerifyLogin(new LetterboxdVerifyRequest { LetterboxdUsername = LbUser, RawCookies = "typed=2", ClearRawCookies = true });
        Assert.Equal("typed=2", cookies);
        await h.Controller.VerifyLogin(new LetterboxdVerifyRequest { LetterboxdUsername = LbUser, ClearRawCookies = true });
        Assert.Null(cookies);
    }

    [Theory]
    [InlineData("http://SEERR.local:5055/", true)]
    [InlineData("http://seerr.local:5055/Seerr", false)]
    [InlineData("http://seerr.local:5056", false)]
    [InlineData("https://seerr.local:5055", false)]
    [InlineData("not a url", false)]
    public void IsStoredUrl_MatchesSchemeHostPortCaseInsensitively(string url, bool expected)
        => Assert.Equal(expected, SecretMerge.IsStoredUrl(url, SeerrUrl));

    [Fact]
    public void IsStoredUrl_ComparesThePathExactly()
        => Assert.False(SecretMerge.IsStoredUrl("http://seerr.local:5055/seerr", "http://seerr.local:5055/Seerr"));

    [Fact]
    public async Task Verify_BlankPasswordWithNothingStored_IsRefused()
    {
        using var h = new ControllerTestHarness(UserId);
        var called = false;
        LetterboxdController.VerifyApiLoginForTesting = (_, _) => { called = true; return Task.CompletedTask; };
        SerializdController.VerifyOverrideForTesting = (_, _, _) => { called = true; return Task.FromResult<string?>("me"); };

        Assert.IsType<BadRequestObjectResult>(await h.Controller.VerifyLogin(new LetterboxdVerifyRequest { LetterboxdUsername = LbUser }));
        Assert.IsType<BadRequestObjectResult>(await SerializdControllerFor(UserId).Verify(new SerializdController.VerifyRequest { Email = SzEmail }));
        Assert.False(called);
    }

    // ----- Renames and owner moves carry the secrets with the account -----

    [Fact]
    public void JellyfinConfigPost_AdminMovesAccountToAnotherUser_SecretsFollow()
    {
        using var h = new ControllerTestHarness(UserId);
        Seed(h.Config);

        // What the admin dashboard sends after picking another Jellyfin user in the account modal.
        var body = JsonNode.Parse(JellyfinGet(h.Config))!;
        var lb = body["Accounts"]![0]!;
        lb["OriginalUserJellyfinId"] = UserId;
        lb["OriginalLetterboxdUsername"] = LbUser;
        lb["UserJellyfinId"] = OtherUserId;
        var sz = body["SerializdAccounts"]![0]!;
        sz["OriginalUserJellyfinId"] = UserId;
        sz["OriginalEmail"] = SzEmail;
        sz["UserJellyfinId"] = OtherUserId;
        JellyfinPost(body.ToJsonString());

        var account = h.Config.Accounts.Single();
        Assert.Equal(OtherUserId, account.UserJellyfinId);
        Assert.Equal(Password, account.LetterboxdPassword);
        Assert.Equal(Cookies, account.RawCookies);
        var serializd = h.Config.SerializdAccounts.Single();
        Assert.Equal(OtherUserId, serializd.UserJellyfinId);
        Assert.Equal(SzPassword, serializd.Password);
    }

    [Fact]
    public void JellyfinConfigPost_AdminMovesAndRenamesWhileAnotherUserHasThatLogin_TakesTheEditedAccountsSecrets()
    {
        using var h = new ControllerTestHarness(UserId);
        Seed(h.Config);
        h.Config.Accounts.Add(new Account { UserJellyfinId = OtherUserId, LetterboxdUsername = "renamed", LetterboxdPassword = "other-users-password" });

        var body = JsonNode.Parse(JellyfinGet(h.Config))!;
        var lb = body["Accounts"]![0]!;
        lb["OriginalUserJellyfinId"] = UserId;
        lb["OriginalLetterboxdUsername"] = LbUser;
        lb["UserJellyfinId"] = OtherUserId;
        lb["LetterboxdUsername"] = "Renamed-Too";
        JellyfinPost(body.ToJsonString());

        Assert.Equal(Password, h.Config.Accounts[0].LetterboxdPassword);
        Assert.Equal("other-users-password", h.Config.Accounts[1].LetterboxdPassword);
    }

    [Fact]
    public void JellyfinConfigPost_OriginalOwnerWithDashes_StillMatches()
    {
        using var h = new ControllerTestHarness(UserId);
        Seed(h.Config);

        var body = JsonNode.Parse(JellyfinGet(h.Config))!;
        body["Accounts"]![0]!["OriginalUserJellyfinId"] = Guid.ParseExact(UserId, "N").ToString("D");
        body["Accounts"]![0]!["OriginalLetterboxdUsername"] = LbUser;
        body["Accounts"]![0]!["UserJellyfinId"] = OtherUserId;
        JellyfinPost(body.ToJsonString());

        Assert.Equal(Password, h.Config.Accounts.Single().LetterboxdPassword);
    }

    [Fact]
    public void JellyfinConfigPost_OriginalMarkers_AreNeverStoredOrReturned()
    {
        using var h = new ControllerTestHarness(UserId);
        Seed(h.Config);

        var body = JsonNode.Parse(JellyfinGet(h.Config))!;
        body["Accounts"]![0]!["OriginalUserJellyfinId"] = UserId;
        body["Accounts"]![0]!["OriginalLetterboxdUsername"] = LbUser;
        body["SerializdAccounts"]![0]!["OriginalEmail"] = SzEmail;
        JellyfinPost(body.ToJsonString());

        var json = JellyfinGet(h.Config);
        Assert.DoesNotContain("Original", json);
        var serializer = new System.Xml.Serialization.XmlSerializer(typeof(PluginConfiguration));
        using var writer = new StringWriter();
        serializer.Serialize(writer, h.Config);
        Assert.DoesNotContain("Original", writer.ToString());
    }

    [Fact]
    public void PutAccounts_RenameWithBlankPassword_KeepsTheSecretsAndSettings()
    {
        using var h = new ControllerTestHarness(UserId);
        Seed(h.Config);
        h.Config.Accounts[0].ExcludedLibraryIds = new() { "0123456789abcdef0123456789abcdef" };

        h.Controller.PutAccounts(new AccountsUpdateRequest
        {
            Accounts = { new AccountUpdateRequest { LetterboxdUsername = "demo-cinephile", OriginalLetterboxdUsername = LbUser, Enabled = true } }
        });

        var account = h.Config.Accounts.Single();
        Assert.Equal("demo-cinephile", account.LetterboxdUsername);
        Assert.Equal(Password, account.LetterboxdPassword);
        Assert.Equal(Cookies, account.RawCookies);
        Assert.Equal(new[] { "0123456789abcdef0123456789abcdef" }, account.ExcludedLibraryIds);
    }

    [Fact]
    public void PutAccounts_OriginalNameOfAnotherUsersAccount_GetsNothing()
    {
        using var h = new ControllerTestHarness(UserId);
        Seed(h.Config, OtherUserId);

        h.Controller.PutAccounts(new AccountsUpdateRequest
        {
            Accounts = { new AccountUpdateRequest { LetterboxdUsername = "mine", OriginalLetterboxdUsername = LbUser } }
        });

        var mine = h.Config.Accounts.Single(a => a.UserJellyfinId == UserId);
        Assert.Equal(string.Empty, mine.LetterboxdPassword);
        Assert.Null(mine.RawCookies);
    }

    [Fact]
    public void SerializdPutAccounts_EmailChangeWithBlankPassword_KeepsThePasswordAndSettings()
    {
        using var h = new ControllerTestHarness(UserId);
        Seed(h.Config);
        h.Config.SerializdAccounts[0].WatchlistName = "Admin's name";
        h.Config.SerializdAccounts[0].ExcludedLibraryIds = new() { "0123456789abcdef0123456789abcdef" };

        SerializdControllerFor(UserId).PutAccounts(new SerializdController.AccountsUpdateRequest
        {
            Accounts = new() { new SerializdController.AccountItem { Email = "demo@example.com", OriginalEmail = SzEmail } }
        });

        var account = h.Config.SerializdAccounts.Single();
        Assert.Equal(SzPassword, account.Password);
        Assert.Equal("Admin's name", account.WatchlistName);
        Assert.Equal(new[] { "0123456789abcdef0123456789abcdef" }, account.ExcludedLibraryIds);
    }

    [Fact]
    public async Task VerifyLogin_RenamedInTheForm_UsesTheStoredAccountItStartedFrom()
    {
        using var h = new ControllerTestHarness(UserId);
        Seed(h.Config);
        string? user = null, password = null;
        LetterboxdController.VerifyApiLoginForTesting = (u, p) => { user = u; password = p; return Task.CompletedTask; };

        var result = await h.Controller.VerifyLogin(new LetterboxdVerifyRequest { LetterboxdUsername = "demo-cinephile", OriginalLetterboxdUsername = LbUser });

        Assert.IsType<OkObjectResult>(result);
        Assert.Equal(("demo-cinephile", Password), (user, password));
    }

    [Fact]
    public async Task SerializdVerify_AdminCheckingAnotherUsersAccount_UsesThatAccountsPassword()
    {
        using var h = new ControllerTestHarness(UserId);
        Seed(h.Config, OtherUserId);
        string? password = null;
        SerializdController.VerifyOverrideForTesting = (_, _, p) => { password = p; return Task.FromResult<string?>("me"); };
        var request = new SerializdController.VerifyRequest { Email = SzEmail, UserJellyfinId = OtherUserId };

        Assert.IsType<BadRequestObjectResult>(await SerializdControllerFor(UserId).Verify(request));
        Assert.Null(password);

        Assert.IsType<OkObjectResult>(await SerializdControllerFor(UserId, admin: true).Verify(request));
        Assert.Equal(SzPassword, password);
    }

    /// <summary>
    /// Seerr's user list: answers only to the API key it was set up with, as the real
    /// X-Api-Key check does (anything else gets 403). Records each request's host and key.
    /// </summary>
    private sealed class FakeSeerr : HttpMessageHandler
    {
        private readonly string _key;
        public FakeSeerr(string key) => _key = key;
        public System.Collections.Generic.List<(string Host, string? Key)> Requests { get; } = new();

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var key = request.Headers.TryGetValues("X-Api-Key", out var values) ? values.FirstOrDefault() : null;
            Requests.Add((request.RequestUri!.Host, key));
            if (key != _key)
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Forbidden));
            var body = "{\"pageInfo\":{\"pages\":1,\"results\":1},\"results\":[{\"id\":7,\"jellyfinUserId\":\"" + UserId + "\"}]}";
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body) });
        }
    }
}
