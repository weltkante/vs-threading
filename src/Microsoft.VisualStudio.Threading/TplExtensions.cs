/********************************************************
*                                                        *
*   © Copyright (C) Microsoft. All rights reserved.      *
*                                                        *
*********************************************************/

namespace Microsoft.VisualStudio.Threading
{
    using System;
    using System.Collections.Generic;
    using System.Runtime.CompilerServices;
    using System.Security;
    using System.Threading;
    using System.Threading.Tasks;

    /// <summary>
    /// Extensions to the Task Parallel Library.
    /// </summary>
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Microsoft.Naming", "CA1704:IdentifiersShouldBeSpelledCorrectly", MessageId = "Tpl")]
    public static partial class TplExtensions
    {
        /// <summary>
        /// A singleton completed task.
        /// </summary>
        [System.Diagnostics.CodeAnalysis.SuppressMessage("Microsoft.Security", "CA2104:DoNotDeclareReadOnlyMutableReferenceTypes")]
        [Obsolete("Use Task.CompletedTask instead.")]
        public static readonly Task CompletedTask = Task.FromResult(default(EmptyStruct));

        /// <summary>
        /// A task that is already canceled.
        /// </summary>
        [System.Diagnostics.CodeAnalysis.SuppressMessage("Microsoft.Security", "CA2104:DoNotDeclareReadOnlyMutableReferenceTypes")]
        [Obsolete("Use Task.FromCanceled instead.")]
        public static readonly Task CanceledTask = Task.FromCanceled(new CancellationToken(canceled: true));

        /// <summary>
        /// A completed task with a <c>true</c> result.
        /// </summary>
        [System.Diagnostics.CodeAnalysis.SuppressMessage("Microsoft.Security", "CA2104:DoNotDeclareReadOnlyMutableReferenceTypes")]
        public static readonly Task<bool> TrueTask = Task.FromResult(true);

        /// <summary>
        /// A completed task with a <c>false</c> result.
        /// </summary>
        [System.Diagnostics.CodeAnalysis.SuppressMessage("Microsoft.Security", "CA2104:DoNotDeclareReadOnlyMutableReferenceTypes")]
        public static readonly Task<bool> FalseTask = Task.FromResult(false);

        /// <summary>
        /// Wait on a task without possibly inlining it to the current thread.
        /// </summary>
        /// <param name="task">The task to wait on.</param>
        public static void WaitWithoutInlining(this Task task)
        {
            Requires.NotNull(task, nameof(task));
            if (!task.IsCompleted)
            {
                // Waiting on a continuation of a task won't ever inline the predecessor (in .NET 4.x anyway).
                var continuation = task.ContinueWith(t => { }, CancellationToken.None, TaskContinuationOptions.RunContinuationsAsynchronously, TaskScheduler.Default);
                continuation.Wait();
            }

            task.Wait(); // purely for exception behavior; alternatively in .NET 4.5 task.GetAwaiter().GetResult();
        }

        /// <summary>
        /// Returns a task that completes as the original task completes or when a timeout expires,
        /// whichever happens first.
        /// </summary>
        /// <param name="task">The task to wait for.</param>
        /// <param name="timeout">The maximum time to wait.</param>
        /// <returns>
        /// A task that completes with the result of the specified <paramref name="task"/> or
        /// faults with a <see cref="TimeoutException"/> if <paramref name="timeout"/> elapses first.
        /// </returns>
        public static async Task WithTimeout(this Task task, TimeSpan timeout)
        {
            Requires.NotNull(task, nameof(task));

            using (var timerCancellation = new CancellationTokenSource())
            {
                Task timeoutTask = Task.Delay(timeout, timerCancellation.Token);
                Task firstCompletedTask = await Task.WhenAny(task, timeoutTask).ConfigureAwait(false);
                if (firstCompletedTask == timeoutTask)
                {
                    throw new TimeoutException();
                }

                // The timeout did not elapse, so cancel the timer to recover system resources.
                timerCancellation.Cancel();

                // re-throw any exceptions from the completed task.
                await task.ConfigureAwait(false);
            }
        }

        /// <summary>
        /// Returns a task that completes as the original task completes or when a timeout expires,
        /// whichever happens first.
        /// </summary>
        /// <typeparam name="T">The type of value returned by the original task.</typeparam>
        /// <param name="task">The task to wait for.</param>
        /// <param name="timeout">The maximum time to wait.</param>
        /// <returns>
        /// A task that completes with the result of the specified <paramref name="task"/> or
        /// faults with a <see cref="TimeoutException"/> if <paramref name="timeout"/> elapses first.
        /// </returns>
        public static async Task<T> WithTimeout<T>(this Task<T> task, TimeSpan timeout)
        {
            await WithTimeout((Task)task, timeout).ConfigureAwait(false);
            return task.GetAwaiter().GetResult();
        }

        /// <summary>
        /// Applies one task's results to another.
        /// </summary>
        /// <typeparam name="T">The type of value returned by a task.</typeparam>
        /// <param name="task">The task whose completion should be applied to another.</param>
        /// <param name="tcs">The task that should receive the completion status.</param>
        [System.Diagnostics.CodeAnalysis.SuppressMessage("Microsoft.Naming", "CA1704:IdentifiersShouldBeSpelledCorrectly", MessageId = "tcs")]
        public static void ApplyResultTo<T>(this Task<T> task, TaskCompletionSource<T> tcs)
        {
            ApplyResultTo(task, tcs, inlineSubsequentCompletion: true);
        }

        /// <summary>
        /// Applies one task's results to another.
        /// </summary>
        /// <typeparam name="T">The type of value returned by a task.</typeparam>
        /// <param name="task">The task whose completion should be applied to another.</param>
        /// <param name="tcs">The task that should receive the completion status.</param>
        [System.Diagnostics.CodeAnalysis.SuppressMessage("Microsoft.Naming", "CA1704:IdentifiersShouldBeSpelledCorrectly", MessageId = "tcs")]
        public static void ApplyResultTo<T>(this Task task, TaskCompletionSource<T> tcs)
            //// where T : defaultable
        {
            Requires.NotNull(task, nameof(task));
            Requires.NotNull(tcs, nameof(tcs));

            if (task.IsCompleted)
            {
                ApplyCompletedTaskResultTo<T>(task, tcs, default(T)!);
            }
            else
            {
                // Using a minimum of allocations (just one task, and no closure) ensure that one task's completion sets equivalent completion on another task.
                task.ContinueWith(
                    (t, s) => ApplyCompletedTaskResultTo(t, (TaskCompletionSource<T>)s!, default(T)!),
                    tcs,
                    CancellationToken.None,
                    TaskContinuationOptions.ExecuteSynchronously,
                    TaskScheduler.Default);
            }
        }

        /// <summary>
        /// Gets a task that will eventually produce the result of another task, when that task finishes.
        /// If that task is instead canceled, its successor will be followed for its result, iteratively.
        /// </summary>
        /// <typeparam name="T">The type of value returned by the task.</typeparam>
        /// <param name="taskToFollow">The task whose result should be returned by the following task.</param>
        /// <param name="ultimateCancellation">A token whose cancellation signals that the following task should be cancelled.</param>
        /// <param name="taskThatFollows">The TaskCompletionSource whose task is to follow.  Leave at <c>null</c> for a new task to be created.</param>
        /// <returns>The following task.</returns>
        public static Task<T> FollowCancelableTaskToCompletion<T>(Func<Task<T>> taskToFollow, CancellationToken ultimateCancellation, TaskCompletionSource<T>? taskThatFollows = null)
        {
            Requires.NotNull(taskToFollow, nameof(taskToFollow));

            var tcs = new TaskCompletionSource<FollowCancelableTaskState<T>, T>(
                    new FollowCancelableTaskState<T>(taskToFollow, ultimateCancellation));

            if (ultimateCancellation.CanBeCanceled)
            {
                var registeredCallback = ultimateCancellation.Register(
                    state =>
                    {
                        var tuple = (Tuple<TaskCompletionSource<FollowCancelableTaskState<T>, T>, CancellationToken>)state!;
                        tuple.Item1.TrySetCanceled(tuple.Item2);
                    },
                    Tuple.Create(tcs, ultimateCancellation));
                tcs.SourceState = tcs.SourceState.WithRegisteredCallback(registeredCallback);
            }

            FollowCancelableTaskToCompletionHelper(tcs, taskToFollow());

            if (taskThatFollows == null)
            {
                return tcs.Task;
            }
            else
            {
                tcs.Task.ApplyResultTo(taskThatFollows);
                return taskThatFollows.Task;
            }
        }

        /// <summary>
        /// Returns an awaitable for the specified task that will never throw, even if the source task
        /// faults or is canceled.
        /// </summary>
        /// <param name="task">The task whose completion should signal the completion of the returned awaitable.</param>
        /// <param name="captureContext">if set to <c>true</c> the continuation will be scheduled on the caller's context; <c>false</c> to always execute the continuation on the threadpool.</param>
        /// <returns>An awaitable.</returns>
        public static NoThrowTaskAwaitable NoThrowAwaitable(this Task task, bool captureContext = true)
        {
            return new NoThrowTaskAwaitable(task, captureContext);
        }

        /// <summary>
        /// Consumes a task and doesn't do anything with it.  Useful for fire-and-forget calls to async methods within async methods.
        /// </summary>
        /// <param name="task">The task whose result is to be ignored.</param>
        [System.Diagnostics.CodeAnalysis.SuppressMessage("Microsoft.Usage", "CA1801:ReviewUnusedParameters", MessageId = "task")]
        public static void Forget(this Task? task)
        {
        }

        /// <summary>
        /// Consumes a <see cref="ValueTask"/> and allows it to be recycled, if applicable.  Useful for fire-and-forget calls to async methods within async methods.
        /// NOTE: APIs should not generally return <see cref="ValueTask"/> if callers aren't 99.9999% likely to await the result immediately.
        /// </summary>
        /// <param name="task">The task whose result is to be ignored.</param>
        public static void Forget(this ValueTask task) => task.Preserve();

        /// <summary>
        /// Consumes a ValueTask and allows it to be recycled, if applicable.  Useful for fire-and-forget calls to async methods within async methods.
        /// NOTE: APIs should not generally return <see cref="ValueTask{T}"/> if callers aren't 99.9999% likely to await the result immediately.
        /// </summary>
        /// <typeparam name="T">The type of value produced by the <paramref name="task"/>.</typeparam>
        /// <param name="task">The task whose result is to be ignored.</param>
        public static void Forget<T>(this ValueTask<T> task) => task.Preserve();

        /// <summary>
        /// Applies one task's results to another.
        /// </summary>
        /// <typeparam name="T">The type of value returned by a task.</typeparam>
        /// <param name="task">The task whose completion should be applied to another.</param>
        /// <param name="tcs">The task that should receive the completion status.</param>
        /// <param name="inlineSubsequentCompletion">
        /// <c>true</c> to complete the supplied <paramref name="tcs"/> as efficiently as possible (inline with the completion of <paramref name="task"/>);
        /// <c>false</c> to complete the <paramref name="tcs"/> asynchronously.
        /// Note if <paramref name="task"/> is completed when this method is invoked, then <paramref name="tcs"/> is always completed synchronously.
        /// </param>
        internal static void ApplyResultTo<T>(this Task<T> task, TaskCompletionSource<T> tcs, bool inlineSubsequentCompletion)
        {
            Requires.NotNull(task, nameof(task));
            Requires.NotNull(tcs, nameof(tcs));

            if (task.IsCompleted)
            {
                ApplyCompletedTaskResultTo(task, tcs);
            }
            else
            {
                // Using a minimum of allocations (just one task, and no closure) ensure that one task's completion sets equivalent completion on another task.
                task.ContinueWith(
                    (t, s) => ApplyCompletedTaskResultTo(t, (TaskCompletionSource<T>)s!),
                    tcs,
                    CancellationToken.None,
                    inlineSubsequentCompletion ? TaskContinuationOptions.ExecuteSynchronously : TaskContinuationOptions.None,
                    TaskScheduler.Default);
            }
        }

        /// <summary>
        /// Returns a reusable task that is already canceled.
        /// </summary>
        /// <typeparam name="T">The type parameter for the returned task.</typeparam>
        internal static Task<T> CanceledTaskOfT<T>() => CanceledTaskOfTCache<T>.CanceledTask;

        /// <summary>
        /// Applies a completed task's results to another.
        /// </summary>
        /// <typeparam name="T">The type of value returned by a task.</typeparam>
        /// <param name="completedTask">The task whose completion should be applied to another.</param>
        /// <param name="taskCompletionSource">The task that should receive the completion status.</param>
        private static void ApplyCompletedTaskResultTo<T>(Task<T> completedTask, TaskCompletionSource<T> taskCompletionSource)
        {
            Assumes.NotNull(completedTask);
            Assumes.True(completedTask.IsCompleted);
            Assumes.NotNull(taskCompletionSource);

            if (completedTask.IsCanceled)
            {
                // NOTE: this is "lossy" in that we don't propagate any CancellationToken that the Task would throw an OperationCanceledException with.
                // Propagating that data would require that we actually cause the completedTask to throw so we can inspect the
                // OperationCanceledException.CancellationToken property, which we consider more costly than it's worth.
                taskCompletionSource.TrySetCanceled();
            }
            else if (completedTask.IsFaulted)
            {
                taskCompletionSource.TrySetException(completedTask.Exception!.InnerExceptions);
            }
            else
            {
                taskCompletionSource.TrySetResult(completedTask.Result);
            }
        }

        /// <summary>
        /// Applies a completed task's results to another.
        /// </summary>
        /// <typeparam name="T">The type of value returned by a task.</typeparam>
        /// <param name="completedTask">The task whose completion should be applied to another.</param>
        /// <param name="taskCompletionSource">The task that should receive the completion status.</param>
        /// <param name="valueOnRanToCompletion">The value to set on the completion source when the source task runs to completion.</param>
        private static void ApplyCompletedTaskResultTo<T>(Task completedTask, TaskCompletionSource<T> taskCompletionSource, T valueOnRanToCompletion)
        {
            Assumes.NotNull(completedTask);
            Assumes.True(completedTask.IsCompleted);
            Assumes.NotNull(taskCompletionSource);

            if (completedTask.IsCanceled)
            {
                // NOTE: this is "lossy" in that we don't propagate any CancellationToken that the Task would throw an OperationCanceledException with.
                // Propagating that data would require that we actually cause the completedTask to throw so we can inspect the
                // OperationCanceledException.CancellationToken property, which we consider more costly than it's worth.
                taskCompletionSource.TrySetCanceled();
            }
            else if (completedTask.IsFaulted)
            {
                taskCompletionSource.TrySetException(completedTask.Exception!.InnerExceptions);
            }
            else
            {
                taskCompletionSource.TrySetResult(valueOnRanToCompletion);
            }
        }

        /// <summary>
        /// Gets a task that will eventually produce the result of another task, when that task finishes.
        /// If that task is instead canceled, its successor will be followed for its result, iteratively.
        /// </summary>
        /// <typeparam name="T">The type of value returned by the task.</typeparam>
        /// <param name="tcs">The TaskCompletionSource whose task is to follow.</param>
        /// <param name="currentTask">The current task.</param>
        /// <returns>
        /// The following task.
        /// </returns>
        private static Task<T> FollowCancelableTaskToCompletionHelper<T>(TaskCompletionSource<FollowCancelableTaskState<T>, T> tcs, Task<T> currentTask)
        {
            Requires.NotNull(tcs, nameof(tcs));
            Requires.NotNull(currentTask, nameof(currentTask));

            currentTask.ContinueWith(
                (t, state) =>
                {
                    var tcsNested = (TaskCompletionSource<FollowCancelableTaskState<T>, T>)state!;
                    switch (t.Status)
                    {
                        case TaskStatus.RanToCompletion:
                            tcsNested.TrySetResult(t.Result);
                            tcsNested.SourceState.RegisteredCallback.Dispose();
                            break;
                        case TaskStatus.Faulted:
                            tcsNested.TrySetException(t.Exception!.InnerExceptions);
                            tcsNested.SourceState.RegisteredCallback.Dispose();
                            break;
                        case TaskStatus.Canceled:
                            var newTask = tcsNested.SourceState.CurrentTask;
                            Assumes.True(newTask != t, "A canceled task was not replaced with a new task.");
                            FollowCancelableTaskToCompletionHelper(tcsNested, newTask);
                            break;
                    }
                },
                tcs,
                tcs.SourceState.UltimateCancellation,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);

            return tcs.Task;
        }

        /// <summary>
        /// An awaitable that wraps a task and never throws an exception when waited on.
        /// </summary>
        [System.Diagnostics.CodeAnalysis.SuppressMessage("Microsoft.Performance", "CA1815:OverrideEqualsAndOperatorEqualsOnValueTypes")]
        public readonly struct NoThrowTaskAwaitable
        {
            /// <summary>
            /// The task.
            /// </summary>
            private readonly Task task;

            /// <summary>
            /// A value indicating whether the continuation should be scheduled on the current sync context.
            /// </summary>
            private readonly bool captureContext;

            /// <summary>
            /// Initializes a new instance of the <see cref="NoThrowTaskAwaitable" /> struct.
            /// </summary>
            /// <param name="task">The task.</param>
            /// <param name="captureContext">Whether the continuation should be scheduled on the current sync context.</param>
            public NoThrowTaskAwaitable(Task task, bool captureContext)
            {
                Requires.NotNull(task, nameof(task));
                this.task = task;
                this.captureContext = captureContext;
            }

            /// <summary>
            /// Gets the awaiter.
            /// </summary>
            /// <returns>The awaiter.</returns>
            [System.Diagnostics.CodeAnalysis.SuppressMessage("Microsoft.Design", "CA1024:UsePropertiesWhereAppropriate")]
            public NoThrowTaskAwaiter GetAwaiter()
            {
                return new NoThrowTaskAwaiter(this.task, this.captureContext);
            }
        }

        /// <summary>
        /// An awaiter that wraps a task and never throws an exception when waited on.
        /// </summary>
        [System.Diagnostics.CodeAnalysis.SuppressMessage("Microsoft.Performance", "CA1815:OverrideEqualsAndOperatorEqualsOnValueTypes")]
        public readonly struct NoThrowTaskAwaiter : ICriticalNotifyCompletion
        {
            /// <summary>
            /// The task.
            /// </summary>
            private readonly Task task;

            /// <summary>
            /// A value indicating whether the continuation should be scheduled on the current sync context.
            /// </summary>
            private readonly bool captureContext;

            /// <summary>
            /// Initializes a new instance of the <see cref="NoThrowTaskAwaiter"/> struct.
            /// </summary>
            /// <param name="task">The task.</param>
            /// <param name="captureContext">if set to <c>true</c> [capture context].</param>
            public NoThrowTaskAwaiter(Task task, bool captureContext)
            {
                Requires.NotNull(task, nameof(task));
                this.task = task;
                this.captureContext = captureContext;
            }

            /// <summary>
            /// Gets a value indicating whether the task has completed.
            /// </summary>
            public bool IsCompleted
            {
                get { return this.task.IsCompleted; }
            }

            /// <summary>
            /// Schedules a delegate for execution at the conclusion of a task's execution.
            /// </summary>
            /// <param name="continuation">The action.</param>
            public void OnCompleted(Action continuation)
            {
                this.task.ConfigureAwait(this.captureContext).GetAwaiter().OnCompleted(continuation);
            }

            /// <summary>
            /// Schedules a delegate for execution at the conclusion of a task's execution
            /// without capturing the ExecutionContext.
            /// </summary>
            /// <param name="continuation">The action.</param>
            public void UnsafeOnCompleted(Action continuation)
            {
                this.task.ConfigureAwait(this.captureContext).GetAwaiter().UnsafeOnCompleted(continuation);
            }

            /// <summary>
            /// Does nothing.
            /// </summary>
            [System.Diagnostics.CodeAnalysis.SuppressMessage("Microsoft.Performance", "CA1822:MarkMembersAsStatic")]
            public void GetResult()
            {
                // Never throw here.
            }
        }

        /// <summary>
        /// A state bag for the <see cref="FollowCancelableTaskToCompletion"/> method.
        /// </summary>
        /// <typeparam name="T">The type of value ultimately returned.</typeparam>
        private readonly struct FollowCancelableTaskState<T>
        {
            /// <summary>
            /// The delegate that returns the task to follow.
            /// </summary>
            private readonly Func<Task<T>> getTaskToFollow;

            /// <summary>
            /// Initializes a new instance of the <see cref="FollowCancelableTaskState{T}"/> struct.
            /// </summary>
            /// <param name="getTaskToFollow">The get task to follow.</param>
            /// <param name="cancellationToken">The cancellation token.</param>
            internal FollowCancelableTaskState(Func<Task<T>> getTaskToFollow, CancellationToken cancellationToken)
                : this(getTaskToFollow, registeredCallback: default, cancellationToken)
            {
            }

            /// <summary>
            /// Initializes a new instance of the <see cref="FollowCancelableTaskState{T}"/> struct.
            /// </summary>
            /// <param name="getTaskToFollow">The get task to follow.</param>
            /// <param name="registeredCallback">The cancellation token registration to dispose of when the task completes normally.</param>
            /// <param name="cancellationToken">The cancellation token.</param>
            private FollowCancelableTaskState(Func<Task<T>> getTaskToFollow, CancellationTokenRegistration registeredCallback, CancellationToken cancellationToken)
            {
                Requires.NotNull(getTaskToFollow, nameof(getTaskToFollow));

                this.getTaskToFollow = getTaskToFollow;
                this.RegisteredCallback = registeredCallback;
                this.UltimateCancellation = cancellationToken;
            }

            /// <summary>
            /// Gets the ultimate cancellation token.
            /// </summary>
            internal CancellationToken UltimateCancellation { get; }

            /// <summary>
            /// Gets the cancellation token registration to dispose of when the task completes normally.
            /// </summary>
            internal CancellationTokenRegistration RegisteredCallback { get; }

            /// <summary>
            /// Gets the current task to follow.
            /// </summary>
            internal Task<T> CurrentTask
            {
                get
                {
                    var task = this.getTaskToFollow();
                    Assumes.NotNull(task);
                    return task;
                }
            }

            internal FollowCancelableTaskState<T> WithRegisteredCallback(CancellationTokenRegistration registeredCallback)
                => new FollowCancelableTaskState<T>(this.getTaskToFollow, registeredCallback, this.UltimateCancellation);
        }

        /// <summary>
        /// A task completion source that contains additional state.
        /// </summary>
        /// <typeparam name="TState">The type of the state.</typeparam>
        /// <typeparam name="TResult">The type of the result.</typeparam>
        private class TaskCompletionSource<TState, TResult> : TaskCompletionSource<TResult>
        {
            /// <summary>
            /// Initializes a new instance of the <see cref="TaskCompletionSource{TState, TResult}" /> class.
            /// </summary>
            /// <param name="sourceState">The state to store in the <see cref="SourceState" /> property.</param>
            /// <param name="taskState">State of the task.</param>
            /// <param name="options">The options.</param>
            internal TaskCompletionSource(TState sourceState, object? taskState = null, TaskCreationOptions options = TaskCreationOptions.None)
                : base(taskState, options)
            {
                this.SourceState = sourceState;
            }

            /// <summary>
            /// Gets or sets the state passed into the constructor.
            /// </summary>
            internal TState SourceState { get; set; }
        }

        /// <summary>
        /// A cache for canceled <see cref="Task{T}"/> instances.
        /// </summary>
        /// <typeparam name="T">The type parameter for the returned task.</typeparam>
        private static class CanceledTaskOfTCache<T>
        {
            /// <summary>
            /// A task that is already canceled.
            /// </summary>
            [System.Diagnostics.CodeAnalysis.SuppressMessage("Microsoft.Security", "CA2104:DoNotDeclareReadOnlyMutableReferenceTypes")]
            internal static readonly Task<T> CanceledTask = Task.FromCanceled<T>(new CancellationToken(canceled: true));
        }
    }
}
