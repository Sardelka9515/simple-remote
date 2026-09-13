using System.Text.Json;
using SimpleRemote.Config;
using SimpleRemote.Pairing;
using SimpleRemote.Server;
using Xunit;

namespace SimpleRemote.Tests;

public class PairingTokenSourceTests
{
    [Fact]
    public void IssuedTokenRedeemsOnce()
    {
        var source = new PairingTokenSource();
        var token = source.Issue();

        Assert.True(source.TryRedeem(token));
        Assert.False(source.TryRedeem(token)); // single use
    }

    [Fact]
    public void WrongTokenIsRejected()
    {
        var source = new PairingTokenSource();
        source.Issue();

        Assert.False(source.TryRedeem("not-the-token"));
        Assert.False(source.TryRedeem(null));
        Assert.False(source.TryRedeem(""));
    }

    [Fact]
    public void IssuingAgainInvalidatesThePrevious()
    {
        var source = new PairingTokenSource();
        var first = source.Issue();
        var second = source.Issue();

        Assert.NotEqual(first, second);
        Assert.False(source.TryRedeem(first));
        Assert.True(source.TryRedeem(second));
    }

    [Fact]
    public void ExpiredTokenIsRejected()
    {
        var time = new FakeTime(DateTimeOffset.UnixEpoch);
        var source = new PairingTokenSource(time);
        var token = source.Issue();

        time.Now += PairingTokenSource.Lifetime + TimeSpan.FromSeconds(1);

        Assert.False(source.TryRedeem(token));
    }

    [Fact]
    public void TokenJustInsideLifetimeStillRedeems()
    {
        var time = new FakeTime(DateTimeOffset.UnixEpoch);
        var source = new PairingTokenSource(time);
        var token = source.Issue();

        time.Now += PairingTokenSource.Lifetime - TimeSpan.FromSeconds(1);

        Assert.True(source.TryRedeem(token));
    }

    [Fact]
    public void RevokeClearsTheToken()
    {
        var source = new PairingTokenSource();
        var token = source.Issue();
        source.Revoke();

        Assert.False(source.TryRedeem(token));
    }
}

public class PairRequestSerializationTests
{
    /// <summary>
    /// The wire shape the browser posts. If the naming policy and the DTO ever disagree, pairing
    /// fails with a 401 that looks exactly like a wrong token, so it is worth pinning down.
    /// </summary>
    [Fact]
    public void CamelCaseBodyBindsToPairingToken()
    {
        var request = JsonSerializer.Deserialize(
            """{"pairingToken":"abc123","name":"iPhone"}""", AppJson.Default.ApiPairRequest);

        Assert.NotNull(request);
        Assert.Equal("abc123", request.PairingToken);
        Assert.Equal("iPhone", request.Name);
    }

    [Fact]
    public void PairResponseSerializesAsCamelCase()
    {
        var json = JsonSerializer.Serialize(
            new ApiPairResponse { DeviceId = "d1", Token = "t1" }, AppJson.Default.ApiPairResponse);

        Assert.Contains("\"deviceId\"", json);
        Assert.Contains("\"token\"", json);
    }
}

public class DeviceStoreTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"sr-devices-{Guid.NewGuid():N}.json");

    [Fact]
    public void RegisteredDeviceVerifies()
    {
        var store = new DeviceStore(_path);
        var issued = store.Register("Test phone");

        Assert.NotNull(store.Verify(issued.DeviceId, issued.Token));
        Assert.Null(store.Verify(issued.DeviceId, "wrong"));
        Assert.Null(store.Verify("wrong", issued.Token));
        Assert.Null(store.Verify(null, null));
    }

    [Fact]
    public void RawTokenIsNeverPersisted()
    {
        var store = new DeviceStore(_path);
        var issued = store.Register("Test phone");
        store.Flush();

        var contents = File.ReadAllText(_path);
        Assert.DoesNotContain(issued.Token, contents);
    }

    [Fact]
    public void DevicesSurviveReload()
    {
        var issued = new DeviceStore(_path).Register("Test phone");

        var reloaded = new DeviceStore(_path);
        Assert.NotNull(reloaded.Verify(issued.DeviceId, issued.Token));
    }

    [Fact]
    public void RevokedDeviceStopsVerifying()
    {
        var store = new DeviceStore(_path);
        var issued = store.Register("Test phone");

        Assert.True(store.Revoke(issued.DeviceId));
        Assert.Null(store.Verify(issued.DeviceId, issued.Token));
        Assert.False(store.Revoke(issued.DeviceId)); // already gone
    }

    [Fact]
    public void CorruptFileDoesNotThrow()
    {
        File.WriteAllText(_path, "{ this is not json");

        var store = new DeviceStore(_path);
        Assert.Empty(store.Devices);
    }

    public void Dispose()
    {
        if (File.Exists(_path)) File.Delete(_path);
        GC.SuppressFinalize(this);
    }
}

internal sealed class FakeTime(DateTimeOffset start) : TimeProvider
{
    public DateTimeOffset Now { get; set; } = start;
    public override DateTimeOffset GetUtcNow() => Now;
}
