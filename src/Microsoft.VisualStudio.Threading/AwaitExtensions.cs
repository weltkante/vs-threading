﻿/********************************************************
*                                                        *
*   © Copyright (C) Microsoft. All rights reserved.      *
*                                                        *
*********************************************************/

namespace Microsoft.VisualStudio.Threading
{
    using System;
    using System.Collections.Generic;
    using System.ComponentModel;
    using System.Diagnostics;
    using System.Diagnostics.CodeAnalysis;
    using System.Runtime.CompilerServices;
    using System.Security;
    using System.Threading;
    using System.Threading.Tasks;
    using Microsoft.Win32;
    using Microsoft.Win32.SafeHandles;

    /// <summary>
    /// Extension methods and awaitables for .NET 4.5.
    /// </summary>
    public static partial class AwaitExtensions
    {
        /// <summary>
        /// Gets an awaiter that schedules continuations on the specified scheduler.
        /// </summary>
        /// <param name="scheduler">The task scheduler used to execute continuations.</param>
        /// <returns>An awaitable.</returns>
        public static TaskSchedulerAwaiter GetAwaiter(this TaskScheduler scheduler)
        {
            Requires.NotNull(scheduler, nameof(scheduler));
            return new TaskSchedulerAwaiter(scheduler);
        }

        /// <summary>
        /// Gets an awaitable that schedules continuations on the specified scheduler.
        /// </summary>
        /// <param name="scheduler">The task scheduler used to execute continuations.</param>
        /// <param name="alwaysYield">A value indicating whether the caller should yield even if
        /// already executing on the desired task scheduler.</param>
        /// <returns>An awaitable.</returns>
        public static TaskSchedulerAwaitable SwitchTo(this TaskScheduler scheduler, bool alwaysYield = false)
        {
            Requires.NotNull(scheduler, nameof(scheduler));
            return new TaskSchedulerAwaitable(scheduler, alwaysYield);
        }

        /// <summary>
        /// Provides await functionality for ordinary <see cref="WaitHandle"/>s.
        /// </summary>
        /// <param name="handle">The handle to wait on.</param>
        /// <returns>The awaiter.</returns>
        public static TaskAwaiter GetAwaiter(this WaitHandle handle)
        {
            Requires.NotNull(handle, nameof(handle));
            Task task = handle.ToTask();
            return task.GetAwaiter();
        }

        /// <summary>
        /// Returns a task that completes when the process exits and provides the exit code of that process.
        /// </summary>
        /// <param name="process">The process to wait for exit.</param>
        /// <param name="cancellationToken">
        /// A token whose cancellation will cause the returned Task to complete
        /// before the process exits in a faulted state with an <see cref="OperationCanceledException"/>.
        /// This token has no effect on the <paramref name="process"/> itself.
        /// </param>
        /// <returns>A task whose result is the <see cref="Process.ExitCode"/> of the <paramref name="process"/>.</returns>
        public static async Task<int> WaitForExitAsync(this Process process, CancellationToken cancellationToken = default(CancellationToken))
        {
            Requires.NotNull(process, nameof(process));

            var tcs = new TaskCompletionSource<int>();
            EventHandler exitHandler = (s, e) =>
            {
                tcs.TrySetResult(process.ExitCode);
            };
            try
            {
                process.EnableRaisingEvents = true;
                process.Exited += exitHandler;
                if (process.HasExited)
                {
                    // Allow for the race condition that the process has already exited.
                    tcs.TrySetResult(process.ExitCode);
                }

                using (cancellationToken.Register(() => tcs.TrySetCanceled(cancellationToken)))
                {
                    return await tcs.Task.ConfigureAwait(false);
                }
            }
            finally
            {
                process.Exited -= exitHandler;
            }
        }

        /// <summary>
        /// Provides a dedicated thread for requesting registry change notifications.
        /// </summary>
        /// <remarks>
        /// For versions of Windows prior to Windows 8, requesting registry change notifications
        /// required that the thread that made the request remain alive or else the watcher would
        /// simply signal the event and stop watching for changes.
        /// This class provides a single, dedicated thread for requesting such notifications
        /// so that they don't get canceled when a thread happens to exit.
        /// The dedicated thread is released when no one is watching the registry any more.
        /// </remarks>
        private static class DownlevelRegistryWatcherSupport
        {
            /// <summary>
            /// The size of the stack allocated for a thread that expects to stay within just a few methods in depth.
            /// </summary>
            /// <remarks>
            /// The default stack size for a thread is 1MB.
            /// </remarks>
            private const int SmallThreadStackSize = 100 * 1024;

            /// <summary>
            /// The object to lock when accessing any fields.
            /// This is also the object that is waited on by the dedicated thread,
            /// and may be pulsed by others to wake the dedicated thread to do some work.
            /// </summary>
            private static readonly object SyncObject = new object();

            /// <summary>
            /// A queue of actions the dedicated thread should take.
            /// </summary>
            private static readonly Queue<Tuple<Action, TaskCompletionSource<EmptyStruct>>> PendingWork = new Queue<Tuple<Action, TaskCompletionSource<EmptyStruct>>>();

            /// <summary>
            /// The number of callers that still have an interest in the survival of the dedicated thread.
            /// The dedicated thread will exit when this value reaches 0.
            /// </summary>
            private static int keepAliveCount;

            /// <summary>
            /// The thread that should stay alive and be dequeuing <see cref="PendingWork"/>.
            /// </summary>
            private static Thread? liveThread;

            /// <summary>
            /// Executes some action on a long-lived thread.
            /// </summary>
            /// <param name="action">The delegate to execute.</param>
            /// <returns>
            /// A task that either faults with the exception thrown by <paramref name="action"/>
            /// or completes after successfully executing the delegate
            /// with a result that should be disposed when it is safe to terminate the long-lived thread.
            /// </returns>
            /// <remarks>
            /// This thread never posts to <see cref="SynchronizationContext.Current"/>, so it is safe
            /// to call this method and synchronously block on its result.
            /// </remarks>
            internal static async Task<IDisposable> ExecuteOnDedicatedThreadAsync(Action action)
            {
                Requires.NotNull(action, nameof(action));

                var tcs = new TaskCompletionSource<EmptyStruct>();
                bool keepAliveCountIncremented = false;
                try
                {
                    lock (SyncObject)
                    {
                        PendingWork.Enqueue(Tuple.Create(action, tcs));

                        try
                        {
                            // This block intentionally left blank.
                        }
                        finally
                        {
                            // We make these two assignments within a finally block
                            // to guard against an untimely ThreadAbortException causing
                            // us to execute just one of them.
                            keepAliveCountIncremented = true;
                            ++keepAliveCount;
                        }

                        if (keepAliveCount == 1)
                        {
                            Assumes.Null(liveThread);
                            liveThread = new Thread(Worker, SmallThreadStackSize)
                            {
                                IsBackground = true,
                                Name = "Registry watcher",
                            };
                            liveThread.Start();
                        }
                        else
                        {
                            // There *could* temporarily be multiple threads in some race conditions.
                            // Pulse all of them so that the live one is sure to get the message.
                            Monitor.PulseAll(SyncObject);
                        }
                    }

                    await tcs.Task.ConfigureAwait(false);
                    return new ThreadHandleRelease();
                }
                catch
                {
                    if (keepAliveCountIncremented)
                    {
                        // Our caller will never have a chance to release their claim on the dedicated thread,
                        // so do it for them.
                        ReleaseRefOnDedicatedThread();
                    }

                    throw;
                }
            }

            /// <summary>
            /// Decrements the count of interested parties in the live thread,
            /// and helps it to terminate if necessary.
            /// </summary>
            private static void ReleaseRefOnDedicatedThread()
            {
                lock (SyncObject)
                {
                    if (--keepAliveCount == 0)
                    {
                        liveThread = null;

                        // Wake up any obsolete thread(s) so they can go to exit.
                        Monitor.PulseAll(SyncObject);
                    }
                }
            }

            /// <summary>
            /// Executes thread-affinitized work from a queue until both the queue is empty
            /// and any lingering interest in the survival of the dedicated thread has been released.
            /// </summary>
            /// <remarks>
            /// This method serves as the <see cref="ThreadStart"/> for our dedicated thread.
            /// </remarks>
            [SuppressMessage("Microsoft.Design", "CA1031:DoNotCatchGeneralExceptionTypes", Justification = "We store the exception in a Task.")]
            private static void Worker()
            {
                while (true)
                {
                    Tuple<Action, TaskCompletionSource<EmptyStruct>>? work = null;
                    lock (SyncObject)
                    {
                        if (Thread.CurrentThread != liveThread)
                        {
                            // Regardless of our PendingWork and keepAliveCount,
                            // it isn't meant for this thread any more.
                            // This happens when keepAliveCount (at least temporarily)
                            // hits 0, so this thread must be assumed to be on its exit path,
                            // and another thread will be spawned to process new requests.
                            Assumes.True(liveThread != null || (keepAliveCount == 0 && PendingWork.Count == 0));
                            return;
                        }

                        if (PendingWork.Count > 0)
                        {
                            work = PendingWork.Dequeue();
                        }
                        else if (keepAliveCount == 0)
                        {
                            // No work, and no reason to stay alive. Exit the thread.
                            return;
                        }
                        else
                        {
                            // Sleep until another thread wants to wake us up with a Pulse.
                            Monitor.Wait(SyncObject);
                        }
                    }

                    if (work != null)
                    {
                        try
                        {
                            work.Item1();
                            work.Item2.SetResult(EmptyStruct.Instance);
                        }
                        catch (Exception ex)
                        {
                            work.Item2.SetException(ex);
                        }
                    }
                }
            }

            /// <summary>
            /// Decrements the dedicated thread use counter by at most one upon disposal.
            /// </summary>
            private class ThreadHandleRelease : IDisposable
            {
                /// <summary>
                /// A value indicating whether this instance has already been disposed.
                /// </summary>
                private bool disposed;

                /// <summary>
                /// Release the keep alive count reserved by this instance.
                /// </summary>
                public void Dispose()
                {
                    lock (SyncObject)
                    {
                        if (!this.disposed)
                        {
                            this.disposed = true;
                            ReleaseRefOnDedicatedThread();
                        }
                    }
                }
            }
        }

        /// <summary>
        /// Converts a <see cref="YieldAwaitable"/> to a <see cref="ConfiguredTaskYieldAwaitable"/>.
        /// </summary>
        /// <param name="yieldAwaitable">The result of <see cref="Task.Yield()"/>.</param>
        /// <param name="continueOnCapturedContext">A value indicating whether the continuation should run on the captured <see cref="SynchronizationContext"/>, if any.</param>
        /// <returns>An awaitable.</returns>
        [System.Diagnostics.CodeAnalysis.SuppressMessage("Microsoft.Usage", "CA1801:ReviewUnusedParameters", MessageId = "yieldAwaitable", Justification = "This allows the extension method syntax to work.")]
        public static ConfiguredTaskYieldAwaitable ConfigureAwait(this YieldAwaitable yieldAwaitable, bool continueOnCapturedContext)
        {
            return new ConfiguredTaskYieldAwaitable(continueOnCapturedContext);
        }

        /// <summary>
        /// Gets an awaitable that schedules the continuation with a preference to executing synchronously on the callstack that completed the <see cref="Task"/>,
        /// without regard to thread ID or any <see cref="SynchronizationContext"/> that may be applied when the continuation is scheduled or when the antecedent completes.
        /// </summary>
        /// <param name="antecedent">The task to await on.</param>
        /// <returns>An awaitable.</returns>
        /// <remarks>
        /// If there is not enough stack space remaining on the thread that is completing the <paramref name="antecedent"/> <see cref="Task"/>,
        /// the continuation may be scheduled on the threadpool.
        /// </remarks>
        public static ExecuteContinuationSynchronouslyAwaitable ConfigureAwaitRunInline(this Task antecedent)
        {
            Requires.NotNull(antecedent, nameof(antecedent));

            return new ExecuteContinuationSynchronouslyAwaitable(antecedent);
        }

        /// <summary>
        /// Gets an awaitable that schedules the continuation with a preference to executing synchronously on the callstack that completed the <see cref="Task"/>,
        /// without regard to thread ID or any <see cref="SynchronizationContext"/> that may be applied when the continuation is scheduled or when the antecedent completes.
        /// </summary>
        /// <typeparam name="T">The type of value returned by the awaited <see cref="Task"/>.</typeparam>
        /// <param name="antecedent">The task to await on.</param>
        /// <returns>An awaitable.</returns>
        /// <remarks>
        /// If there is not enough stack space remaining on the thread that is completing the <paramref name="antecedent"/> <see cref="Task"/>,
        /// the continuation may be scheduled on the threadpool.
        /// </remarks>
        public static ExecuteContinuationSynchronouslyAwaitable<T> ConfigureAwaitRunInline<T>(this Task<T> antecedent)
        {
            Requires.NotNull(antecedent, nameof(antecedent));

            return new ExecuteContinuationSynchronouslyAwaitable<T>(antecedent);
        }

        /// <summary>
        /// An awaitable that executes continuations on the specified task scheduler.
        /// </summary>
        [System.Diagnostics.CodeAnalysis.SuppressMessage("Microsoft.Performance", "CA1815:OverrideEqualsAndOperatorEqualsOnValueTypes")]
        public readonly struct TaskSchedulerAwaitable
        {
            /// <summary>
            /// The scheduler for continuations.
            /// </summary>
            private readonly TaskScheduler taskScheduler;

            /// <summary>
            /// A value indicating whether the awaitable will always call the caller to yield.
            /// </summary>
            private readonly bool alwaysYield;

            /// <summary>
            /// Initializes a new instance of the <see cref="TaskSchedulerAwaitable"/> struct.
            /// </summary>
            /// <param name="taskScheduler">The task scheduler used to execute continuations.</param>
            /// <param name="alwaysYield">A value indicating whether the caller should yield even if
            /// already executing on the desired task scheduler.</param>
            public TaskSchedulerAwaitable(TaskScheduler taskScheduler, bool alwaysYield = false)
            {
                Requires.NotNull(taskScheduler, nameof(taskScheduler));

                this.taskScheduler = taskScheduler;
                this.alwaysYield = alwaysYield;
            }

            /// <summary>
            /// Gets an awaitable that schedules continuations on the specified scheduler.
            /// </summary>
            [System.Diagnostics.CodeAnalysis.SuppressMessage("Microsoft.Design", "CA1024:UsePropertiesWhereAppropriate")]
            public TaskSchedulerAwaiter GetAwaiter()
            {
                return new TaskSchedulerAwaiter(this.taskScheduler, this.alwaysYield);
            }
        }

        /// <summary>
        /// An awaiter returned from <see cref="GetAwaiter(TaskScheduler)"/>.
        /// </summary>
        [System.Diagnostics.CodeAnalysis.SuppressMessage("Microsoft.Performance", "CA1815:OverrideEqualsAndOperatorEqualsOnValueTypes")]
        public readonly struct TaskSchedulerAwaiter : ICriticalNotifyCompletion
        {
            /// <summary>
            /// The scheduler for continuations.
            /// </summary>
            private readonly TaskScheduler scheduler;

            /// <summary>
            /// A value indicating whether <see cref="IsCompleted"/>
            /// should always return false.
            /// </summary>
            private readonly bool alwaysYield;

            /// <summary>
            /// Initializes a new instance of the <see cref="TaskSchedulerAwaiter"/> struct.
            /// </summary>
            /// <param name="scheduler">The scheduler for continuations.</param>
            /// <param name="alwaysYield">A value indicating whether the caller should yield even if
            /// already executing on the desired task scheduler.</param>
            public TaskSchedulerAwaiter(TaskScheduler scheduler, bool alwaysYield = false)
            {
                this.scheduler = scheduler;
                this.alwaysYield = alwaysYield;
            }

            /// <summary>
            /// Gets a value indicating whether no yield is necessary.
            /// </summary>
            /// <value><c>true</c> if the caller is already running on that TaskScheduler.</value>
            public bool IsCompleted
            {
                get
                {
                    if (this.alwaysYield)
                    {
                        return false;
                    }

                    // We special case the TaskScheduler.Default since that is semantically equivalent to being
                    // on a ThreadPool thread, and there are various ways to get on those threads.
                    // TaskScheduler.Current is never null.  Even if no scheduler is really active and the current
                    // thread is not a threadpool thread, TaskScheduler.Current == TaskScheduler.Default, so we have
                    // to protect against that case too.
                    bool isThreadPoolThread = Thread.CurrentThread.IsThreadPoolThread;
                    return (this.scheduler == TaskScheduler.Default && isThreadPoolThread)
                        || (this.scheduler == TaskScheduler.Current && TaskScheduler.Current != TaskScheduler.Default);
                }
            }

            /// <summary>
            /// Schedules a continuation to execute using the specified task scheduler.
            /// </summary>
            /// <param name="continuation">The delegate to invoke.</param>
            public void OnCompleted(Action continuation)
            {
                if (this.scheduler == TaskScheduler.Default)
                {
                    ThreadPool.QueueUserWorkItem(state => ((Action)state!)(), continuation);
                }
                else
                {
                    Task.Factory.StartNew(continuation, CancellationToken.None, TaskCreationOptions.None, this.scheduler);
                }
            }

            /// <summary>
            /// Schedules a continuation to execute using the specified task scheduler
            /// without capturing the ExecutionContext.
            /// </summary>
            /// <param name="continuation">The action.</param>
            public void UnsafeOnCompleted(Action continuation)
            {
                if (this.scheduler == TaskScheduler.Default)
                {
                    ThreadPool.UnsafeQueueUserWorkItem(state => ((Action)state!)(), continuation);
                }
                else
                {
                    // There is no API for scheduling a Task without capturing the ExecutionContext.
                    Task.Factory.StartNew(continuation, CancellationToken.None, TaskCreationOptions.None, this.scheduler);
                }
            }

            /// <summary>
            /// Does nothing.
            /// </summary>
            [System.Diagnostics.CodeAnalysis.SuppressMessage("Microsoft.Performance", "CA1822:MarkMembersAsStatic")]
            public void GetResult()
            {
            }
        }

        /// <summary>
        /// An awaitable that will always lead the calling async method to yield,
        /// then immediately resume, possibly on the original <see cref="SynchronizationContext"/>.
        /// </summary>
        [System.Diagnostics.CodeAnalysis.SuppressMessage("Microsoft.Performance", "CA1815:OverrideEqualsAndOperatorEqualsOnValueTypes")]
        public readonly struct ConfiguredTaskYieldAwaitable
        {
            /// <summary>
            /// A value indicating whether the continuation should run on the captured <see cref="SynchronizationContext"/>, if any.
            /// </summary>
            private readonly bool continueOnCapturedContext;

            /// <summary>
            /// Initializes a new instance of the <see cref="ConfiguredTaskYieldAwaitable"/> struct.
            /// </summary>
            /// <param name="continueOnCapturedContext">A value indicating whether the continuation should run on the captured <see cref="SynchronizationContext"/>, if any.</param>
            public ConfiguredTaskYieldAwaitable(bool continueOnCapturedContext)
            {
                this.continueOnCapturedContext = continueOnCapturedContext;
            }

            /// <summary>
            /// Gets the awaiter.
            /// </summary>
            /// <returns>The awaiter.</returns>
            [System.Diagnostics.CodeAnalysis.SuppressMessage("Microsoft.Design", "CA1024:UsePropertiesWhereAppropriate")]
            public ConfiguredTaskYieldAwaiter GetAwaiter() => new ConfiguredTaskYieldAwaiter(this.continueOnCapturedContext);
        }

        /// <summary>
        /// An awaiter that will always lead the calling async method to yield,
        /// then immediately resume, possibly on the original <see cref="SynchronizationContext"/>.
        /// </summary>
        [System.Diagnostics.CodeAnalysis.SuppressMessage("Microsoft.Performance", "CA1815:OverrideEqualsAndOperatorEqualsOnValueTypes")]
        public readonly struct ConfiguredTaskYieldAwaiter : ICriticalNotifyCompletion
        {
            /// <summary>
            /// A value indicating whether the continuation should run on the captured <see cref="SynchronizationContext"/>, if any.
            /// </summary>
            private readonly bool continueOnCapturedContext;

            /// <summary>
            /// Initializes a new instance of the <see cref="ConfiguredTaskYieldAwaiter"/> struct.
            /// </summary>
            /// <param name="continueOnCapturedContext">A value indicating whether the continuation should run on the captured <see cref="SynchronizationContext"/>, if any.</param>
            public ConfiguredTaskYieldAwaiter(bool continueOnCapturedContext)
            {
                this.continueOnCapturedContext = continueOnCapturedContext;
            }

            /// <summary>
            /// Gets a value indicating whether the caller should yield.
            /// </summary>
            /// <value>Always false.</value>
            [System.Diagnostics.CodeAnalysis.SuppressMessage("Microsoft.Performance", "CA1822:MarkMembersAsStatic")]
            public bool IsCompleted => false;

            /// <summary>
            /// Schedules a continuation to execute immediately (but not synchronously).
            /// </summary>
            /// <param name="continuation">The delegate to invoke.</param>
            public void OnCompleted(Action continuation)
            {
                if (this.continueOnCapturedContext)
                {
                    Task.Yield().GetAwaiter().OnCompleted(continuation);
                }
                else
                {
                    ThreadPool.QueueUserWorkItem(state => ((Action)state!)(), continuation);
                }
            }

            /// <summary>
            /// Schedules a delegate for execution at the conclusion of a task's execution
            /// without capturing the ExecutionContext.
            /// </summary>
            /// <param name="continuation">The action.</param>
            public void UnsafeOnCompleted(Action continuation)
            {
                if (this.continueOnCapturedContext)
                {
                    Task.Yield().GetAwaiter().UnsafeOnCompleted(continuation);
                }
                else
                {
                    ThreadPool.UnsafeQueueUserWorkItem(state => ((Action)state!)(), continuation);
                }
            }

            /// <summary>
            /// Does nothing.
            /// </summary>
            [System.Diagnostics.CodeAnalysis.SuppressMessage("Microsoft.Performance", "CA1822:MarkMembersAsStatic")]
            public void GetResult()
            {
            }
        }

        /// <summary>
        /// A Task awaitable that has affinity to executing callbacks synchronously on the completing callstack.
        /// </summary>
        [System.Diagnostics.CodeAnalysis.SuppressMessage("Microsoft.Performance", "CA1815:OverrideEqualsAndOperatorEqualsOnValueTypes")]
        public readonly struct ExecuteContinuationSynchronouslyAwaitable
        {
            /// <summary>
            /// The task whose completion will execute the continuation.
            /// </summary>
            private readonly Task antecedent;

            /// <summary>
            /// Initializes a new instance of the <see cref="ExecuteContinuationSynchronouslyAwaitable"/> struct.
            /// </summary>
            /// <param name="antecedent">The task whose completion will execute the continuation.</param>
            public ExecuteContinuationSynchronouslyAwaitable(Task antecedent)
            {
                Requires.NotNull(antecedent, nameof(antecedent));
                this.antecedent = antecedent;
            }

            /// <summary>
            /// Gets the awaiter.
            /// </summary>
            /// <returns>The awaiter.</returns>
            [System.Diagnostics.CodeAnalysis.SuppressMessage("Microsoft.Design", "CA1024:UsePropertiesWhereAppropriate")]
            public ExecuteContinuationSynchronouslyAwaiter GetAwaiter() => new ExecuteContinuationSynchronouslyAwaiter(this.antecedent);
        }

        /// <summary>
        /// A Task awaiter that has affinity to executing callbacks synchronously on the completing callstack.
        /// </summary>
        [System.Diagnostics.CodeAnalysis.SuppressMessage("Microsoft.Performance", "CA1815:OverrideEqualsAndOperatorEqualsOnValueTypes")]
        public readonly struct ExecuteContinuationSynchronouslyAwaiter : INotifyCompletion
        {
            /// <summary>
            /// The task whose completion will execute the continuation.
            /// </summary>
            private readonly Task antecedent;

            /// <summary>
            /// Initializes a new instance of the <see cref="ExecuteContinuationSynchronouslyAwaiter"/> struct.
            /// </summary>
            /// <param name="antecedent">The task whose completion will execute the continuation.</param>
            public ExecuteContinuationSynchronouslyAwaiter(Task antecedent)
            {
                Requires.NotNull(antecedent, nameof(antecedent));
                this.antecedent = antecedent;
            }

            /// <summary>
            /// Gets a value indicating whether the antedent <see cref="Task"/> has already completed.
            /// </summary>
            public bool IsCompleted => this.antecedent.IsCompleted;

            /// <summary>
            /// Rethrows any exception thrown by the antecedent.
            /// </summary>
            public void GetResult() => this.antecedent.GetAwaiter().GetResult();

            /// <summary>
            /// Schedules a callback to run when the antecedent task completes.
            /// </summary>
            /// <param name="continuation">The callback to invoke.</param>
            public void OnCompleted(Action continuation)
            {
                Requires.NotNull(continuation, nameof(continuation));

                this.antecedent.ContinueWith(
                    (_, s) => ((Action)s!)(),
                    continuation,
                    CancellationToken.None,
                    TaskContinuationOptions.ExecuteSynchronously,
                    TaskScheduler.Default);
            }
        }

        /// <summary>
        /// A Task awaitable that has affinity to executing callbacks synchronously on the completing callstack.
        /// </summary>
        /// <typeparam name="T">The type of value returned by the awaited <see cref="Task"/>.</typeparam>
        [System.Diagnostics.CodeAnalysis.SuppressMessage("Microsoft.Performance", "CA1815:OverrideEqualsAndOperatorEqualsOnValueTypes")]
        public readonly struct ExecuteContinuationSynchronouslyAwaitable<T>
        {
            /// <summary>
            /// The task whose completion will execute the continuation.
            /// </summary>
            private readonly Task<T> antecedent;

            /// <summary>
            /// Initializes a new instance of the <see cref="ExecuteContinuationSynchronouslyAwaitable{T}"/> struct.
            /// </summary>
            /// <param name="antecedent">The task whose completion will execute the continuation.</param>
            public ExecuteContinuationSynchronouslyAwaitable(Task<T> antecedent)
            {
                Requires.NotNull(antecedent, nameof(antecedent));
                this.antecedent = antecedent;
            }

            /// <summary>
            /// Gets the awaiter.
            /// </summary>
            /// <returns>The awaiter.</returns>
            [System.Diagnostics.CodeAnalysis.SuppressMessage("Microsoft.Design", "CA1024:UsePropertiesWhereAppropriate")]
            public ExecuteContinuationSynchronouslyAwaiter<T> GetAwaiter() => new ExecuteContinuationSynchronouslyAwaiter<T>(this.antecedent);
        }

        /// <summary>
        /// A Task awaiter that has affinity to executing callbacks synchronously on the completing callstack.
        /// </summary>
        /// <typeparam name="T">The type of value returned by the awaited <see cref="Task"/>.</typeparam>
        [System.Diagnostics.CodeAnalysis.SuppressMessage("Microsoft.Performance", "CA1815:OverrideEqualsAndOperatorEqualsOnValueTypes")]
        public readonly struct ExecuteContinuationSynchronouslyAwaiter<T> : INotifyCompletion
        {
            /// <summary>
            /// The task whose completion will execute the continuation.
            /// </summary>
            private readonly Task<T> antecedent;

            /// <summary>
            /// Initializes a new instance of the <see cref="ExecuteContinuationSynchronouslyAwaiter{T}"/> struct.
            /// </summary>
            /// <param name="antecedent">The task whose completion will execute the continuation.</param>
            public ExecuteContinuationSynchronouslyAwaiter(Task<T> antecedent)
            {
                Requires.NotNull(antecedent, nameof(antecedent));
                this.antecedent = antecedent;
            }

            /// <summary>
            /// Gets a value indicating whether the antedent <see cref="Task"/> has already completed.
            /// </summary>
            public bool IsCompleted => this.antecedent.IsCompleted;

            /// <summary>
            /// Rethrows any exception thrown by the antecedent.
            /// </summary>
            [System.Diagnostics.CodeAnalysis.SuppressMessage("Microsoft.Design", "CA1024:UsePropertiesWhereAppropriate")]
            public T GetResult() => this.antecedent.GetAwaiter().GetResult();

            /// <summary>
            /// Schedules a callback to run when the antecedent task completes.
            /// </summary>
            /// <param name="continuation">The callback to invoke.</param>
            public void OnCompleted(Action continuation)
            {
                Requires.NotNull(continuation, nameof(continuation));

                this.antecedent.ContinueWith(
                    (_, s) => ((Action)s!)(),
                    continuation,
                    CancellationToken.None,
                    TaskContinuationOptions.ExecuteSynchronously,
                    TaskScheduler.Default);
            }
        }
    }
}
