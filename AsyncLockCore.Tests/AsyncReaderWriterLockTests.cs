﻿using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace AsyncLockCore.Tests;

[TestClass]
public class AsyncReaderWriterLockTests
{
    private const int TimeoutMilliseconds = 20;

    [TestMethod]
    public async Task Async_Lock_Read_Allows_Multiple_Concurrent_Readers()
    {
        var lockObj = new AsyncReaderWriterLock();
        using var readGuard1 = await lockObj.Read(); // Ensure first read lock is acquired
        using var readGuard2 = await lockObj.Read(); // Ensure second read lock is acquired
    }

    [TestMethod]
    public async Task Async_Lock_Write_Exclusive_Access()
    {
        using var cancellation = new CancellationTokenSource(TimeoutMilliseconds);

        var lockObj = new AsyncReaderWriterLock();

        var write1 = lockObj.Write(cancellation.Token).AsTask();
        var write2 = lockObj.Write(cancellation.Token).AsTask();

        using var writeGuard1 = await write1; // Ensure first write lock is acquired

        // Second write should wait
        await Assert.ThrowsExceptionAsync<TaskCanceledException>(() => write2);
        Assert.IsTrue(write2.IsCanceled);
    }

    [TestMethod]
    public async Task Async_Lock_Write_Blocks_Subsequent_Readers()
    {
        using var cancellation = new CancellationTokenSource(TimeoutMilliseconds);

        var lockObj = new AsyncReaderWriterLock();

        var write = lockObj.Write(cancellation.Token).AsTask();
        var read = lockObj.Read(cancellation.Token).AsTask();

        using var writeGuard = await write; // Ensure write lock is acquired

        // Read should wait
        await Assert.ThrowsExceptionAsync<TaskCanceledException>(() => read);
        Assert.IsTrue(read.IsCanceled);
    }

    [TestMethod]
    public async Task Async_Lock_Read_Blocks_Subsequent_Writers()
    {
        using var cancellation = new CancellationTokenSource(TimeoutMilliseconds);

        var lockObj = new AsyncReaderWriterLock();
        var read = lockObj.Read(cancellation.Token).AsTask();
        var write = lockObj.Write(cancellation.Token).AsTask();

        using var readGuard = await read; // Ensure read lock is acquired

        // Write should wait
        await Assert.ThrowsExceptionAsync<TaskCanceledException>(() => write);
        Assert.IsTrue(write.IsCanceled);
    }

    [TestMethod]
    public async Task Async_Lock_Released_Write_Releases_Subsequent_Readers()
    {
        var lockObj = new AsyncReaderWriterLock();

        var write1Guard = await lockObj.Write();

        var read1 = lockObj.Read().AsTask();
        var read2 = lockObj.Read().AsTask();

        await Task.Delay(TimeoutMilliseconds, CancellationToken.None);

        Assert.IsFalse(read1.IsCompleted);
        Assert.IsFalse(read2.IsCompleted);

        write1Guard.Dispose();

        using var read1Guard = await read1;
        using var read2Guard = await read2;
        using var read3Guard = await lockObj.Read(CancellationToken.None);
    }

    [TestMethod]
    public async Task Async_Lock_Readers_And_Writers_Are_Served_In_Order()
    {
        var lockObj = new AsyncReaderWriterLock();

        var write1 = lockObj.Write().AsTask();
        var read1 = lockObj.Read().AsTask();
        var read2 = lockObj.Read().AsTask();
        var write2 = lockObj.Write().AsTask();
        var read3 = lockObj.Read().AsTask();

        using (await write1)
        {
            // read1 should wait
            await Task.Delay(TimeoutMilliseconds);
            Assert.IsFalse(read1.IsCompleted);
        }

        using (await read1)
        using (await read2)
        {
            // write2 should wait
            await Task.Delay(TimeoutMilliseconds);
            Assert.IsFalse(write2.IsCompleted);
        }

        using (await write2)
        {
            // read3 should wait
            await Task.Delay(TimeoutMilliseconds);
            Assert.IsFalse(read3.IsCompleted);
        }

        using var read3Guard = await read3;
    }

    [TestMethod]
    public async Task Async_Lock_Parallel_Read_Write()
    {
        var lockObj = new AsyncReaderWriterLock();

        var counter = 0;
        var list = new List<int>();

        var parallelOperationCount = Environment.ProcessorCount * 2;

        var tasks = new List<Task>();
        for (var i = 0; i < parallelOperationCount; ++i)
        {
            tasks.Add(Writer());
            tasks.Add(Reader());
        }

        await Task.WhenAll(tasks);

        for (var i = 0; i < list.Count - 1; ++i)
            Assert.AreEqual(list[i] + 1, list[i + 1]);

        return;

        async Task Writer()
        {
            var rnd = new Random();
            for (var i = 0; i < 100; ++i)
            {
                await Task.Yield();
                using (await lockObj.Write())
                {
                    list.Add(counter);
                    await Task.Delay(rnd.Next(5));
                    ++counter;
                }
            }
        }

        async Task Reader()
        {
            var rnd = new Random();
            for (var i = 0; i < 100; ++i)
            {
                await Task.Yield();
                using (await lockObj.Read())
                {
                    var value = counter;
                    var count = list.Count;
                    await Task.Delay(rnd.Next(5));
                    Assert.AreEqual(value, counter);
                    Assert.AreEqual(count, list.Count);
                }
            }
        }
    }
}