using System.Threading;
using System.Threading.Tasks;

using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Configs;

namespace AsyncLockCore.Benchmarks;

[MemoryDiagnoser]
[GroupBenchmarksBy(BenchmarkLogicalGroupRule.ByCategory)]
public class AsyncLockBenchmarks
{
    private readonly SemaphoreSlim _semaphore = new(1, 1);
    private readonly WaitCallback _releaseSemaphore = state => ((SemaphoreSlim)state!).Release();

    private readonly AsyncReaderWriterLock _asyncLock = new();
    private readonly WaitCallback _disposeGuard = state => ((AsyncReaderWriterLock.Guard)state!).Dispose();

#if NET8_0_OR_GREATER
    private readonly DotNext.Threading.AsyncReaderWriterLock _dotNextLock = new();
    private readonly WaitCallback _releaseDotNextLock = state => ((DotNext.Threading.AsyncReaderWriterLock)state!).Release();
#endif

    [BenchmarkCategory("Enter & Exit")]
    [Benchmark(Baseline = true)]
    public async Task Semaphore_Lock()
    {
        await _semaphore.WaitAsync().ConfigureAwait(false);
        _semaphore.Release();
    }

    [BenchmarkCategory("Enter & Exit")]
    [Benchmark]
    public async Task AsyncLock_Read()
    {
        using var guard = await _asyncLock.Read().ConfigureAwait(false);
    }

    [BenchmarkCategory("Enter & Exit")]
    [Benchmark]
    public async Task AsyncLock_Write()
    {
        using var guard = await _asyncLock.Write().ConfigureAwait(false);
    }

#if NET8_0_OR_GREATER
    [BenchmarkCategory("Enter & Exit")]
    [Benchmark]
    public async Task DotNextLock_Read()
    {
        await _dotNextLock.EnterReadLockAsync().ConfigureAwait(false);
        _dotNextLock.Release();
    }

    [BenchmarkCategory("Enter & Exit")]
    [Benchmark]
    public async Task DotNextLock_Write()
    {
        await _dotNextLock.EnterWriteLockAsync().ConfigureAwait(false);
        _dotNextLock.Release();
    }
#endif

    [BenchmarkCategory("Wait & Enter & Exit")]
    [Benchmark(Baseline = true)]
    public async Task Semaphore_Wait_Lock()
    {
        await _semaphore.WaitAsync().ConfigureAwait(false);
        var lockTask = _semaphore.WaitAsync();
        _semaphore.Release();
        await lockTask.ConfigureAwait(false);
        _semaphore.Release();
    }

    [BenchmarkCategory("Wait & Enter & Exit")]
    [Benchmark]
    public async Task AsyncLock_Wait_Read()
    {
        var writeGuard = await _asyncLock.Write().ConfigureAwait(false);
        var readTask = _asyncLock.Read();
        writeGuard.Dispose();
        using var guard = await readTask.ConfigureAwait(false);
    }

    [BenchmarkCategory("Wait & Enter & Exit")]
    [Benchmark]
    public async Task AsyncLock_Wait_Write()
    {
        var readGuard = await _asyncLock.Read().ConfigureAwait(false);
        var writeTask = _asyncLock.Write();
        readGuard.Dispose();
        using var guard = await writeTask.ConfigureAwait(false);
    }

#if NET8_0_OR_GREATER
    [BenchmarkCategory("Wait & Enter & Exit")]
    [Benchmark]
    public async Task DotNextLock_Wait_Read()
    {
        await _dotNextLock.EnterWriteLockAsync().ConfigureAwait(false);
        var readTask = _dotNextLock.EnterReadLockAsync();
        _dotNextLock.Release();
        await readTask.ConfigureAwait(false);
        _dotNextLock.Release();
    }

    [BenchmarkCategory("Wait & Enter & Exit")]
    [Benchmark]
    public async Task DotNextLock_Wait_Write()
    {
        await _dotNextLock.EnterReadLockAsync().ConfigureAwait(false);
        var writeTask = _dotNextLock.EnterWriteLockAsync();
        _dotNextLock.Release();
        await writeTask.ConfigureAwait(false);
        _dotNextLock.Release();
    }
#endif

    [BenchmarkCategory("Async Wait & Enter & Exit")]
    [Benchmark(Baseline = true)]
    public async Task Semaphore_Wait_Lock_Async()
    {
        await _semaphore.WaitAsync().ConfigureAwait(false);
        var lockTask = _semaphore.WaitAsync();
        ThreadPool.QueueUserWorkItem(_releaseSemaphore, _semaphore);
        await lockTask.ConfigureAwait(false);
        _semaphore.Release();
    }

    [BenchmarkCategory("Async Wait & Enter & Exit")]
    [Benchmark]
    public async Task AsyncLock_Wait_Read_Async()
    {
        var writeGuard = await _asyncLock.Write().ConfigureAwait(false);
        var readTask = _asyncLock.Read();
        ThreadPool.QueueUserWorkItem(_disposeGuard, writeGuard);
        using var guard = await readTask.ConfigureAwait(false);
    }

    [BenchmarkCategory("Async Wait & Enter & Exit")]
    [Benchmark]
    public async Task AsyncLock_Wait_Write_Async()
    {
        var readGuard = await _asyncLock.Read().ConfigureAwait(false);
        var writeTask = _asyncLock.Write();
        ThreadPool.QueueUserWorkItem(_disposeGuard, readGuard);
        using var guard = await writeTask.ConfigureAwait(false);
    }

#if NET8_0_OR_GREATER
    [BenchmarkCategory("Async Wait & Enter & Exit")]
    [Benchmark]
    public async Task DotNextLock_Wait_Read_Async()
    {
        await _dotNextLock.EnterWriteLockAsync().ConfigureAwait(false);
        var readTask = _dotNextLock.EnterReadLockAsync();
        ThreadPool.QueueUserWorkItem(_releaseDotNextLock, _dotNextLock);
        await readTask.ConfigureAwait(false);
        _dotNextLock.Release();
    }

    [BenchmarkCategory("Async Wait & Enter & Exit")]
    [Benchmark]
    public async Task DotNextLock_Wait_Write_Async()
    {
        await _dotNextLock.EnterReadLockAsync().ConfigureAwait(false);
        var writeTask = _dotNextLock.EnterWriteLockAsync();
        ThreadPool.QueueUserWorkItem(_releaseDotNextLock, _dotNextLock);
        await writeTask.ConfigureAwait(false);
        _dotNextLock.Release();
    }
#endif
}