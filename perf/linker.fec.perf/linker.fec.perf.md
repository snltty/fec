# perf要求

以下几个测试，测试完放进README.md的## 性能测试中，不用放太多描述，只要在结果表格顶部说明一下测试环境(包括语言版本，系统版本，内存，CPU等)即可，表格内容按照要求输出即可

1. 测试独立性能encode/decode，要求如下
    1. 用BenchmarkDotNet
    1. 包长：分别 64B/128B/256B/512B/1024B/1400B
    2. 输出结果：操作/包长/平均耗时/吞吐/分配/Gen0/Gen1/Gen2
2. 测试整体性能encode/decode，要求如下
    1. 用BenchmarkDotNet
    2. 包长：分别 64B/128B/256B/512B/1024B/1400B
    3. 输出结果：操作/包长/平均耗时/吞吐/分配/Gen0/Gen1/Gen2
3. 测试包批处理encode decode，要求如下，主要看带宽开销
    1. 只统计带宽比，不必使用BenchmarkDotNet
    2. 配置：sourceSymbolsPerBlock/repairSymbolsPerBlock 10/2
    3. 包长：分别 64B/128B/256B/512B/1024B/1400B
    4. 输出结果，说明一下 encode后是哪些地方占用带宽，比如frame header 占了 13字节
    4. 输出结果：操作/原始包数/FEC帧数/带宽比