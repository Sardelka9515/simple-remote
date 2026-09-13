using System.Net.NetworkInformation;
using SimpleRemote.Input;
using SimpleRemote.Net;
using Xunit;

namespace SimpleRemote.Tests;

public class KeyComboParserTests
{
    private const ushort VkControl = 0x11;
    private const ushort VkAlt = 0x12;
    private const ushort VkShift = 0x10;
    private const ushort VkWin = 0x5B;

    [Fact]
    public void SingleLetterParses()
    {
        Assert.True(KeyComboParser.TryParse("a", out var combo));
        Assert.Empty(combo.Value.Modifiers);
        Assert.Equal('A', combo.Value.Key);
    }

    [Fact]
    public void ModifiersAreOrderedAsWritten()
    {
        Assert.True(KeyComboParser.TryParse("Ctrl+Shift+Esc", out var combo));
        Assert.Equal([VkControl, VkShift], combo.Value.Modifiers);
        Assert.Equal(0x1B, combo.Value.Key);
    }

    [Theory]
    [InlineData("Alt+F4", VkAlt, 0x73)]
    [InlineData("Win+D", VkWin, 'D')]
    [InlineData("ctrl+c", VkControl, 'C')]
    [InlineData("CONTROL+V", VkControl, 'V')]
    public void CommonCombosParse(string text, ushort modifier, int key)
    {
        Assert.True(KeyComboParser.TryParse(text, out var combo));
        Assert.Equal([modifier], combo.Value.Modifiers);
        Assert.Equal(key, combo.Value.Key);
    }

    [Fact]
    public void MediaKeysResolve()
    {
        Assert.True(KeyComboParser.TryParse("MediaPlayPause", out var combo));
        Assert.Equal(0xB3, combo.Value.Key);
    }

    [Fact]
    public void FunctionKeysCoverTheFullRange()
    {
        Assert.True(KeyComboParser.TryParse("F1", out var f1));
        Assert.Equal(0x70, f1.Value.Key);

        Assert.True(KeyComboParser.TryParse("F24", out var f24));
        Assert.Equal(0x87, f24.Value.Key);
    }

    [Fact]
    public void DuplicateModifiersCollapse()
    {
        // Left unhandled, this would emit an unbalanced down/down/up sequence.
        Assert.True(KeyComboParser.TryParse("Ctrl+Ctrl+A", out var combo));
        Assert.Equal([VkControl], combo.Value.Modifiers);
    }

    [Fact]
    public void WhitespaceAroundPartsIsTolerated()
    {
        Assert.True(KeyComboParser.TryParse(" Ctrl + Alt + Delete ", out var combo));
        Assert.Equal([VkControl, VkAlt], combo.Value.Modifiers);
        Assert.Equal(0x2E, combo.Value.Key);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("Ctrl")]           // modifiers alone are not a shortcut
    [InlineData("Ctrl+Shift")]
    [InlineData("Ctrl+A+B")]       // two terminal keys
    [InlineData("Ctrl+Nonsense")]
    [InlineData("+++")]
    public void InvalidInputIsRejectedWithoutThrowing(string? text)
    {
        Assert.False(KeyComboParser.TryParse(text, out var combo));
        Assert.Null(combo);
    }
}

public class LocalAddressesTests
{
    /// <summary>
    /// A Hyper-V or WSL adapter with a routable-looking address is the classic cause of a QR code
    /// that scans perfectly and then times out, so real adapters must always outrank them.
    /// </summary>
    [Theory]
    [InlineData("vEthernet (Default Switch)", "Hyper-V Virtual Ethernet Adapter")]
    [InlineData("vEthernet (WSL)", "Hyper-V Virtual Ethernet Adapter")]
    [InlineData("VirtualBox Host-Only Network", "VirtualBox Host-Only Ethernet Adapter")]
    [InlineData("Tailscale", "Tailscale Tunnel")]
    public void VirtualAdaptersRankBelowRealOnes(string name, string description)
    {
        var real = LocalAddresses.Rank("Wi-Fi", "Intel Wireless-AC 9560",
            NetworkInterfaceType.Wireless80211, hasGateway: true, "192.168.1.42");

        var virtualAdapter = LocalAddresses.Rank(name, description,
            NetworkInterfaceType.Ethernet, hasGateway: true, "192.168.1.42");

        Assert.True(virtualAdapter < real, $"{name} should rank below a real Wi-Fi adapter");
    }

    [Fact]
    public void WirelessOutranksEthernetAllElseEqual()
    {
        // The phone is on Wi-Fi, so the wireless subnet is the one it can actually reach.
        var wifi = LocalAddresses.Rank("Wi-Fi", "", NetworkInterfaceType.Wireless80211, true, "192.168.1.10");
        var ethernet = LocalAddresses.Rank("Ethernet", "", NetworkInterfaceType.Ethernet, true, "192.168.1.10");

        Assert.True(wifi > ethernet);
    }

    [Fact]
    public void GatewayPresenceRaisesRank()
    {
        var withGateway = LocalAddresses.Rank("Wi-Fi", "", NetworkInterfaceType.Wireless80211, true, "192.168.1.10");
        var without = LocalAddresses.Rank("Wi-Fi", "", NetworkInterfaceType.Wireless80211, false, "192.168.1.10");

        Assert.True(withGateway > without);
    }

    [Fact]
    public void PrivateRangesOutrankPublicAddresses()
    {
        var priv = LocalAddresses.Rank("Wi-Fi", "", NetworkInterfaceType.Wireless80211, true, "192.168.0.5");
        var pub = LocalAddresses.Rank("Wi-Fi", "", NetworkInterfaceType.Wireless80211, true, "203.0.113.5");

        Assert.True(priv > pub);
    }

    [Theory]
    [InlineData("172.16.0.5", true)]
    [InlineData("172.31.255.1", true)]
    [InlineData("172.15.0.5", false)]  // just outside the private block
    [InlineData("172.32.0.5", false)]
    public void PrivateBlockOf172IsBoundedCorrectly(string address, bool isPrivate)
    {
        var score = LocalAddresses.Rank("Wi-Fi", "", NetworkInterfaceType.Wireless80211, true, address);
        var publicScore = LocalAddresses.Rank("Wi-Fi", "", NetworkInterfaceType.Wireless80211, true, "203.0.113.5");

        Assert.Equal(isPrivate, score > publicScore);
    }

    [Fact]
    public void EnumerateNeverThrowsAndAlwaysGivesAnAnswer()
    {
        // Runs against real hardware: the contract is only that it does not throw.
        var all = LocalAddresses.Enumerate();
        Assert.NotNull(all);
        Assert.False(string.IsNullOrEmpty(LocalAddresses.Best()));
    }

    [Fact]
    public void BestHonoursAPinnedAddressOnlyIfItStillExists()
    {
        Assert.Equal(LocalAddresses.Best(), LocalAddresses.Best("10.99.99.99"));
    }
}
