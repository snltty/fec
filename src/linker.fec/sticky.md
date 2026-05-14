## 小包合并 encode 思路

1. A 产生业务包，`StickyPacketEncoder.WriteAsync(packet)` 写入时统一转成 `[4-byte little-endian length][packet]`。
2. A 用发送线程持续调用 `StickyPacketEncoder.ReadAsync`。`Pipe.Reader.ReadAsync` 有数据就返回，所以不额外加 flush/超时策略；只做机会式合并，不为了合并而增加延迟。
3. `LinkerFecCodec.EncodePacket` 的输入始终是一个或多个完整 `[length][packet]` record；单包普通 FEC 也使用同样格式。
4. Sticky encoder 每次只取完整 record 列表，并把它作为一个普通 FEC 包交给 `LinkerFecCodec.EncodePacket`。每批同时受两个上限约束：业务 payload 总长度不超过 `SymbolSize * SourceSymbolsPerBlock`，原始业务包数量不超过 `SourceSymbolsPerBlock`；本地 4-byte record length 不占用 source symbol 容量。
5. `EncodePacket` 会把每个 record 作为一个 source symbol/frame，不再把多个小 record 塞进同一个 source frame。比如 10/2 满批会输出 `10 source + 2 repair`，避免丢一个 source frame 就丢掉整批小包。
6. Sticky encoder 输出与普通 packetized encode 一致：连续的 `[4-byte little-endian frame length][FEC frame]`。调用方逐个 UDP datagram 发送时读取本地 4-byte frame length，只发送后面的 FEC frame；这个 4-byte frame length 不计入网络带宽。
7. B 收到 FEC frame 后直接调用 `LinkerFecCodec.DecodeFrame(frame, destination)`。
8. Decode 成功时，`destination[..decodedLength]` 就是完整业务 record 列表：`[4-byte little-endian length][packet][length][packet]...`，业务方只按长度前缀读取即可，不需要 `StickyPacketDecoder`。
