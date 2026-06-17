using System;
using System.Buffers.Binary;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Threading.Tasks;
using linker.stun;

var tests = new (string Name, Func<Task> Body)[]
{
    ("binding request encodes change request flags", BindingRequestEncodesChangeRequestFlags),
    ("xor mapped address round trips IPv4", XorMappedAddressRoundTripsIpv4),
    ("xor mapped address round trips IPv6", XorMappedAddressRoundTripsIpv6),
    ("error response parses code and reason", ErrorResponseParsesCodeAndReason),
    ("binding client parses loopback response", BindingClientParsesLoopbackResponse),
    ("behavior discovery reports RFC5780 unsupported without other address", BehaviorDiscoveryReportsUnsupportedWithoutOtherAddress),
    ("p2p estimate treats IPv6 as public endpoint", P2PEstimateTreatsIpv6AsPublicEndpoint),
    ("p2p estimate scores known NAT behavior", P2PEstimateScoresKnownNatBehavior),
    ("p2p summary formats behavior address family and rate", P2PSummaryFormatsBehaviorAddressFamilyAndRate)
};

var failed = 0;
foreach (var test in tests)
{
    try
    {
        await test.Body();
        Console.WriteLine($"[PASS] {test.Name}");
    }
    catch (Exception ex)
    {
        failed++;
        Console.Error.WriteLine($"[FAIL] {test.Name}: {ex}");
    }
}

if (failed > 0)
{
    Environment.ExitCode = 1;
}

static Task BindingRequestEncodesChangeRequestFlags()
{
    Span<byte> transactionId = stackalloc byte[StunConstants.TransactionIdLength];
    for (var i = 0; i < transactionId.Length; i++)
    {
        transactionId[i] = (byte)(i + 1);
    }

    Span<byte> packet = stackalloc byte[256];
    var length = StunMessageCodec.WriteBindingRequest(packet, transactionId, StunChangeRequest.ChangeIpAndPort, software: null);

    Assert(StunMessageCodec.TryParse(packet[..length], out var message, out var error), error ?? "parse failed");
    Assert(message is not null, "message was null");
    Assert(message!.MessageType == StunConstants.BindingRequest, "unexpected message type");
    var changeRequest = message.Attributes.FirstOrDefault(attribute => attribute.Type == StunConstants.AttributeChangeRequest);
    Assert(changeRequest is not null, "CHANGE-REQUEST was not encoded");
    Assert(changeRequest!.Value.Length == 4, "CHANGE-REQUEST length mismatch");
    Assert(BinaryPrimitives.ReadUInt32BigEndian(changeRequest.Value) == (uint)StunChangeRequest.ChangeIpAndPort, "CHANGE-REQUEST flags mismatch");
    return Task.CompletedTask;
}

static Task XorMappedAddressRoundTripsIpv4()
{
    var endpoint = new IPEndPoint(IPAddress.Parse("203.0.113.7"), 54321);
    var parsed = RoundTripXorMapped(endpoint);
    Assert(StunEndpointEquals(endpoint, parsed), $"expected {endpoint}, got {parsed}");
    return Task.CompletedTask;
}

static Task XorMappedAddressRoundTripsIpv6()
{
    var endpoint = new IPEndPoint(IPAddress.Parse("2001:db8::1234"), 45678);
    var parsed = RoundTripXorMapped(endpoint);
    Assert(StunEndpointEquals(endpoint, parsed), $"expected {endpoint}, got {parsed}");
    return Task.CompletedTask;
}

static Task ErrorResponseParsesCodeAndReason()
{
    Span<byte> transactionId = stackalloc byte[StunConstants.TransactionIdLength];
    Span<byte> packet = stackalloc byte[256];
    var length = StunMessageCodec.WriteBindingErrorResponse(packet, transactionId, 420, "Unknown Attribute");

    Assert(StunMessageCodec.TryParse(packet[..length], out var message, out var error), error ?? "parse failed");
    Assert(message is not null, "message was null");
    Assert(message!.MessageType == StunConstants.BindingErrorResponse, "unexpected message type");
    Assert(message.Error is not null, "error was not parsed");
    Assert(message.Error!.Code == 420, "error code mismatch");
    Assert(message.Error.Reason == "Unknown Attribute", "error reason mismatch");
    return Task.CompletedTask;
}

static async Task BindingClientParsesLoopbackResponse()
{
    using var server = new LoopbackStunServer(includeOtherAddress: false);
    await server.StartAsync();

    var client = new StunClient();
    var result = await client.QueryBindingAsync(
        "127.0.0.1",
        server.Port,
        new StunClientOptions
        {
            AddressFamilyMode = StunAddressFamilyMode.Ipv4Only,
            MaxAttempts = 1,
            InitialRto = TimeSpan.FromSeconds(1),
            Software = null
        });

    Assert(result.Status == StunBindingStatus.Success, result.Message ?? "binding failed");
    Assert(result.ReflexiveEndPoint is not null, "missing reflexive endpoint");
    Assert(result.ReflexiveEndPoint!.Address.Equals(IPAddress.Loopback), "unexpected reflexive address");
    Assert(result.ReflexiveEndPoint.Port > 0, "unexpected reflexive port");
    await server.WaitAsync();
}

static async Task BehaviorDiscoveryReportsUnsupportedWithoutOtherAddress()
{
    using var server = new LoopbackStunServer(includeOtherAddress: false);
    await server.StartAsync();

    var client = new StunClient();
    var result = await client.DiscoverNatBehaviorAsync(
        "127.0.0.1",
        server.Port,
        new StunClientOptions
        {
            AddressFamilyMode = StunAddressFamilyMode.Ipv4Only,
            MaxAttempts = 1,
            InitialRto = TimeSpan.FromSeconds(1),
            Software = null
        });

    Assert(result.Status == StunNatBehaviorStatus.Rfc5780NotSupported, result.Message ?? "unexpected behavior status");
    Assert(result.Binding.Status == StunBindingStatus.Success, "binding should have succeeded");
    Assert(result.Binding.OtherAddress is null, "server should not have reported OTHER-ADDRESS");
    await server.WaitAsync();
}

static Task P2PEstimateTreatsIpv6AsPublicEndpoint()
{
    var result = NewBehaviorResult(
        new IPEndPoint(IPAddress.Parse("2001:db8::1"), 50000),
        StunNatMappingBehavior.Unknown,
        StunNatFilteringBehavior.Unknown);

    Assert(result.EstimatedP2PSuccessRate == 100, "IPv6 endpoint should estimate 100% P2P success.");
    Assert(result.EstimatedP2PSuccessReason is not null, "IPv6 endpoint should include an estimate reason.");
    return Task.CompletedTask;
}

static Task P2PEstimateScoresKnownNatBehavior()
{
    var result = NewBehaviorResult(
        new IPEndPoint(IPAddress.Parse("203.0.113.7"), 50000),
        StunNatMappingBehavior.EndpointIndependent,
        StunNatFilteringBehavior.AddressAndPortDependent);

    Assert(result.EstimatedP2PSuccessRate == 70, "Unexpected P2P estimate for endpoint-independent mapping and address-port filtering.");
    Assert(result.EstimatedP2PSuccessReason is not null, "Known behavior should include an estimate reason.");
    return Task.CompletedTask;
}

static Task P2PSummaryFormatsBehaviorAddressFamilyAndRate()
{
    var result = NewBehaviorResult(
        new IPEndPoint(IPAddress.Parse("203.0.113.7"), 50000),
        StunNatMappingBehavior.EndpointIndependent,
        StunNatFilteringBehavior.AddressAndPortDependent);

    Assert(result.P2PSummary == "EndpointIndependent/AddressAndPortDependent/IPV4-70%", $"Unexpected summary: {result.P2PSummary}");
    return Task.CompletedTask;
}

static IPEndPoint RoundTripXorMapped(IPEndPoint endpoint)
{
    Span<byte> transactionId = stackalloc byte[StunConstants.TransactionIdLength];
    for (var i = 0; i < transactionId.Length; i++)
    {
        transactionId[i] = (byte)(255 - i);
    }

    Span<byte> packet = stackalloc byte[512];
    var length = StunMessageCodec.WriteBindingSuccessResponse(packet, transactionId, endpoint, software: null);
    Assert(StunMessageCodec.TryParse(packet[..length], out var message, out var error), error ?? "parse failed");
    Assert(message is not null, "message was null");
    Assert(message!.ReflexiveEndPoint is not null, "reflexive endpoint was not parsed");
    return message.ReflexiveEndPoint!;
}

static bool StunEndpointEquals(IPEndPoint left, IPEndPoint right)
{
    return left.Port == right.Port && left.Address.Equals(right.Address);
}

static StunNatBehaviorResult NewBehaviorResult(
    IPEndPoint reflexiveEndPoint,
    StunNatMappingBehavior mappingBehavior,
    StunNatFilteringBehavior filteringBehavior)
{
    var binding = new StunBindingResult(
        StunBindingStatus.Success,
        "test",
        new IPEndPoint(IPAddress.Parse("192.0.2.1"), 3478),
        new IPEndPoint(IPAddress.Parse("192.0.2.100"), 50000),
        reflexiveEndPoint,
        null,
        null,
        null,
        null,
        TimeSpan.FromMilliseconds(1),
        1,
        null);

    return new StunNatBehaviorResult(
        StunNatBehaviorStatus.Success,
        binding,
        mappingBehavior,
        filteringBehavior,
        null,
        null,
        null,
        null,
        null);
}

static void Assert(bool condition, string message)
{
    if (!condition)
    {
        throw new InvalidOperationException(message);
    }
}

internal sealed class LoopbackStunServer : IDisposable
{
    private readonly bool _includeOtherAddress;
    private readonly Socket _socket;
    private Task? _task;

    public LoopbackStunServer(bool includeOtherAddress)
    {
        _includeOtherAddress = includeOtherAddress;
        _socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        _socket.Bind(new IPEndPoint(IPAddress.Loopback, 0));
    }

    public int Port => ((IPEndPoint)_socket.LocalEndPoint!).Port;

    public Task StartAsync()
    {
        _task = RunAsync();
        return Task.CompletedTask;
    }

    public async Task WaitAsync()
    {
        if (_task is not null)
        {
            await _task;
        }
    }

    public void Dispose()
    {
        _socket.Dispose();
    }

    private async Task RunAsync()
    {
        var receiveBuffer = new byte[1024];
        var responseBuffer = new byte[1024];
        EndPoint any = new IPEndPoint(IPAddress.Any, 0);
        var received = await _socket.ReceiveFromAsync(receiveBuffer, SocketFlags.None, any);
        if (!StunMessageCodec.TryParse(receiveBuffer.AsSpan(0, received.ReceivedBytes), out var request, out var error))
        {
            throw new InvalidOperationException(error ?? "server parse failed");
        }

        if (request is null)
        {
            throw new InvalidOperationException("request was null");
        }

        var remote = (IPEndPoint)received.RemoteEndPoint;
        var local = (IPEndPoint)_socket.LocalEndPoint!;
        var other = _includeOtherAddress ? new IPEndPoint(IPAddress.Loopback, local.Port + 1) : null;
        var responseLength = StunMessageCodec.WriteBindingSuccessResponse(
            responseBuffer,
            request!.TransactionId,
            remote,
            responseOrigin: local,
            otherAddress: other,
            software: null);

        await _socket.SendToAsync(responseBuffer.AsMemory(0, responseLength), SocketFlags.None, remote);
    }
}
