using System;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using System.Threading.Tasks.Sources;

namespace AsyncLockCore;

public class AsyncReaderWriterLock
{
    private SpinLock _lock = new(false);

    private readonly Action<object?> _cancellationCallback;
    private readonly SendOrPostCallback _syncEnterCallback;
    private readonly ContextCallback _execEnterCallback;
    private readonly SendOrPostCallback _syncAndExecEnterCallback;

    private Guard? _incomingQueueFirst;
    private Guard? _incomingQueueLast;
    private Guard? _inProgressQueueFirst;
    private Guard? _inProgressQueueLast;
    private Guard? _freeStackHead;

    public AsyncReaderWriterLock()
    {
        _cancellationCallback = state => TryCancel((Guard)state!);
        _syncEnterCallback = state =>
        {
            var guard = (Guard)state!;
            guard.EnterContinuation!(guard.EnterContinuationState);
        };
        _execEnterCallback = state =>
        {
            var guard = (Guard)state!;
            guard.EnterContinuation!(guard.EnterContinuationState);
        };
        _syncAndExecEnterCallback = state => ExecutionContext.Run(((Guard)state!).EnterContinuationExecutionContext!, _execEnterCallback, state);
    }

    public ValueTask<Guard> Read(CancellationToken cancellationToken = default) => Enter(false, cancellationToken);
    public ValueTask<Guard> Write(CancellationToken cancellationToken = default) => Enter(true, cancellationToken);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private ValueTask<Guard> Enter(bool isWrite, CancellationToken cancellationToken)
    {
        return cancellationToken.IsCancellationRequested
            ? new ValueTask<Guard>(Task.FromCanceled<Guard>(cancellationToken))
            : TryEnter(isWrite, cancellationToken, out var guard)
                ? new ValueTask<Guard>(guard)
                : new ValueTask<Guard>(guard, 0);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private bool TryEnter(bool isWrite, CancellationToken cancellationToken, out Guard guard)
    {
        guard = StackTryPopLockFree(ref _freeStackHead) ?? new Guard(this);
        guard.IsWrite = isWrite;

        bool locked = false;
        try
        {
            _lock.Enter(ref locked);

            var isEntered = _incomingQueueFirst is null && (_inProgressQueueLast is null || !isWrite && !_inProgressQueueLast.IsWrite);
            var canBeCanceled = !isEntered && cancellationToken.CanBeCanceled;

            guard.Status = isEntered ? ValueTaskSourceStatus.Succeeded : ValueTaskSourceStatus.Pending;
            guard.CanBeCanceled = canBeCanceled;
            guard.CancellationRegistration = canBeCanceled ? cancellationToken.Register(_cancellationCallback, guard) : default;

            if (isEntered)
                QueueAddLast(ref _inProgressQueueFirst, ref _inProgressQueueLast, guard);
            else
                QueueAddLast(ref _incomingQueueFirst, ref _incomingQueueLast, guard);
            return isEntered;
        }
        finally
        {
            if (locked)
                _lock.Exit(false);
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void Exit(Guard guard)
    {
        bool locked = false;
        try
        {
            _lock.Enter(ref locked);

            QueueRemove(ref _inProgressQueueFirst, ref _inProgressQueueLast, guard);
        }
        finally
        {
            if (locked)
                _lock.Exit(false);
        }

        Update();

        guard.EnterContinuation = null;
        guard.EnterContinuationState = null;
        guard.EnterContinuationExecutionContext = null;
        guard.EnterContinuationSynchronizationContext = null;
        StackPushLockFree(ref _freeStackHead, guard);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void TryCancel(Guard guard)
    {
        // Status changes only one way so it is safe to ckeck it 1st time outside of the lock
        if (guard.Status != ValueTaskSourceStatus.Pending)
            return;

        bool locked = false;
        try
        {
            _lock.Enter(ref locked);

            if (guard.Status != ValueTaskSourceStatus.Pending)
                return;

            QueueRemove(ref _incomingQueueFirst, ref _incomingQueueLast, guard);
            guard.Status = ValueTaskSourceStatus.Canceled;
        }
        finally
        {
            if (locked)
                _lock.Exit(false);
        }

        Update();

        // It is safe do it otuside of the lock since Status is already "Canceled" and continuation-related fields can't be modified
        InvokeEnterContinuation(guard);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void AttachOnCompleted(Guard guard, Action<object?> continuation, object? state, ValueTaskSourceOnCompletedFlags flags)
    {
        // Status changes only one way so it is safe to ckeck it 1st time outside of the lock
        if (guard.Status == ValueTaskSourceStatus.Pending)
        {
            // We need to capture these ouside of the lock since they can be expensive
            var executionContext = flags.HasFlag(ValueTaskSourceOnCompletedFlags.FlowExecutionContext) ? ExecutionContext.Capture() : null;
            var synchronizationContext = flags.HasFlag(ValueTaskSourceOnCompletedFlags.UseSchedulingContext) ? SynchronizationContext.Current : null;

            bool locked = false;
            try
            {
                _lock.Enter(ref locked);

                if (guard.Status == ValueTaskSourceStatus.Pending)
                {
                    guard.EnterContinuation = continuation;
                    guard.EnterContinuationState = state;
                    guard.EnterContinuationExecutionContext = executionContext;
                    guard.EnterContinuationSynchronizationContext = synchronizationContext;
                    return;
                }
            }
            finally
            {
                if (locked)
                    _lock.Exit(false);
            }
        }
        continuation(state);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private bool UpdateOnce()
    {
        Guard guard;
        bool result;

        bool locked = false;
        try
        {
            _lock.Enter(ref locked);

            if (_incomingQueueFirst is null || _inProgressQueueLast is not null && (_inProgressQueueLast.IsWrite || _incomingQueueFirst.IsWrite))
                return false;

            guard = QueueRemoveFirst(ref _incomingQueueFirst, ref _incomingQueueLast);
            QueueAddLast(ref _inProgressQueueFirst, ref _inProgressQueueLast, guard);

            Debug.Assert(guard.Status == ValueTaskSourceStatus.Pending);

            guard.Status = ValueTaskSourceStatus.Succeeded;
            result = !guard.IsWrite && _incomingQueueFirst is { IsWrite: false };
        }
        finally
        {
            if (locked)
                _lock.Exit(false);
        }

        // It is safe to do it outside of the lock since Status is already "Succeeded" and these fields can't be modified
        if (guard.CanBeCanceled)
            guard.CancellationRegistration.Dispose();

        // It is safe do it otuside of the lock since Status is already "Succeeded" and continuation-related fields can't be modified
        InvokeEnterContinuation(guard);

        return result;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void Update()
    {
        while (UpdateOnce())
        {
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void InvokeEnterContinuation(Guard guard)
    {
        if (guard.EnterContinuation is null)
            return;

        var syncContext = guard.EnterContinuationSynchronizationContext;
        var execContext = guard.EnterContinuationExecutionContext;
        if (syncContext is not null && syncContext != SynchronizationContext.Current)
        {
            var callback = execContext is null ? _syncEnterCallback : _syncAndExecEnterCallback;
            syncContext.Post(callback, guard);
        }
        else if (execContext is not null)
            ExecutionContext.Run(execContext, _execEnterCallback, guard);
        else
            guard.EnterContinuation!(guard.EnterContinuationState);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void QueueAddLast(ref Guard? first, ref Guard? last, Guard guard)
    {
        if (last is null)
            first = guard;
        else
        {
            guard.Previous = last;
            last.Next = guard;
        }
        last = guard;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void QueueRemove(ref Guard? first, ref Guard? last, Guard guard)
    {
        var previous = guard.Previous;
        if (previous is null)
            first = guard.Next;
        else
            previous.Next = guard.Next;

        var next = guard.Next;
        if (next is null)
            last = guard.Previous;
        else
            next.Previous = guard.Previous;

        guard.Next = null;
        guard.Previous = null;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Guard QueueRemoveFirst(ref Guard? first, ref Guard? last)
    {
        Debug.Assert(first is not null);
        var guard = first!;
        first = guard.Next;
        if (first is null)
            last = null;
        else
            first.Previous = null;

        guard.Next = null;
        return guard;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void StackPushLockFree(ref Guard? head, Guard guard)
    {
        Guard? oldHead;
        do
        {
            oldHead = head;
            guard.Next = oldHead;
        }
        while (Interlocked.CompareExchange(ref head, guard, oldHead) != oldHead);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Guard? StackTryPopLockFree(ref Guard? head)
    {
        Guard? guard;
        Guard? newHead;
        do
        {
            guard = head;
            if (guard is null)
                return null;
            newHead = guard.Next;
        }
        while (Interlocked.CompareExchange(ref head, newHead, guard) != guard);
        guard.Next = null;
        return guard;
    }

    public sealed class Guard : IDisposable, IValueTaskSource<Guard>
    {
        private readonly AsyncReaderWriterLock _owner;

        internal Guard? Next;
        internal Guard? Previous;
        internal bool IsWrite;
        internal volatile ValueTaskSourceStatus Status;
        internal bool CanBeCanceled;
        internal CancellationTokenRegistration CancellationRegistration;
        internal Action<object?>? EnterContinuation;
        internal object? EnterContinuationState;
        internal ExecutionContext? EnterContinuationExecutionContext;
        internal SynchronizationContext? EnterContinuationSynchronizationContext;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal Guard(AsyncReaderWriterLock owner) => _owner = owner;

        public void Dispose() => _owner.Exit(this);

        Guard IValueTaskSource<Guard>.GetResult(short token)
        {
            return Status switch
            {
                ValueTaskSourceStatus.Pending => throw new InvalidOperationException(),
                ValueTaskSourceStatus.Canceled => throw new TaskCanceledException(),
                _ => this,
            };
        }

        ValueTaskSourceStatus IValueTaskSource<Guard>.GetStatus(short token) => Status;

        void IValueTaskSource<Guard>.OnCompleted(Action<object?> continuation, object? state, short token, ValueTaskSourceOnCompletedFlags flags) => _owner.AttachOnCompleted(this, continuation, state, flags);
    }
}