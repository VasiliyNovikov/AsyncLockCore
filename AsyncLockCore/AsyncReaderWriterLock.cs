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

    private Scope? _incomingQueueFirst;
    private Scope? _incomingQueueLast;
    private Scope? _inProgressQueueFirst;
    private Scope? _inProgressQueueLast;
    private Scope? _freeStackHead;

    public AsyncReaderWriterLock()
    {
        _cancellationCallback = state => TryCancel((Scope)state!);
        _syncEnterCallback = state =>
        {
            var scope = (Scope)state!;
            scope.EnterContinuation!(scope.EnterContinuationState);
        };
        _execEnterCallback = state =>
        {
            var scope = (Scope)state!;
            scope.EnterContinuation!(scope.EnterContinuationState);
        };
        _syncAndExecEnterCallback = state => ExecutionContext.Run(((Scope)state!).EnterContinuationExecutionContext!, _execEnterCallback, state);
    }

    public ValueTask<Scope> Read(CancellationToken cancellationToken = default) => Enter(false, cancellationToken);
    public ValueTask<Scope> Write(CancellationToken cancellationToken = default) => Enter(true, cancellationToken);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private ValueTask<Scope> Enter(bool isWrite, CancellationToken cancellationToken)
    {
        return cancellationToken.IsCancellationRequested
            ? new ValueTask<Scope>(Task.FromCanceled<Scope>(cancellationToken))
            : TryEnter(isWrite, cancellationToken, out var scope)
                ? new ValueTask<Scope>(scope)
                : new ValueTask<Scope>(scope, 0);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private bool TryEnter(bool isWrite, CancellationToken cancellationToken, out Scope scope)
    {
        scope = StackTryPopLockFree(ref _freeStackHead) ?? new Scope(this);
        scope.IsWrite = isWrite;

        bool locked = false;
        try
        {
            _lock.Enter(ref locked);

            var isEntered = _incomingQueueFirst is null && (_inProgressQueueLast is null || !isWrite && !_inProgressQueueLast.IsWrite);
            var canBeCanceled = !isEntered && cancellationToken.CanBeCanceled;

            scope.Status = isEntered ? ValueTaskSourceStatus.Succeeded : ValueTaskSourceStatus.Pending;
            scope.CanBeCanceled = canBeCanceled;
            scope.CancellationRegistration = canBeCanceled ? cancellationToken.Register(_cancellationCallback, scope) : default;

            if (isEntered)
                QueueAddLast(ref _inProgressQueueFirst, ref _inProgressQueueLast, scope);
            else
                QueueAddLast(ref _incomingQueueFirst, ref _incomingQueueLast, scope);
            return isEntered;
        }
        finally
        {
            if (locked)
                _lock.Exit(false);
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void Exit(Scope scope)
    {
        bool locked = false;
        try
        {
            _lock.Enter(ref locked);

            QueueRemove(ref _inProgressQueueFirst, ref _inProgressQueueLast, scope);
        }
        finally
        {
            if (locked)
                _lock.Exit(false);
        }

        Update();

        scope.EnterContinuation = null;
        scope.EnterContinuationState = null;
        scope.EnterContinuationExecutionContext = null;
        scope.EnterContinuationSynchronizationContext = null;
        StackPushLockFree(ref _freeStackHead, scope);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void TryCancel(Scope scope)
    {
        // Status changes only one way so it is safe to ckeck it 1st time outside of the lock
        if (scope.Status != ValueTaskSourceStatus.Pending)
            return;

        bool locked = false;
        try
        {
            _lock.Enter(ref locked);

            if (scope.Status != ValueTaskSourceStatus.Pending)
                return;

            QueueRemove(ref _incomingQueueFirst, ref _incomingQueueLast, scope);
            scope.Status = ValueTaskSourceStatus.Canceled;
        }
        finally
        {
            if (locked)
                _lock.Exit(false);
        }

        Update();

        // It is safe do it otuside of the lock since Status is already "Canceled" and continuation-related fields can't be modified
        InvokeEnterContinuation(scope);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void AttachOnCompleted(Scope scope, Action<object?> continuation, object? state, ValueTaskSourceOnCompletedFlags flags)
    {
        // Status changes only one way so it is safe to ckeck it 1st time outside of the lock
        if (scope.Status == ValueTaskSourceStatus.Pending)
        {
            // We need to capture these ouside of the lock since they can be expensive
            var executionContext = flags.HasFlag(ValueTaskSourceOnCompletedFlags.FlowExecutionContext) ? ExecutionContext.Capture() : null;
            var synchronizationContext = flags.HasFlag(ValueTaskSourceOnCompletedFlags.UseSchedulingContext) ? SynchronizationContext.Current : null;

            bool locked = false;
            try
            {
                _lock.Enter(ref locked);

                if (scope.Status == ValueTaskSourceStatus.Pending)
                {
                    scope.EnterContinuation = continuation;
                    scope.EnterContinuationState = state;
                    scope.EnterContinuationExecutionContext = executionContext;
                    scope.EnterContinuationSynchronizationContext = synchronizationContext;
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
        Scope scope;
        bool result;

        bool locked = false;
        try
        {
            _lock.Enter(ref locked);

            if (_incomingQueueFirst is null || _inProgressQueueLast is not null && (_inProgressQueueLast.IsWrite || _incomingQueueFirst.IsWrite))
                return false;

            scope = QueueRemoveFirst(ref _incomingQueueFirst, ref _incomingQueueLast);
            QueueAddLast(ref _inProgressQueueFirst, ref _inProgressQueueLast, scope);

            Debug.Assert(scope.Status == ValueTaskSourceStatus.Pending);

            scope.Status = ValueTaskSourceStatus.Succeeded;
            result = !scope.IsWrite && _incomingQueueFirst is { IsWrite: false };
        }
        finally
        {
            if (locked)
                _lock.Exit(false);
        }

        // It is safe to do it outside of the lock since Status is already "Succeeded" and these fields can't be modified
        if (scope.CanBeCanceled)
            scope.CancellationRegistration.Dispose();

        // It is safe do it otuside of the lock since Status is already "Succeeded" and continuation-related fields can't be modified
        InvokeEnterContinuation(scope);

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
    private void InvokeEnterContinuation(Scope scope)
    {
        if (scope.EnterContinuation is null)
            return;

        var syncContext = scope.EnterContinuationSynchronizationContext;
        var execContext = scope.EnterContinuationExecutionContext;
        if (syncContext is not null && syncContext != SynchronizationContext.Current)
        {
            var callback = execContext is null ? _syncEnterCallback : _syncAndExecEnterCallback;
            syncContext.Post(callback, scope);
        }
        else if (execContext is not null)
            ExecutionContext.Run(execContext, _execEnterCallback, scope);
        else
            scope.EnterContinuation!(scope.EnterContinuationState);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void QueueAddLast(ref Scope? first, ref Scope? last, Scope scope)
    {
        if (last is null)
            first = scope;
        else
        {
            scope.Previous = last;
            last.Next = scope;
        }
        last = scope;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void QueueRemove(ref Scope? first, ref Scope? last, Scope scope)
    {
        var previous = scope.Previous;
        if (previous is null)
            first = scope.Next;
        else
            previous.Next = scope.Next;

        var next = scope.Next;
        if (next is null)
            last = scope.Previous;
        else
            next.Previous = scope.Previous;

        scope.Next = null;
        scope.Previous = null;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Scope QueueRemoveFirst(ref Scope? first, ref Scope? last)
    {
        Debug.Assert(first is not null);
        var scope = first!;
        first = scope.Next;
        if (first is null)
            last = null;
        else
            first.Previous = null;

        scope.Next = null;
        return scope;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void StackPushLockFree(ref Scope? head, Scope scope)
    {
        Scope? oldHead;
        do
        {
            oldHead = head;
            scope.Next = oldHead;
        }
        while (Interlocked.CompareExchange(ref head, scope, oldHead) != oldHead);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Scope? StackTryPopLockFree(ref Scope? head)
    {
        Scope? scope;
        Scope? newHead;
        do
        {
            scope = head;
            if (scope is null)
                return null;
            newHead = scope.Next;
        }
        while (Interlocked.CompareExchange(ref head, newHead, scope) != scope);
        scope.Next = null;
        return scope;
    }

    public sealed class Scope : IDisposable, IValueTaskSource<Scope>
    {
        private readonly AsyncReaderWriterLock _owner;

        internal Scope? Next;
        internal Scope? Previous;
        internal bool IsWrite;
        internal volatile ValueTaskSourceStatus Status;
        internal bool CanBeCanceled;
        internal CancellationTokenRegistration CancellationRegistration;
        internal Action<object?>? EnterContinuation;
        internal object? EnterContinuationState;
        internal ExecutionContext? EnterContinuationExecutionContext;
        internal SynchronizationContext? EnterContinuationSynchronizationContext;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal Scope(AsyncReaderWriterLock owner) => _owner = owner;

        public void Dispose() => _owner.Exit(this);

        Scope IValueTaskSource<Scope>.GetResult(short token)
        {
            return Status switch
            {
                ValueTaskSourceStatus.Pending => throw new InvalidOperationException(),
                ValueTaskSourceStatus.Canceled => throw new TaskCanceledException(),
                _ => this,
            };
        }

        ValueTaskSourceStatus IValueTaskSource<Scope>.GetStatus(short token) => Status;

        void IValueTaskSource<Scope>.OnCompleted(Action<object?> continuation, object? state, short token, ValueTaskSourceOnCompletedFlags flags) => _owner.AttachOnCompleted(this, continuation, state, flags);
    }
}