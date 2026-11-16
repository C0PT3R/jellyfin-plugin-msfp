using System;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using Jellyfin.Plugin.Msfp.Federation;
using Jellyfin.Plugin.Msfp.Protocol;
using Xunit;

namespace Jellyfin.Plugin.Msfp.Tests;

/// <summary>
/// Pairs with a real Jellyfin running this plugin, over real HTTP. Runs only
/// when MSFP_LIVE_CODE (a code that Jellyfin made) and MSFP_LIVE_TOKEN (a
/// Jellyfin administrator's access token) are set; passes otherwise.
/// </summary>
public class LiveJellyfinTests
{
    [Fact]
    public async Task PairsAcceptsAndEndsWithALiveJellyfin()
    {
        var text = Environment.GetEnvironmentVariable("MSFP_LIVE_CODE");
        var token = Environment.GetEnvironmentVariable("MSFP_LIVE_TOKEN");
        if (string.IsNullOrEmpty(text) || string.IsNullOrEmpty(token))
        {
            return;
        }

        Assert.True(PairingCode.TryParse(text, out var code));
        var b = new FederationService(ServerKey.Generate(), new PeerStore(null), TimeProvider.System,
            () => new ServerSettings("http://192.0.2.10:8096", "Live test B"), new DefaultTransport());

        // Requests dated the second B's memory began would be refused by B;
        // A's memory began long ago. Nothing to wait for.
        var redeemed = await b.RedeemAsync(text);
        Assert.True(redeemed.Ok, redeemed.Message);
        Assert.Equal(PeerState.Theirs, redeemed.Peer!.State);

        using var admin = new HttpClient { BaseAddress = new Uri(code.Address) };
        admin.DefaultRequestHeaders.Add("Authorization", $"MediaBrowser Token=\"{token}\"");
        var peers = JsonDocument.Parse(await admin.GetStringAsync("/Msfp/Peers")).RootElement;
        var me = peers.EnumerateArray().Single(p => p.GetProperty("key").GetString() == b.Key.PublicKeyText);
        Assert.Equal("ours", me.GetProperty("state").GetString());
        Assert.Equal("Live test B", me.GetProperty("name").GetString());
        Assert.Equal(b.Key.Fingerprint, me.GetProperty("fingerprint").GetString());

        // A's operator accepts. Its hello can't reach B (192.0.2.10 is a
        // documentation address), so B learns at its own next hello.
        var accept = await admin.PostAsync($"/Msfp/Peers/{b.Key.PublicKeyText}/Accept", null);
        Assert.True(accept.IsSuccessStatusCode);
        var afterHello = await b.HelloAsync(Encodings.Base64Url(code.PublicKey));
        Assert.Equal(PeerState.Paired, afterHello!.State);

        // B ends the pairing: A deletes B.
        Assert.True(await b.EndAsync(Encodings.Base64Url(code.PublicKey)));
        peers = JsonDocument.Parse(await admin.GetStringAsync("/Msfp/Peers")).RootElement;
        Assert.DoesNotContain(peers.EnumerateArray(), p => p.GetProperty("key").GetString() == b.Key.PublicKeyText);
    }
}
