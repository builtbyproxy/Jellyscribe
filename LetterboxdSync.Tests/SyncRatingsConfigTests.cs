using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Xml.Serialization;
using LetterboxdSync.Api;
using LetterboxdSync.Configuration;
using Microsoft.AspNetCore.Mvc;
using Xunit;

namespace LetterboxdSync.Tests;

/// <summary>
/// Persistence contract for the per-account "Sync ratings to Letterboxd" toggle: on for configs
/// saved before it existed, and never reset by a client that does not send it.
/// </summary>
[Collection("Plugin")]
public class SyncRatingsConfigTests
{
    private const string UserId = "aabbccddeeff00112233445566778899";

    [Fact]
    public void Account_LegacyXmlWithoutElement_DefaultsOn()
    {
        const string legacyXml = """
            <?xml version="1.0" encoding="utf-16"?>
            <Account xmlns:xsi="http://www.w3.org/2001/XMLSchema-instance" xmlns:xsd="http://www.w3.org/2001/XMLSchema">
              <UserJellyfinId>user1</UserJellyfinId>
              <LetterboxdUsername>8bitproxy</LetterboxdUsername>
              <Enabled>true</Enabled>
            </Account>
            """;

        var serializer = new XmlSerializer(typeof(Account));
        using var reader = new StringReader(legacyXml);
        var account = (Account)serializer.Deserialize(reader)!;

        Assert.True(account.SyncRatings);
    }

    [Fact]
    public void Account_XmlRoundTrip_KeepsOff()
    {
        var serializer = new XmlSerializer(typeof(Account));
        using var writer = new StringWriter();
        serializer.Serialize(writer, new Account { LetterboxdUsername = "8bitproxy", SyncRatings = false });
        using var reader = new StringReader(writer.ToString());

        Assert.False(((Account)serializer.Deserialize(reader)!).SyncRatings);
    }

    [Fact]
    public void PutAccounts_FieldOmitted_KeepsStoredOff_AndNewAccountDefaultsOn()
    {
        using var h = new ControllerTestHarness(UserId);
        h.AddAccount(UserId, "existing").SyncRatings = false;

        h.Controller.PutAccounts(new AccountsUpdateRequest
        {
            Accounts = new List<AccountUpdateRequest>
            {
                new() { LetterboxdUsername = "Existing", LetterboxdPassword = "pw", Enabled = true },
                new() { LetterboxdUsername = "brand-new", LetterboxdPassword = "pw", Enabled = true }
            }
        });

        var mine = h.Config.Accounts.Where(a => a.UserJellyfinId == UserId).ToList();
        Assert.False(mine.Single(a => a.LetterboxdUsername == "Existing").SyncRatings);
        Assert.True(mine.Single(a => a.LetterboxdUsername == "brand-new").SyncRatings);
    }

    [Fact]
    public void PutAccounts_ExplicitValue_IsApplied()
    {
        using var h = new ControllerTestHarness(UserId);
        h.AddAccount(UserId, "existing");

        h.Controller.PutAccounts(new AccountsUpdateRequest
        {
            Accounts = new List<AccountUpdateRequest>
            {
                new() { LetterboxdUsername = "existing", LetterboxdPassword = "pw", Enabled = true, SyncRatings = false }
            }
        });

        Assert.False(h.Config.Accounts.Single(a => a.UserJellyfinId == UserId).SyncRatings);
    }

    [Fact]
    public void GetAccounts_CarriesSyncRatings()
    {
        using var h = new ControllerTestHarness(UserId);
        h.AddAccount(UserId, "existing").SyncRatings = false;

        var ok = Assert.IsType<OkObjectResult>(h.Controller.GetAccounts());
        var accounts = (IEnumerable)ok.Value!.GetType()
            .GetProperty("accounts", BindingFlags.Public | BindingFlags.Instance)!.GetValue(ok.Value)!;
        var account = accounts.Cast<object>().Single();

        Assert.False((bool)account.GetType().GetProperty("syncRatings")!.GetValue(account)!);
    }
}
