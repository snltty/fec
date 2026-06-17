using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using linker.kcp;

if (args.Length > 0 && string.Equals(args[0], "--loss-compare", StringComparison.OrdinalIgnoreCase))
{
    Environment.ExitCode = await RunLossCompareAsync(args).ConfigureAwait(false);
    return;
}

var tests = new (string Name, Action Body)[]
{
    ("single packet round trip", SinglePacketRoundTrip),
    ("fragmented message round trip", FragmentedMessageRoundTrip),
    ("out of order packets are delivered in order", OutOfOrderPacketsAreDeliveredInOrder),
    ("rto retransmission recovers dropped packet", RtoRetransmissionRecoversDroppedPacket),
    ("acks release send window", AcksReleaseSendWindow),
    ("stream mode coalesces pending writes", StreamModeCoalescesPendingWrites),
    ("input rejects invalid packets", InputRejectsInvalidPackets),
    ("mtu and send limits are validated", MtuAndSendLimitsAreValidated),
    ("ack range marks selective acknowledgements", AckRangeMarksSelectiveAcknowledgements),
    ("connection receive async uses two byte records", KcpConnectionReceiveAsyncUsesTwoByteRecords)
};

var failed = 0;
foreach (var test in tests)
{
    try
    {
        test.Body();
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

static void SinglePacketRoundTrip()
{
    using var pair = new KcpPair(mtu: 256);
    var payload = DeterministicBytes(96);
    Assert(pair.A.Send(payload) == 0, "Send failed.");
    pair.Pump();

    var received = new byte[payload.Length];
    Assert(pair.B.PeekSize() == payload.Length, "Receiver did not report the expected packet size.");
    Assert(pair.B.Recv(received) == payload.Length, "Recv returned an unexpected length.");
    Assert(payload.AsSpan().SequenceEqual(received), "Round-trip payload mismatch.");
}

static void FragmentedMessageRoundTrip()
{
    using var pair = new KcpPair(mtu: 128);
    var payload = DeterministicBytes(2048);
    Assert(pair.A.Send(payload) == 0, "Fragmented send failed.");
    pair.Pump();

    var received = new byte[payload.Length];
    Assert(pair.B.PeekSize() == payload.Length, "Fragmented message was not reassembled.");
    Assert(pair.B.Recv(received) == payload.Length, "Fragmented recv length mismatch.");
    Assert(payload.AsSpan().SequenceEqual(received), "Fragmented payload mismatch.");
}

static void OutOfOrderPacketsAreDeliveredInOrder()
{
    using var pair = new KcpPair(mtu: 64);
    var packets = new[]
    {
        DeterministicBytes(40, 1),
        DeterministicBytes(40, 2),
        DeterministicBytes(40, 3)
    };

    foreach (var packet in packets)
    {
        Assert(pair.A.Send(packet) == 0, "Send failed.");
    }

    pair.A.Flush();
    pair.ReverseAToB();
    pair.Transfer();

    var received = new byte[40];
    for (var i = 0; i < packets.Length; i++)
    {
        Assert(pair.B.PeekSize() == received.Length, $"Packet {i} was not ready.");
        Assert(pair.B.Recv(received) == received.Length, $"Packet {i} length mismatch.");
        Assert(packets[i].AsSpan().SequenceEqual(received), $"Packet {i} was delivered out of order.");
    }
}

static void RtoRetransmissionRecoversDroppedPacket()
{
    using var pair = new KcpPair(mtu: 256);
    var payload = DeterministicBytes(64);
    pair.DropNextAToB = true;

    Assert(pair.A.Send(payload) == 0, "Send failed.");
    pair.A.Flush();
    pair.Transfer();
    Assert(pair.B.PeekSize() == -1, "Dropped packet should not be readable before retransmission.");

    Thread.Sleep(230);
    pair.Pump(rounds: 8, sleepMilliseconds: 2);

    var received = new byte[payload.Length];
    Assert(pair.B.PeekSize() == payload.Length, "Retransmitted packet was not readable.");
    Assert(pair.B.Recv(received) == payload.Length, "Retransmitted recv length mismatch.");
    Assert(payload.AsSpan().SequenceEqual(received), "Retransmitted payload mismatch.");
}

static void AcksReleaseSendWindow()
{
    using var pair = new KcpPair(mtu: 256);
    var payload = DeterministicBytes(64);
    Assert(pair.A.Send(payload) == 0, "Send failed.");
    Assert(pair.A.WaitSnd() == 1, "Send queue should contain one packet before flush.");
    pair.Pump();

    var received = new byte[payload.Length];
    Assert(pair.B.Recv(received) == payload.Length, "Recv failed.");
    pair.Pump();
    Assert(pair.A.WaitSnd() == 0, "ACK did not release sender buffer.");
}

static void StreamModeCoalescesPendingWrites()
{
    using var pair = new KcpPair(mtu: 256);
    pair.A.StreamMode = true;

    Assert(pair.A.Send("abc"u8) == 0, "First stream send failed.");
    Assert(pair.A.Send("def"u8) == 0, "Second stream send failed.");
    pair.Pump();

    Span<byte> received = stackalloc byte[6];
    Assert(pair.B.PeekSize() == 6, "Stream writes were not coalesced.");
    Assert(pair.B.Recv(received) == 6, "Stream recv length mismatch.");
    Assert(received.SequenceEqual("abcdef"u8), "Stream payload mismatch.");
}

static void InputRejectsInvalidPackets()
{
    using var kcp = new Kcp(1, _ => { });
    Assert(kcp.Input(new byte[Kcp.Overhead - 1]) == -1, "Short packet was not rejected.");

    Span<byte> wrongConv = stackalloc byte[Kcp.Overhead];
    BinaryPrimitives.WriteUInt32LittleEndian(wrongConv, 2);
    wrongConv[4] = Kcp.CommandAck;
    Assert(kcp.Input(wrongConv) == -1, "Wrong conv packet was not rejected.");

    Span<byte> invalidCommand = stackalloc byte[Kcp.Overhead];
    BinaryPrimitives.WriteUInt32LittleEndian(invalidCommand, 1);
    invalidCommand[4] = 0xff;
    Assert(kcp.Input(invalidCommand) == -3, "Invalid command was not rejected.");

    Span<byte> truncatedPayload = stackalloc byte[Kcp.Overhead];
    BinaryPrimitives.WriteUInt32LittleEndian(truncatedPayload, 1);
    truncatedPayload[4] = Kcp.CommandPush;
    BinaryPrimitives.WriteUInt32LittleEndian(truncatedPayload[20..], 1);
    Assert(kcp.Input(truncatedPayload) == -2, "Truncated payload was not rejected.");
}

static void MtuAndSendLimitsAreValidated()
{
    using var kcp = new Kcp(1, _ => { });
    Assert(kcp.SetMtu(Kcp.Overhead) == -1, "Invalid MTU was accepted.");
    Assert(kcp.SetMtu(Kcp.Overhead + 1) == 0, "Minimum valid MTU was rejected.");
    Assert(kcp.Mss == 1, "Minimum valid MTU should produce MSS=1.");
    Assert(kcp.Send(ReadOnlySpan<byte>.Empty) == -1, "Empty send was accepted.");
    Assert(kcp.Send(new byte[256]) == -2, "Message requiring more than 255 fragments was accepted.");
}

static void AckRangeMarksSelectiveAcknowledgements()
{
    var aToB = new List<byte[]>();
    var bToA = new List<byte[]>();
    using var sender = new Kcp(0x10203040, packet => aToB.Add(packet.ToArray()));
    using var receiver = new Kcp(0x10203040, packet => bToA.Add(packet.ToArray()));
    Assert(sender.SetMtu(64) == 0, "Sender MTU setup failed.");
    Assert(receiver.SetMtu(64) == 0, "Receiver MTU setup failed.");
    sender.NoDelay(1, 10, 2, 1);
    receiver.NoDelay(1, 10, 2, 1);

    var payload = DeterministicBytes(16);
    for (var i = 0; i < 5; i++)
    {
        Assert(sender.Send(payload) == 0, $"Send {i} failed.");
    }

    sender.Flush();
    Assert(aToB.Count == 5, $"Expected 5 outbound data packets, got {aToB.Count}.");

    receiver.Input(aToB[0]);
    receiver.Input(aToB[2]);
    receiver.Input(aToB[3]);
    receiver.Input(aToB[4]);
    receiver.Flush();

    Assert(bToA.Exists(packet => packet.Length >= Kcp.Overhead && packet[4] == Kcp.CommandAckRange), "Receiver did not emit an ACK range.");
    foreach (var packet in bToA)
    {
        sender.Input(packet);
    }

    var diagnostics = sender.GetDiagnostics();
    Assert(diagnostics.SelectiveAckedSegments == 3, $"ACK range marked {diagnostics.SelectiveAckedSegments} selective acknowledgements.");
}

static void KcpConnectionReceiveAsyncUsesTwoByteRecords()
{
    KcpConnectionReceiveAsyncUsesTwoByteRecordsAsync().GetAwaiter().GetResult();
}

static async Task KcpConnectionReceiveAsyncUsesTwoByteRecordsAsync()
{
    using var socketA = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
    using var socketB = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
    socketA.Bind(new IPEndPoint(IPAddress.Loopback, 0));
    socketB.Bind(new IPEndPoint(IPAddress.Loopback, 0));

    await using var connectionA = new KcpConnection(
        0x55667788,
        mtu: 256,
        window: 128,
        nodelay: 1,
        interval: 10,
        resend: 2,
        nc: 1,
        socketA,
        socketB.LocalEndPoint!,
        recv: true,
        flushBatchSegments: 1,
        ackFlushBatchPackets: 1);

    await using var connectionB = new KcpConnection(
        0x55667788,
        mtu: 256,
        window: 128,
        nodelay: 1,
        interval: 10,
        resend: 2,
        nc: 1,
        socketB,
        socketA.LocalEndPoint!,
        recv: true,
        flushBatchSegments: 1,
        ackFlushBatchPackets: 1);

    var payload = DeterministicBytes(37, 9);
    await connectionA.SendAsync(payload).ConfigureAwait(false);
    connectionA.FlushPending();

    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
    var buffer = new byte[128];
    var received = await connectionB.ReceiveAsync(buffer, timeout.Token).ConfigureAwait(false);

    Assert(received == sizeof(ushort) + payload.Length, "ReceiveAsync returned an unexpected framed length.");
    Assert(BinaryPrimitives.ReadUInt16LittleEndian(buffer) == payload.Length, "ReceiveAsync did not use a 2-byte length prefix.");
    Assert(buffer.AsSpan(sizeof(ushort), payload.Length).SequenceEqual(payload), "ReceiveAsync payload mismatch.");
}

static byte[] DeterministicBytes(int length, int salt = 0)
{
    var bytes = new byte[length];
    var value = 0x5A + salt;
    for (var i = 0; i < bytes.Length; i++)
    {
        value = unchecked((value * 1103515245) + 12345);
        bytes[i] = (byte)(value >> 16);
    }

    return bytes;
}

static void Assert(bool condition, string message)
{
    if (!condition)
    {
        throw new InvalidOperationException(message);
    }
}

static async Task<int> RunLossCompareAsync(string[] args)
{
    var seconds = GetIntArg(args, 1, 10);
    var basePort = GetIntArg(args, 2, 0);
    var lossPercent = GetDoubleArg(args, 3, 10.0);
    var ackLossPercent = GetOptionDouble(args, "--ack-loss", lossPercent);
    var payloadLength = GetOptionInt(args, "--payload", 1400);
    var mtu = GetOptionInt(args, "--mtu", 1500);
    var window = GetOptionInt(args, "--window", 8192);
    var flushBatch = GetOptionInt(args, "--flush-batch", 128);
    var ackBatch = GetOptionInt(args, "--ack-batch", 1024);
    var nodelay = GetOptionInt(args, "--nodelay", 1);
    var interval = GetOptionInt(args, "--interval", 10);
    var resend = GetOptionInt(args, "--resend", 2);
    var nc = GetOptionInt(args, "--nc", 1);

    if (seconds <= 0)
    {
        Console.Error.WriteLine("seconds must be greater than zero.");
        return 1;
    }

    if (basePort < 0 || basePort > IPEndPoint.MaxPort)
    {
        Console.Error.WriteLine("basePort must be 0..65535.");
        return 1;
    }

    if (lossPercent < 0 || lossPercent > 100)
    {
        Console.Error.WriteLine("lossPercent must be 0..100.");
        return 1;
    }

    Console.WriteLine($"loss compare: seconds={seconds}, basePort={basePort}, loss={lossPercent:N2}%, ackLoss={ackLossPercent:N2}%, payload={payloadLength}B, mtu={mtu}, window={window}, flushBatch={flushBatch}, ackBatch={ackBatch}, nodelay={nodelay}, interval={interval}, resend={resend}, nc={nc}");

    var raw = await RunRawUdpLossCompareAsync(TimeSpan.FromSeconds(seconds), basePort, lossPercent, payloadLength).ConfigureAwait(false);
    PrintLossCompareResult("raw UDP", raw);

    var kcp = await RunKcpLossCompareAsync(TimeSpan.FromSeconds(seconds), basePort == 0 ? 0 : basePort + 10, lossPercent, ackLossPercent, payloadLength, mtu, window, flushBatch, ackBatch, nodelay, interval, resend, nc).ConfigureAwait(false);
    PrintLossCompareResult("KcpConnection", kcp);

    var ratio = raw.AppBytes == 0 ? 0 : (double)kcp.AppBytes / raw.AppBytes;
    Console.WriteLine($"compare: KCP/raw app throughput = {ratio:P1}");
    return 0;
}

static async Task<LossCompareResult> RunRawUdpLossCompareAsync(TimeSpan duration, int basePort, double lossPercent, int payloadLength)
{
    using var receiver = CreateUdpSocket(BindEndpoint(basePort));
    using var proxySocket = CreateUdpSocket(BindEndpoint(basePort == 0 ? 0 : basePort + 1));
    using var sender = CreateUdpSocket(new IPEndPoint(IPAddress.Loopback, 0));

    var proxyEndpoint = (IPEndPoint)proxySocket.LocalEndPoint!;
    var receiverEndpoint = (IPEndPoint)receiver.LocalEndPoint!;
    using var cts = new CancellationTokenSource();
    using var proxy = new LossProxy(proxySocket, receiverEndpoint, lossPercent, seed: 1001);

    var stats = new TrafficStats();
    var receiveTask = StartLongRunning(() => RawReceiveLoop(receiver, stats, cts.Token), cts.Token);
    var proxyTask = proxy.Start(cts.Token);
    var sendTask = StartLongRunning(() => RawSendLoop(sender, proxyEndpoint, payloadLength, stats, cts.Token), cts.Token);

    var stopwatch = Stopwatch.StartNew();
    await Task.Delay(duration).ConfigureAwait(false);
    cts.Cancel();
    stopwatch.Stop();

    await WhenAllIgnoreCancellationAsync(sendTask, receiveTask, proxyTask).ConfigureAwait(false);

    return new LossCompareResult(
        AppBytes: Interlocked.Read(ref stats.ReceivedBytes),
        AppPackets: Interlocked.Read(ref stats.ReceivedPackets),
        SentPackets: Interlocked.Read(ref stats.SentPackets),
        SentBytes: Interlocked.Read(ref stats.SentBytes),
        C2SDropped: proxy.DroppedPackets,
        C2SReceived: proxy.ReceivedPackets,
        S2CDropped: 0,
        S2CReceived: 0,
        SendWouldBlock: Interlocked.Read(ref stats.SendWouldBlock),
        KcpSendWouldBlock: 0,
        KcpSendDrop: 0,
        KcpWaitSndPeak: 0,
        ClientDiagnostics: default,
        ServerDiagnostics: default,
        Elapsed: stopwatch.Elapsed);
}

static async Task<LossCompareResult> RunKcpLossCompareAsync(TimeSpan duration, int basePort, double lossPercent, double ackLossPercent, int payloadLength, int mtu, int window, int flushBatch, int ackBatch, int nodelay, int interval, int resend, int nc)
{
    using var clientSocket = CreateUdpSocket(BindEndpoint(basePort));
    using var serverSocket = CreateUdpSocket(BindEndpoint(basePort == 0 ? 0 : basePort + 1));
    using var c2sSocket = CreateUdpSocket(BindEndpoint(basePort == 0 ? 0 : basePort + 2));
    using var s2cSocket = CreateUdpSocket(BindEndpoint(basePort == 0 ? 0 : basePort + 3));

    var clientEndpoint = (IPEndPoint)clientSocket.LocalEndPoint!;
    var serverEndpoint = (IPEndPoint)serverSocket.LocalEndPoint!;
    var c2sEndpoint = (IPEndPoint)c2sSocket.LocalEndPoint!;
    var s2cEndpoint = (IPEndPoint)s2cSocket.LocalEndPoint!;

    using var cts = new CancellationTokenSource();
    using var c2sProxy = new LossProxy(c2sSocket, serverEndpoint, lossPercent, seed: 2001);
    using var s2cProxy = new LossProxy(s2cSocket, clientEndpoint, ackLossPercent, seed: 2002);

    await using var client = new KcpConnection(
        0x87654321,
        mtu,
        window,
        nodelay,
        interval,
        resend,
        nc,
        clientSocket,
        c2sEndpoint,
        recv: true,
        flushBatchSegments: flushBatch,
        ackFlushBatchPackets: ackBatch);

    await using var server = new KcpConnection(
        0x87654321,
        mtu,
        window,
        nodelay,
        interval,
        resend,
        nc,
        serverSocket,
        s2cEndpoint,
        recv: false,
        flushBatchSegments: flushBatch,
        ackFlushBatchPackets: ackBatch);

    var stats = new TrafficStats();
    var c2sTask = c2sProxy.Start(cts.Token);
    var s2cTask = s2cProxy.Start(cts.Token);
    var inputTask = StartLongRunning(() => KcpExternalInputLoop(serverSocket, server, cts.Token), cts.Token);
    var receiveTask = Task.Run(() => KcpReceiveLoopAsync(server, stats, cts.Token), cts.Token);
    var sendTask = Task.Run(() => KcpSendLoopAsync(client, payloadLength, stats, cts.Token), cts.Token);

    var stopwatch = Stopwatch.StartNew();
    await Task.Delay(duration).ConfigureAwait(false);
    cts.Cancel();
    stopwatch.Stop();

    await WhenAllIgnoreCancellationAsync(sendTask, receiveTask, inputTask, c2sTask, s2cTask).ConfigureAwait(false);

    return new LossCompareResult(
        AppBytes: Interlocked.Read(ref stats.ReceivedBytes),
        AppPackets: Interlocked.Read(ref stats.ReceivedPackets),
        SentPackets: Interlocked.Read(ref stats.SentPackets),
        SentBytes: Interlocked.Read(ref stats.SentBytes),
        C2SDropped: c2sProxy.DroppedPackets,
        C2SReceived: c2sProxy.ReceivedPackets,
        S2CDropped: s2cProxy.DroppedPackets,
        S2CReceived: s2cProxy.ReceivedPackets,
        SendWouldBlock: Interlocked.Read(ref stats.SendWouldBlock),
        KcpSendWouldBlock: client.SendWouldBlockCount + server.SendWouldBlockCount,
        KcpSendDrop: client.SendDropCount + server.SendDropCount,
        KcpWaitSndPeak: Math.Max(client.WaitSndPeak, server.WaitSndPeak),
        ClientDiagnostics: client.GetDiagnostics(),
        ServerDiagnostics: server.GetDiagnostics(),
        Elapsed: stopwatch.Elapsed);
}

static void RawSendLoop(Socket sender, EndPoint target, int payloadLength, TrafficStats stats, CancellationToken cancellationToken)
{
    var payload = DeterministicBytes(payloadLength, 17);
    while (!cancellationToken.IsCancellationRequested)
    {
        try
        {
            var sent = sender.SendTo(payload, SocketFlags.None, target);
            Interlocked.Increment(ref stats.SentPackets);
            Interlocked.Add(ref stats.SentBytes, sent);
        }
        catch (SocketException ex) when (IsWouldBlock(ex))
        {
            Interlocked.Increment(ref stats.SendWouldBlock);
            Thread.Yield();
        }
        catch (SocketException ex) when (IsExpectedSocketClose(ex))
        {
            break;
        }
        catch (ObjectDisposedException)
        {
            break;
        }
    }
}

static void RawReceiveLoop(Socket receiver, TrafficStats stats, CancellationToken cancellationToken)
{
    var buffer = new byte[2048];
    EndPoint remote = new IPEndPoint(IPAddress.Any, 0);
    while (!cancellationToken.IsCancellationRequested)
    {
        try
        {
            var received = receiver.ReceiveFrom(buffer, SocketFlags.None, ref remote);
            if (received > 0)
            {
                Interlocked.Increment(ref stats.ReceivedPackets);
                Interlocked.Add(ref stats.ReceivedBytes, received);
            }
        }
        catch (SocketException ex) when (IsWouldBlock(ex))
        {
            PollSocket(receiver);
        }
        catch (SocketException ex) when (IsExpectedSocketClose(ex))
        {
            break;
        }
        catch (ObjectDisposedException)
        {
            break;
        }
    }
}

static async Task KcpSendLoopAsync(KcpConnection connection, int payloadLength, TrafficStats stats, CancellationToken cancellationToken)
{
    var payload = DeterministicBytes(payloadLength, 23);
    while (!cancellationToken.IsCancellationRequested)
    {
        try
        {
            await connection.SendAsync(payload, cancellationToken).ConfigureAwait(false);
            Interlocked.Increment(ref stats.SentPackets);
            Interlocked.Add(ref stats.SentBytes, payloadLength);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            break;
        }
        catch (ObjectDisposedException)
        {
            break;
        }
    }
}

static async Task KcpReceiveLoopAsync(KcpConnection connection, TrafficStats stats, CancellationToken cancellationToken)
{
    var buffer = new byte[1024 * 1024];
    while (!cancellationToken.IsCancellationRequested)
    {
        try
        {
            var received = await connection.ReceiveAsync(buffer, cancellationToken).ConfigureAwait(false);
            CountTwoByteRecords(buffer.AsSpan(0, received), stats);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            break;
        }
        catch (ObjectDisposedException)
        {
            break;
        }
    }
}

static void KcpExternalInputLoop(Socket socket, KcpConnection connection, CancellationToken cancellationToken)
{
    var buffer = new byte[2048];
    EndPoint remote = new IPEndPoint(IPAddress.Any, 0);
    while (!cancellationToken.IsCancellationRequested)
    {
        try
        {
            var received = socket.ReceiveFrom(buffer, SocketFlags.None, ref remote);
            if (received > 0)
            {
                connection.Input(buffer, 0, received);
            }
        }
        catch (SocketException ex) when (IsWouldBlock(ex))
        {
            PollSocket(socket);
        }
        catch (SocketException ex) when (IsExpectedSocketClose(ex))
        {
            break;
        }
        catch (ObjectDisposedException)
        {
            break;
        }
    }
}

static void CountTwoByteRecords(ReadOnlySpan<byte> records, TrafficStats stats)
{
    while (records.Length >= sizeof(ushort))
    {
        var length = BinaryPrimitives.ReadUInt16LittleEndian(records);
        records = records[sizeof(ushort)..];
        if (records.Length < length)
        {
            break;
        }

        Interlocked.Increment(ref stats.ReceivedPackets);
        Interlocked.Add(ref stats.ReceivedBytes, length);
        records = records[length..];
    }
}

static void PrintLossCompareResult(string name, LossCompareResult result)
{
    var appGbps = result.Elapsed.TotalSeconds <= 0 ? 0 : result.AppBytes * 8.0 / result.Elapsed.TotalSeconds / 1_000_000_000.0;
    var sentGbps = result.Elapsed.TotalSeconds <= 0 ? 0 : result.SentBytes * 8.0 / result.Elapsed.TotalSeconds / 1_000_000_000.0;
    var c2sLoss = result.C2SReceived == 0 ? 0 : result.C2SDropped * 100.0 / result.C2SReceived;
    var s2cLoss = result.S2CReceived == 0 ? 0 : result.S2CDropped * 100.0 / result.S2CReceived;

    Console.WriteLine(
        $"{name}: app={appGbps:N2} Gbps, appBytes={result.AppBytes:N0}, appPackets={result.AppPackets:N0}, " +
        $"sent={sentGbps:N2} Gbps/{result.SentPackets:N0} packets, dt={result.Elapsed.TotalSeconds:N2}s");
    Console.WriteLine(
        $"{name}: c2s drop={c2sLoss:N2}% ({result.C2SDropped:N0}/{result.C2SReceived:N0}), " +
        $"s2c drop={s2cLoss:N2}% ({result.S2CDropped:N0}/{result.S2CReceived:N0}), " +
        $"sendWouldBlock={result.SendWouldBlock:N0}, kcpWouldBlock={result.KcpSendWouldBlock:N0}, kcpDrop={result.KcpSendDrop:N0}, waitPeak={result.KcpWaitSndPeak:N0}");

    if (result.ClientDiagnostics.OutputDatagrams > 0 || result.ServerDiagnostics.OutputDatagrams > 0)
    {
        PrintKcpDiagnostics(result);
    }
}

static void PrintKcpDiagnostics(LossCompareResult result)
{
    var client = result.ClientDiagnostics;
    var server = result.ServerDiagnostics;
    Console.WriteLine(
        "KCP client: " +
        $"outDg={client.OutputDatagrams:N0}, outPush init/fast/early/rto={client.OutputInitialPushSegments:N0}/{client.OutputFastResendPushSegments:N0}/{client.OutputEarlyResendPushSegments:N0}/{client.OutputRtoResendPushSegments:N0}, " +
        $"inAck={client.InputAckSegments:N0}, fastMarks={client.FastAckMarks:N0}, " +
        $"flush full/pending/ack={client.FullFlushCount:N0}/{client.PendingFlushCount:N0}/{client.AckOnlyFlushCount:N0}, " +
        $"sndBuf={client.SendBufferCount:N0}, rtt/rto={client.SmoothedRtt:N0}/{client.Rto:N0}ms");
    Console.WriteLine(
        "KCP server: " +
        $"inPush={server.InputPushSegments:N0}, outDg={server.OutputDatagrams:N0}, outAck={server.OutputAckSegments:N0}, " +
        $"flush full/pending/ack={server.FullFlushCount:N0}/{server.PendingFlushCount:N0}/{server.AckOnlyFlushCount:N0}, " +
        $"rcvQ/rcvBuf={server.ReceiveQueueCount:N0}/{server.ReceiveBufferCount:N0}");
}

static int GetIntArg(string[] args, int index, int defaultValue)
{
    return args.Length > index && int.TryParse(args[index], out var value) ? value : defaultValue;
}

static double GetDoubleArg(string[] args, int index, double defaultValue)
{
    return args.Length > index && double.TryParse(args[index], out var value) ? value : defaultValue;
}

static int GetOptionInt(string[] args, string name, int defaultValue)
{
    for (var i = 4; i < args.Length; i++)
    {
        var arg = args[i];
        if (string.Equals(arg, name, StringComparison.OrdinalIgnoreCase)
            && i + 1 < args.Length
            && int.TryParse(args[i + 1], out var value))
        {
            return value;
        }

        if (arg.StartsWith(name + "=", StringComparison.OrdinalIgnoreCase)
            && int.TryParse(arg[(name.Length + 1)..], out value))
        {
            return value;
        }
    }

    return defaultValue;
}

static double GetOptionDouble(string[] args, string name, double defaultValue)
{
    for (var i = 4; i < args.Length; i++)
    {
        var arg = args[i];
        if (string.Equals(arg, name, StringComparison.OrdinalIgnoreCase)
            && i + 1 < args.Length
            && double.TryParse(args[i + 1], out var value))
        {
            return value;
        }

        if (arg.StartsWith(name + "=", StringComparison.OrdinalIgnoreCase)
            && double.TryParse(arg[(name.Length + 1)..], out value))
        {
            return value;
        }
    }

    return defaultValue;
}

static IPEndPoint BindEndpoint(int port)
{
    return new IPEndPoint(IPAddress.Loopback, port);
}

static Socket CreateUdpSocket(IPEndPoint endpoint)
{
    var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp)
    {
        Blocking = false
    };
    TrySetSocketBuffers(socket, 16 * 1024 * 1024);
    socket.Bind(endpoint);
    return socket;
}

static void TrySetSocketBuffers(Socket socket, int size)
{
    try
    {
        socket.ReceiveBufferSize = size;
        socket.SendBufferSize = size;
    }
    catch (SocketException)
    {
    }
    catch (ObjectDisposedException)
    {
    }
}

static Task StartLongRunning(Action action, CancellationToken cancellationToken)
{
    return Task.Factory.StartNew(action, cancellationToken, TaskCreationOptions.LongRunning, TaskScheduler.Default);
}

static async Task WhenAllIgnoreCancellationAsync(params Task[] tasks)
{
    try
    {
        await Task.WhenAll(tasks).ConfigureAwait(false);
    }
    catch (OperationCanceledException)
    {
    }
}

static void PollSocket(Socket socket)
{
    try
    {
        socket.Poll(1000, SelectMode.SelectRead);
    }
    catch (SocketException ex) when (IsExpectedSocketClose(ex))
    {
    }
    catch (ObjectDisposedException)
    {
    }
}

static bool IsWouldBlock(SocketException ex)
{
    return ex.SocketErrorCode is SocketError.WouldBlock or SocketError.IOPending or SocketError.NoBufferSpaceAvailable;
}

static bool IsExpectedSocketClose(SocketException ex)
{
    return ex.SocketErrorCode is SocketError.Interrupted
        or SocketError.OperationAborted
        or SocketError.ConnectionReset
        or SocketError.NotSocket
        || ex.NativeErrorCode is 10004;
}

internal readonly record struct LossCompareResult(
    long AppBytes,
    long AppPackets,
    long SentPackets,
    long SentBytes,
    long C2SDropped,
    long C2SReceived,
    long S2CDropped,
    long S2CReceived,
    long SendWouldBlock,
    long KcpSendWouldBlock,
    long KcpSendDrop,
    long KcpWaitSndPeak,
    KcpDiagnostics ClientDiagnostics,
    KcpDiagnostics ServerDiagnostics,
    TimeSpan Elapsed);

internal sealed class TrafficStats
{
    public long SentPackets;
    public long SentBytes;
    public long ReceivedPackets;
    public long ReceivedBytes;
    public long SendWouldBlock;
}

internal sealed class LossProxy : IDisposable
{
    private readonly Socket _socket;
    private readonly EndPoint _target;
    private readonly double _lossPercent;
    private readonly Random _random;
    private long _receivedPackets;
    private long _droppedPackets;

    public LossProxy(Socket socket, EndPoint target, double lossPercent, int seed)
    {
        _socket = socket;
        _target = target;
        _lossPercent = lossPercent;
        _random = new Random(seed);
    }

    public long ReceivedPackets => Interlocked.Read(ref _receivedPackets);

    public long DroppedPackets => Interlocked.Read(ref _droppedPackets);

    public Task Start(CancellationToken cancellationToken)
    {
        return Task.Factory.StartNew(
            () => Run(cancellationToken),
            cancellationToken,
            TaskCreationOptions.LongRunning,
            TaskScheduler.Default);
    }

    public void Dispose()
    {
    }

    private void Run(CancellationToken cancellationToken)
    {
        var buffer = new byte[2048];
        EndPoint remote = new IPEndPoint(IPAddress.Any, 0);
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                var received = _socket.ReceiveFrom(buffer, SocketFlags.None, ref remote);
                if (received <= 0)
                {
                    continue;
                }

                Interlocked.Increment(ref _receivedPackets);
                if (_random.NextDouble() * 100.0 < _lossPercent)
                {
                    Interlocked.Increment(ref _droppedPackets);
                    continue;
                }

                _socket.SendTo(buffer.AsSpan(0, received), SocketFlags.None, _target);
            }
            catch (SocketException ex) when (IsWouldBlockLocal(ex))
            {
                PollSocketLocal(_socket);
            }
            catch (SocketException ex) when (IsExpectedSocketCloseLocal(ex))
            {
                break;
            }
            catch (ObjectDisposedException)
            {
                break;
            }
        }
    }

    private static void PollSocketLocal(Socket socket)
    {
        try
        {
            socket.Poll(1000, SelectMode.SelectRead);
        }
        catch (SocketException ex) when (IsExpectedSocketCloseLocal(ex))
        {
        }
        catch (ObjectDisposedException)
        {
        }
    }

    private static bool IsWouldBlockLocal(SocketException ex)
    {
        return ex.SocketErrorCode is SocketError.WouldBlock or SocketError.IOPending or SocketError.NoBufferSpaceAvailable;
    }

    private static bool IsExpectedSocketCloseLocal(SocketException ex)
    {
        return ex.SocketErrorCode is SocketError.Interrupted
            or SocketError.OperationAborted
            or SocketError.ConnectionReset
            or SocketError.NotSocket
            || ex.NativeErrorCode is 10004;
    }
}

internal sealed class KcpPair : IDisposable
{
    private readonly Queue<byte[]> _aToB = new();
    private readonly Queue<byte[]> _bToA = new();

    public KcpPair(uint conv = 0x11223344, int mtu = 256)
    {
        A = new Kcp(conv, packet =>
        {
            if (DropNextAToB)
            {
                DropNextAToB = false;
                return;
            }

            _aToB.Enqueue(packet.ToArray());
        });
        B = new Kcp(conv, packet => _bToA.Enqueue(packet.ToArray()));
        A.SetMtu(mtu);
        B.SetMtu(mtu);
        A.NoDelay(1, 10, 2, 1);
        B.NoDelay(1, 10, 2, 1);
    }

    public Kcp A { get; }

    public Kcp B { get; }

    public bool DropNextAToB { get; set; }

    public void Pump(int rounds = 16, int sleepMilliseconds = 0)
    {
        for (var i = 0; i < rounds; i++)
        {
            A.Flush();
            B.Flush();
            var moved = Transfer();
            if (sleepMilliseconds > 0)
            {
                Thread.Sleep(sleepMilliseconds);
            }

            if (moved == 0 && A.WaitSnd() == 0 && B.WaitSnd() == 0)
            {
                break;
            }
        }
    }

    public int Transfer()
    {
        var moved = 0;
        while (_aToB.Count > 0 || _bToA.Count > 0)
        {
            while (_aToB.Count > 0)
            {
                B.Input(_aToB.Dequeue(), KcpPacketType.Regular, ackNoDelay: true);
                moved++;
            }

            while (_bToA.Count > 0)
            {
                A.Input(_bToA.Dequeue(), KcpPacketType.Regular, ackNoDelay: true);
                moved++;
            }
        }

        return moved;
    }

    public void ReverseAToB()
    {
        var packets = _aToB.ToArray();
        _aToB.Clear();
        for (var i = packets.Length - 1; i >= 0; i--)
        {
            _aToB.Enqueue(packets[i]);
        }
    }

    public void Dispose()
    {
        A.Dispose();
        B.Dispose();
    }
}
