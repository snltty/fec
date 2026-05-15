using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace linker.fec.sample.udp
{
    internal class Program
    {
        const int row = 10, col = 10;
        static string[] array = new string[row * col];

        static void Main(string[] args)
        {
            bool client = args.Any(a => a.Contains("client"));
            bool server = args.Any(a => a.Contains("server"));
            bool fec = args.Any(a => a.Contains("fec"));
            IPEndPoint ep = IPEndPoint.Parse((args.FirstOrDefault(a => a.StartsWith("ep")) ?? "127.0.0.1:12345").Replace("ep", ""));

            if (client)
            {
                RunClient(fec, ep);
            }
            else if (server)
            {
                RunServer(fec, ep);
            }

            Console.ReadLine();
        }
        static async void RunClient(bool fec, IPEndPoint ep)
        {
            LinkerFecStreamingEncoder codec = new LinkerFecStreamingEncoder(new LinkerFecOptions
            {
                SourceSymbolsPerBlock = 10,
                RepairSymbolsPerBlock = 2,
                SymbolSize = 1433,
                RepairProfile = [
                   new LinkerFecRepairProfilePoint(1, 3),
                    new LinkerFecRepairProfilePoint(10, 4)
                ],
            }, TimeSpan.FromMilliseconds(10));
            byte[] encodeBuffer = new byte[codec.MaxOutputBufferSize];
            byte[] flushBuffer = new byte[codec.MaxOutputBufferSize];
            byte[] decodeBuffer = new byte[codec.Options.MaxDecodeBufferSize];
            LinkerFecDecodedPacketKind[] kinds = new LinkerFecDecodedPacketKind[codec.Options.SourceSymbolsPerBlock + codec.Options.RepairSymbolsPerBlock];

            UdpClient udp = new UdpClient();
            udp.Connect(ep);
            TryFlushTask(codec, flushBuffer, async (packet) => await TrySendAsync(udp, packet).ConfigureAwait(false));

            _ = Task.Run(async () =>
            {
                while (true)
                {
                    try
                    {
                        var result = await udp.ReceiveAsync().ConfigureAwait(false);
                        if (fec)
                        {
                            await TryDecodeFrame(codec, result.Buffer, decodeBuffer, kinds, (packet) =>
                            {
                                string recv = Encoding.UTF8.GetString(packet.Span);
                                Console.Write($"{recv},");
                                array[int.Parse(recv.Split('-')[0])] = recv;
                                return Task.CompletedTask;
                            }).ConfigureAwait(false);
                        }
                        else
                        {
                            string recv = Encoding.UTF8.GetString(result.Buffer);
                            Console.Write($"{recv},");
                            array[int.Parse(recv.Split('-')[0])] = recv;
                        }
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"Error receiving UDP packet: {ex.Message}");
                    }
                }
            });

            for (int index = 0; index < row * col; index++)
            {
                byte[] source = Encoding.UTF8.GetBytes($"{(index < 10 ? "0" : "")}{index}");
                if (fec)
                {
                    await TryEncodePacket(codec, source, encodeBuffer, async (encodedPacket) =>
                    {
                        await TrySendAsync(udp, encodedPacket).ConfigureAwait(false);
                    }).ConfigureAwait(false);
                }
                else
                {
                    await TrySendAsync(udp, source).ConfigureAwait(false);
                }
                await Task.Delay(1000).ConfigureAwait(false);
            }
            await Task.Delay(5000).ConfigureAwait(false);
            Console.WriteLine();
            var sb = new StringBuilder();
            sb.AppendLine($"|{string.Join(" | ", Enumerable.Range(0, col).Select(j => $"{j}"))}|");
            sb.AppendLine($"|{string.Join("|", Enumerable.Repeat("---", col))}|");
            for (int i = 0; i < row; i++)
            {
                sb.AppendLine($"|{string.Join("|", Enumerable.Range(0, col).Select(j => string.IsNullOrWhiteSpace(array[i * col + j]) ? "MISS" : array[i * col + j]))}|");
            }
            Console.WriteLine(sb.ToString());

        }
        static async void RunServer(bool fec, IPEndPoint ep)
        {
            LinkerFecStreamingEncoder codec = new LinkerFecStreamingEncoder(new LinkerFecOptions
            {
                SourceSymbolsPerBlock = 10,
                RepairSymbolsPerBlock = 2,
                SymbolSize = 1433,
                RepairProfile = [
                    new LinkerFecRepairProfilePoint(1, 3),
                    new LinkerFecRepairProfilePoint(10, 4)
                ]
            }, TimeSpan.FromMilliseconds(10));
            byte[] encodeBuffer = new byte[codec.MaxOutputBufferSize];
            byte[] flushBuffer = new byte[codec.MaxOutputBufferSize];
            byte[] decodeBuffer = new byte[codec.Options.MaxDecodeBufferSize];
            LinkerFecDecodedPacketKind[] kinds = new LinkerFecDecodedPacketKind[codec.Options.SourceSymbolsPerBlock + codec.Options.RepairSymbolsPerBlock];

            UdpClient udp = new UdpClient(ep);
            IPEndPoint source = new IPEndPoint(IPAddress.Any, 0);
            TryFlushTask(codec, flushBuffer, async (packet) => await TrySendAsync(udp, packet, source).ConfigureAwait(false));

            while (true)
            {
                var result = await udp.ReceiveAsync().ConfigureAwait(false);
                source = result.RemoteEndPoint;
                if (fec)
                {
                    await TryDecodeFrame(codec, result.Buffer, decodeBuffer, kinds, async (packet) =>
                    {
                        await TryEncodePacket(codec, packet, encodeBuffer, async (encodedPacket) =>
                         {
                             await TrySendAsync(udp, encodedPacket, result.RemoteEndPoint).ConfigureAwait(false);

                         }).ConfigureAwait(false);

                    }).ConfigureAwait(false);
                }
                else
                {
                    await TrySendAsync(udp, result.Buffer, result.RemoteEndPoint).ConfigureAwait(false);
                }
            }
        }

        static async Task TryEncodePacket(LinkerFecStreamingEncoder codec, ReadOnlyMemory<byte> source, Memory<byte> destination, Func<Memory<byte>, Task> callback)
        {
            byte[] record = new byte[sizeof(int) + source.Length];
            BinaryPrimitives.WriteInt32LittleEndian(record.AsSpan(0, sizeof(int)), source.Length);
            source.Span.CopyTo(record.AsSpan(sizeof(int)));

            if (codec.TryEncodePacket(record, destination, out int bytesWritten, out int packetCount))
            {
                var memory = destination.Slice(0, bytesWritten);
                for (int i = 0; i < packetCount; i++)
                {
                    int packetLength = BinaryPrimitives.ReadInt32LittleEndian(memory.Span);
                    Memory<byte> packet = memory.Slice(sizeof(int), packetLength);
                    await callback(packet).ConfigureAwait(false);

                    memory = memory.Slice(sizeof(int) + packetLength);
                }
            }
        }
        static async Task TryDecodeFrame(LinkerFecStreamingEncoder codec, ReadOnlyMemory<byte> frame, Memory<byte> destination, LinkerFecDecodedPacketKind[] packetKinds, Func<Memory<byte>, Task> callback)
        {
            if (codec.TryDecodeFrame(frame, destination, packetKinds, out int bytesWritten, out int packetCount))
            {
                Memory<byte> packets = destination.Slice(0, bytesWritten);
                for (int i = 0; i < packetCount; i++)
                {
                    int packetLength = BinaryPrimitives.ReadInt32LittleEndian(packets.Span);
                    Memory<byte> packet = packets.Slice(sizeof(int), packetLength);
                    if (packetKinds[i] == LinkerFecDecodedPacketKind.Recovered)
                    {
                        packet = Encoding.UTF8.GetBytes($"{Encoding.UTF8.GetString(packet.Span)}-FEC");
                    }
                    await callback(packet).ConfigureAwait(false);

                    packets = packets.Slice(sizeof(int) + packetLength);
                }
            }
        }

        static async void TryFlushTask(LinkerFecStreamingEncoder codec, byte[] encodeBuffer, Func<Memory<byte>, Task> callback)
        {
            using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(2));
            try
            {
                while (await timer.WaitForNextTickAsync())
                {
                    if (codec.TryFlushRepairs(encodeBuffer.AsSpan(), out int bytesWritten, out int packetCount))
                    {
                        var memory = encodeBuffer.AsMemory(0, bytesWritten);
                        for (int i = 0; i < packetCount; i++)
                        {
                            int packetLength = BinaryPrimitives.ReadInt32LittleEndian(memory.Span);
                            Memory<byte> packet = memory.Slice(sizeof(int), packetLength);
                            await callback(packet).ConfigureAwait(false);

                            memory = memory.Slice(sizeof(int) + packetLength);
                        }
                    }
                }
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Flush task stopped: {ex.Message}");
            }
        }

        static async Task<bool> TrySendAsync(UdpClient udp, ReadOnlyMemory<byte> packet)
        {
            try
            {
                await udp.SendAsync(packet).ConfigureAwait(false);
                return true;
            }
            catch (SocketException ex) when (IsLocalFirewallDrop(ex))
            {
                return false;
            }
        }
        static async Task<bool> TrySendAsync(UdpClient udp, ReadOnlyMemory<byte> packet, IPEndPoint remoteEndPoint)
        {
            try
            {
                await udp.SendAsync(packet, remoteEndPoint).ConfigureAwait(false);
                return true;
            }
            catch (SocketException ex) when (IsLocalFirewallDrop(ex))
            {
                return false;
            }
        }
        static bool IsLocalFirewallDrop(SocketException ex)
        {
            return ex.SocketErrorCode == SocketError.AccessDenied ||
                ex.NativeErrorCode is 1 or 13;
        }
    }
}
