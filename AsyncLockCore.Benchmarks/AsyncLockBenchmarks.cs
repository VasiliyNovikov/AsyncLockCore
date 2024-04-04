﻿using System.Threading;
using System.Threading.Tasks;

using BenchmarkDotNet.Attributes;

namespace AsyncLockCore.Benchmarks;

[MemoryDiagnoser]
public class AsyncLockBenchmarks
{
    private readonly AsyncReaderWriterLock _readWriterLock = new();
    private readonly SemaphoreSlim _semaphore = new(1, 1);
    private readonly WaitCallback _disposeGuard = state => ((AsyncReaderWriterLock.Guard)state!).Dispose();
    private readonly WaitCallback _releaseSemaphore = state => ((SemaphoreSlim)state!).Release();

    [Benchmark]
    public async Task AsyncReadWriterLock_Read()
    {
        using var guard = await _readWriterLock.Read().ConfigureAwait(false);
    }

    [Benchmark]
    public async Task AsyncReadWriterLock_Write()
    {
        using var guard = await _readWriterLock.Write().ConfigureAwait(false);
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
        var writeGuard = await _readWriterLock.Write().ConfigureAwait(false);
        var readTask = _readWriterLock.Read();
        writeGuard.Dispose();
        using var guard = await readTask.ConfigureAwait(false);
    }

    [Benchmark]
    public async Task AsyncReadWriterLock_Wait_Write()
    {
        var readGuard = await _readWriterLock.Read().ConfigureAwait(false);
        var writeTask = _readWriterLock.Write();
        readGuard.Dispose();
        using var guard = await writeTask.ConfigureAwait(false);
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
        var writeGuard = await _readWriterLock.Write().ConfigureAwait(false);
        var readTask = _readWriterLock.Read();
        ThreadPool.QueueUserWorkItem(_disposeGuard, writeGuard);
        using var guard = await readTask.ConfigureAwait(false);
    }

    [Benchmark]
    public async Task AsyncReadWriterLock_Wait_Write_Async()
    {
        var readGuard = await _readWriterLock.Read().ConfigureAwait(false);
        var writeTask = _readWriterLock.Write();
        ThreadPool.QueueUserWorkItem(_disposeGuard, readGuard);
        using var guard = await writeTask.ConfigureAwait(false);
    }

    [Benchmark]
    public async Task SemaphoreSlim_Wait_Lock_Async()
    {
        await _semaphore.WaitAsync().ConfigureAwait(false);
        var lockTask = _semaphore.WaitAsync();
        ThreadPool.QueueUserWorkItem(_releaseSemaphore, _semaphore);
        await lockTask.ConfigureAwait(false);
        _semaphore.Release();
    }
}