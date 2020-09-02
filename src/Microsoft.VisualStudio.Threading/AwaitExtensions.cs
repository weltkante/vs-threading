/********************************************************
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
