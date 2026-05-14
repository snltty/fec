using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace linker.fec.sample.udp
{
    internal class Program
    {
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

            if (server)
            {
                RunServer(fec, ep);
            }

            Console.ReadLine();
        }
        static async void RunClient(bool fec, IPEndPoint ep)
        {
            LinkerFecPacketBatcher packetBatcher = new LinkerFecPacketBatcher(256 * 1024, new LinkerFecOptions
            {
                SourceSymbolsPerBlock = 10,
                RepairSymbolsPerBlock = 2,
                SymbolSize = 1433,
            });
            byte[] encodeBuffer = new byte[packetBatcher.Options.MaxEncodeBufferSize];
            UdpClient udp = new UdpClient();
            udp.Connect(ep);

            _ = Task.Run(async () =>
            {
                while (true)
                {
                        var memory = await packetBatcher.ReadAsync().ConfigureAwait(false);
                    do
                    {
                        var frameLength = BinaryPrimitives.ReadInt32LittleEndian(memory.Span);
                        var frame = memory.Slice(sizeof(int), frameLength);

                        await udp.SendAsync(frame).ConfigureAwait(false);

                        memory = memory.Slice(sizeof(int) + frameLength);

                    } while (memory.Length > 0);
                }
            });

            int row = 10, col = 10;
            string[] array = new string[row * col];
            for (int index = 0; index < row * col; index++)
            {
                {
                    if (fec)
                    {
                        await packetBatcher.WriteAsync(Encoding.UTF8.GetBytes($"{index}")).ConfigureAwait(false);
                    }
                    else
                    {
                        await udp.SendAsync(Encoding.UTF8.GetBytes($"{index}")).ConfigureAwait(false);
                    }
                    try
                    {
                        using CancellationTokenSource cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(1000));
                        var result = await udp.ReceiveAsync(cts.Token).ConfigureAwait(false);
                        Console.Write($"{Encoding.UTF8.GetString(result.Buffer)},");
                        array[index] = Encoding.UTF8.GetString(result.Buffer);
                    }
                    catch (Exception)
                    {
                        array[index] = "MISS";
                        Console.Write("MISS,");
                    }
                    await Task.Delay(1000).ConfigureAwait(false);
                }
            }
            Console.WriteLine();
            var sb = new StringBuilder();
            sb.AppendLine($"|{string.Join(" | ", Enumerable.Range(0, col).Select(j => $"{j}"))}|");
            sb.AppendLine($"|{string.Join("|", Enumerable.Repeat("---", col))}|");
            for (int i = 0; i < row; i++)
            {
                sb.AppendLine($"|{string.Join("|", Enumerable.Range(0, col).Select(j => array[i * col + j]))}|");
            }
            Console.WriteLine(sb.ToString());

        }
        static async void RunServer(bool fec, IPEndPoint ep)
        {
            LinkerFecCodec linkerFecCodec = new LinkerFecCodec(new LinkerFecOptions
            {
                SourceSymbolsPerBlock = 10,
                RepairSymbolsPerBlock = 2,
                SymbolSize = 1433,

            });
            byte[] decodeBuffer = new byte[linkerFecCodec.Options.MaxDecodeBufferSize];
            LinkerFecDecodedPacketKind[] kinds = new LinkerFecDecodedPacketKind[linkerFecCodec.Options.SourceSymbolsPerBlock + linkerFecCodec.Options.RepairSymbolsPerBlock];

            UdpClient udp = new UdpClient(ep);
            while (true)
            {
                var result = await udp.ReceiveAsync().ConfigureAwait(false);
                if (fec)
                {
                    if (linkerFecCodec.TryDecodeFrame(result.Buffer, decodeBuffer.AsMemory(), kinds, out int bytesWritten, out int packetCount))
                    {
                        Memory<byte> packets = decodeBuffer.AsMemory(0, bytesWritten);
                        for (int i = 0; i < packetCount; i++)
                        {
                            int packetLength = BinaryPrimitives.ReadInt32LittleEndian(packets.Span);
                            Memory<byte> packet = packets.Slice(sizeof(int), packetLength);

                            if (kinds[i] == LinkerFecDecodedPacketKind.Source)
                            {
                                await udp.SendAsync(packet, result.RemoteEndPoint).ConfigureAwait(false);
                            }
                            else
                            {
                                await udp.SendAsync(Encoding.UTF8.GetBytes("FEC"), result.RemoteEndPoint).ConfigureAwait(false);
                            }
                            
                            packets = packets.Slice(sizeof(int) + packetLength);
                        }
                    }
                }
                else
                {
                    await udp.SendAsync(result.Buffer, result.RemoteEndPoint).ConfigureAwait(false);
                }
            }
        }
    }
}
