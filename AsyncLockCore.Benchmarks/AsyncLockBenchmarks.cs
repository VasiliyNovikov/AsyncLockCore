using System;
using System.Threading;
using System.Threading.Tasks;

using BenchmarkDotNet.Attributes;

namespace AsyncLockCore.Benchmarks;

[MemoryDiagnoser]
public class AsyncLockBenchmarks
{
    private readonly AsyncReaderWriterLock _readWriterLock = new();
    private readonly SemaphoreSlim _semaphore = new(1, 1);
    private readonly Action<AsyncReaderWriterLock.Scope> _disposeScope = scope => scope.Dispose();
    private readonly Action<SemaphoreSlim> _releaseSemaphore = semaphore => semaphore.Release();

    [Benchmark]
    public async Task AsyncReadWriterLock_Read()
    {
        using var scope = await _readWriterLock.Read().ConfigureAwait(false);
    }

    [Benchmark]
    public async Task AsyncReadWriterLock_Write()
    {
        using var scope = await _readWriterLock.Write().ConfigureAwait(false);
    }

    [Benchmark]
    public async Task SemaphoreSlim_Lock()
    {
        await _semaphore.WaitAsync().ConfigureAwait(false);
        _semaphore.Release();
    }


    [Benchmark]
    public async Task AsyncReadWriterLock_Wait_Read()
    {
        var writeScope = await _readWriterLock.Write().ConfigureAwait(false);
        var readTask = _readWriterLock.Read();
        writeScope.Dispose();
        using var scope = await readTask.ConfigureAwait(false);
    }

    [Benchmark]
    public async Task AsyncReadWriterLock_Wait_Write()
    {
        var readScope = await _readWriterLock.Read().ConfigureAwait(false);
        var writeTask = _readWriterLock.Write();
        readScope.Dispose();
        using var scope = await writeTask.ConfigureAwait(false);
    }

    [Benchmark]
    public async Task SemaphoreSlim_Wait_Lock()
    {
        await _semaphore.WaitAsync().ConfigureAwait(false);
        var lockTask = _semaphore.WaitAsync();
        _semaphore.Release();
        await lockTask.ConfigureAwait(false);
        _semaphore.Release();
    }

    [Benchmark]
    public async Task AsyncReadWriterLock_Wait_Read_Async()
    {
        var writeScope = await _readWriterLock.Write().ConfigureAwait(false);
        var readTask = _readWriterLock.Read();
        ThreadPool.QueueUserWorkItem(_disposeScope, writeScope, true);
        using var scope = await readTask.ConfigureAwait(false);
    }

    [Benchmark]
    public async Task AsyncReadWriterLock_Wait_Write_Async()
    {
        var readScope = await _readWriterLock.Read().ConfigureAwait(false);
        var writeTask = _readWriterLock.Write();
        ThreadPool.QueueUserWorkItem(_disposeScope, readScope, true);
        using var scope = await writeTask.ConfigureAwait(false);
    }

    [Benchmark]
    public async Task SemaphoreSlim_Wait_Lock_Async()
    {
        await _semaphore.WaitAsync().ConfigureAwait(false);
        var lockTask = _semaphore.WaitAsync();
        ThreadPool.QueueUserWorkItem(_releaseSemaphore, _semaphore, true);
        await lockTask.ConfigureAwait(false);
        _semaphore.Release();
    }
}