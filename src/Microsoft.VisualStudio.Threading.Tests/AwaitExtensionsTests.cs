//-----------------------------------------------------------------------
// <copyright file="AwaitExtensionsTests.cs" company="Microsoft">
//     Copyright (c) Microsoft. All rights reserved.
// </copyright>
//-----------------------------------------------------------------------

namespace Microsoft.VisualStudio.Threading.Tests
{
    using System;
    using System.Collections.Generic;
    using System.Diagnostics;
    using System.IO;
    using System.Linq;
    using System.Runtime.InteropServices;
    using System.Threading;
    using System.Threading.Tasks;
    using Microsoft.Win32;
    using Xunit;
    using Xunit.Abstractions;

    public partial class AwaitExtensionsTests : TestBase
    {
        public AwaitExtensionsTests(ITestOutputHelper logger)
            : base(logger)
        {
        }

        public enum NamedSyncContexts
        {
            None,
            A,
            B,
        }

        [Theory, CombinatorialData]
        public void TaskYield_ConfigureAwait_OnDispatcher(bool useDefaultYield)
        {
            this.ExecuteOnDispatcher(async delegate
            {
                var asyncLocal = new AsyncLocal<object> { Value = 3 };
                int originalThreadId = Environment.CurrentManagedThreadId;

                if (useDefaultYield)
                {
                    await Task.Yield();
                }
                else
                {
                    await Task.Yield().ConfigureAwait(true);
                }

                Assert.Equal(3, asyncLocal.Value);
                Assert.Equal(originalThreadId, Environment.CurrentManagedThreadId);

                await Task.Yield().ConfigureAwait(false);
                Assert.Equal(3, asyncLocal.Value);
                Assert.NotEqual(originalThreadId, Environment.CurrentManagedThreadId);
            });
        }

        [Theory, CombinatorialData]
        public void TaskYield_ConfigureAwait_OnDefaultSyncContext(bool useDefaultYield)
        {
            Task.Run(async delegate
            {
                SynchronizationContext defaultSyncContext = new SynchronizationContext();
                SynchronizationContext.SetSynchronizationContext(defaultSyncContext);
                var asyncLocal = new AsyncLocal<object> { Value = 3 };

                if (useDefaultYield)
                {
                    await Task.Yield();
                }
                else
                {
                    await Task.Yield().ConfigureAwait(true);
                }

                Assert.Equal(3, asyncLocal.Value);
                Assert.Null(SynchronizationContext.Current);

                await Task.Yield().ConfigureAwait(false);
                Assert.Equal(3, asyncLocal.Value);
                Assert.Null(SynchronizationContext.Current);
            });
        }

        [Theory, CombinatorialData]
        public void TaskYield_ConfigureAwait_OnNonDefaultTaskScheduler(bool useDefaultYield)
        {
            var scheduler = new MockTaskScheduler();
            Task.Factory.StartNew(
                async delegate
                {
                    var asyncLocal = new AsyncLocal<object> { Value = 3 };

                    if (useDefaultYield)
                    {
                        await Task.Yield();
                    }
                    else
                    {
                        await Task.Yield().ConfigureAwait(true);
                    }

                    Assert.Equal(3, asyncLocal.Value);
                    Assert.Same(scheduler, TaskScheduler.Current);

                    await Task.Yield().ConfigureAwait(false);
                    Assert.Equal(3, asyncLocal.Value);
                    Assert.NotSame(scheduler, TaskScheduler.Current);
                },
                CancellationToken.None,
                TaskCreationOptions.None,
                scheduler).Unwrap().GetAwaiter().GetResult();
        }

        [Theory, CombinatorialData]
        public void TaskYield_ConfigureAwait_OnDefaultTaskScheduler(bool useDefaultYield)
        {
            Task.Run(
                async delegate
                {
                    var asyncLocal = new AsyncLocal<object> { Value = 3 };

                    if (useDefaultYield)
                    {
                        await Task.Yield();
                    }
                    else
                    {
                        await Task.Yield().ConfigureAwait(true);
                    }

                    Assert.Equal(3, asyncLocal.Value);
                    Assert.Same(TaskScheduler.Default, TaskScheduler.Current);

                    await Task.Yield().ConfigureAwait(false);
                    Assert.Equal(3, asyncLocal.Value);
                    Assert.Same(TaskScheduler.Default, TaskScheduler.Current);
                }).GetAwaiter().GetResult();
        }

        [Theory, CombinatorialData]
        public async Task TaskYield_ConfigureAwait_OnCompleted_CapturesExecutionContext(bool captureContext)
        {
            var taskResultSource = new TaskCompletionSource<object?>();
            AsyncLocal<object?> asyncLocal = new AsyncLocal<object?>();
            asyncLocal.Value = "expected";
            Task.Yield().ConfigureAwait(captureContext).GetAwaiter().OnCompleted(delegate
            {
                try
                {
                    Assert.Equal("expected", asyncLocal.Value);
                    taskResultSource.SetResult(null);
                }
                catch (Exception ex)
                {
                    taskResultSource.SetException(ex);
                }
            });
            asyncLocal.Value = null;
            await taskResultSource.Task;
        }

        [Fact]
        public void AwaitCustomTaskScheduler()
        {
            var mockScheduler = new MockTaskScheduler();
            Task.Run(async delegate
            {
                await mockScheduler;
                Assert.Equal(1, mockScheduler.QueueTaskInvocations);
                Assert.Same(mockScheduler, TaskScheduler.Current);
            }).GetAwaiter().GetResult();
        }

        [Fact]
        public void AwaitCustomTaskSchedulerNoYieldWhenAlreadyOnScheduler()
        {
            var mockScheduler = new MockTaskScheduler();
            Task.Run(async delegate
            {
                await mockScheduler;
                Assert.True(mockScheduler.GetAwaiter().IsCompleted, "We're already executing on that scheduler, so no reason to yield.");
            }).GetAwaiter().GetResult();
        }

        [Fact]
        public void AwaitThreadPoolSchedulerYieldsOnNonThreadPoolThreads()
        {
            // In some test runs (including VSTS cloud test), this test runs on a threadpool thread.
            if (Thread.CurrentThread.IsThreadPoolThread)
            {
                var testResult = Task.Factory.StartNew(delegate
                    {
                        Assert.False(Thread.CurrentThread.IsThreadPoolThread); // avoid infinite recursion if it doesn't get us off a threadpool thread.
                        this.AwaitThreadPoolSchedulerYieldsOnNonThreadPoolThreads();
                    },
                    CancellationToken.None,
                    TaskCreationOptions.LongRunning, // arrange for a dedicated thread
                    TaskScheduler.Default);
                testResult.GetAwaiter().GetResult(); // rethrow any test failure.
                return; // skip the test that runs on this thread.
            }

            Assert.False(TaskScheduler.Default.GetAwaiter().IsCompleted);
        }

        [Fact]
        public void AwaitThreadPoolSchedulerNoYieldOnThreadPool()
        {
            Task.Run(delegate
            {
                Assert.True(Thread.CurrentThread.IsThreadPoolThread, "Test depends on thread looking like threadpool thread.");
                Assert.True(TaskScheduler.Default.GetAwaiter().IsCompleted);
            }).GetAwaiter().GetResult();
        }

        [Theory]
        [CombinatorialData]
        public void ConfigureAwaitRunInline_NoExtraThreadSwitching(NamedSyncContexts invokeOn, NamedSyncContexts completeOn)
        {
            // Set up various SynchronizationContexts that we may invoke or complete the async method with.
            var aSyncContext = SingleThreadedTestSynchronizationContext.New();
            var bSyncContext = SingleThreadedTestSynchronizationContext.New();
            var invokeOnSyncContext = invokeOn == NamedSyncContexts.None ? null
                : invokeOn == NamedSyncContexts.A ? aSyncContext
                : invokeOn == NamedSyncContexts.B ? bSyncContext
                : throw new ArgumentOutOfRangeException(nameof(invokeOn));
            var completeOnSyncContext = completeOn == NamedSyncContexts.None ? null
                : completeOn == NamedSyncContexts.A ? aSyncContext
                : completeOn == NamedSyncContexts.B ? bSyncContext
                : throw new ArgumentOutOfRangeException(nameof(completeOn));

            // Set up a single-threaded SynchronizationContext that we'll invoke the async method within.
            SynchronizationContext.SetSynchronizationContext(invokeOnSyncContext);

            var unblockAsyncMethod = new TaskCompletionSource<bool>();
            var asyncTask = AwaitThenGetThreadAsync(unblockAsyncMethod.Task);

            SynchronizationContext.SetSynchronizationContext(completeOnSyncContext);
            unblockAsyncMethod.SetResult(true);

            // Confirm that setting the intermediate task allowed the async method to complete immediately, using our thread to do it.
            Assert.True(asyncTask.IsCompleted);
            Assert.Equal(Environment.CurrentManagedThreadId, asyncTask.Result);

            async Task<int> AwaitThenGetThreadAsync(Task antecedent)
            {
                await antecedent.ConfigureAwaitRunInline();
                return Environment.CurrentManagedThreadId;
            }
        }

        [Theory]
        [CombinatorialData]
        public void ConfigureAwaitRunInlineOfT_NoExtraThreadSwitching(NamedSyncContexts invokeOn, NamedSyncContexts completeOn)
        {
            // Set up various SynchronizationContexts that we may invoke or complete the async method with.
            var aSyncContext = SingleThreadedTestSynchronizationContext.New();
            var bSyncContext = SingleThreadedTestSynchronizationContext.New();
            var invokeOnSyncContext = invokeOn == NamedSyncContexts.None ? null
                : invokeOn == NamedSyncContexts.A ? aSyncContext
                : invokeOn == NamedSyncContexts.B ? bSyncContext
                : throw new ArgumentOutOfRangeException(nameof(invokeOn));
            var completeOnSyncContext = completeOn == NamedSyncContexts.None ? null
                : completeOn == NamedSyncContexts.A ? aSyncContext
                : completeOn == NamedSyncContexts.B ? bSyncContext
                : throw new ArgumentOutOfRangeException(nameof(completeOn));

            // Set up a single-threaded SynchronizationContext that we'll invoke the async method within.
            SynchronizationContext.SetSynchronizationContext(invokeOnSyncContext);

            var unblockAsyncMethod = new TaskCompletionSource<bool>();
            var asyncTask = AwaitThenGetThreadAsync(unblockAsyncMethod.Task);

            SynchronizationContext.SetSynchronizationContext(completeOnSyncContext);
            unblockAsyncMethod.SetResult(true);

            // Confirm that setting the intermediate task allowed the async method to complete immediately, using our thread to do it.
            Assert.True(asyncTask.IsCompleted);
            Assert.Equal(Environment.CurrentManagedThreadId, asyncTask.Result);

            async Task<int> AwaitThenGetThreadAsync(Task<bool> antecedent)
            {
                bool result = await antecedent.ConfigureAwaitRunInline();
                Assert.True(result);
                return Environment.CurrentManagedThreadId;
            }
        }

        [Fact]
        public void ConfigureAwaitRunInline_AlreadyCompleted()
        {
            var asyncTask = AwaitThenGetThreadAsync(Task.FromResult(true));
            Assert.True(asyncTask.IsCompleted);
            Assert.Equal(Environment.CurrentManagedThreadId, asyncTask.Result);

            async Task<int> AwaitThenGetThreadAsync(Task antecedent)
            {
                await antecedent.ConfigureAwaitRunInline();
                return Environment.CurrentManagedThreadId;
            }
        }

        [Fact]
        public void ConfigureAwaitRunInlineOfT_AlreadyCompleted()
        {
            var asyncTask = AwaitThenGetThreadAsync(Task.FromResult(true));
            Assert.True(asyncTask.IsCompleted);
            Assert.Equal(Environment.CurrentManagedThreadId, asyncTask.Result);

            async Task<int> AwaitThenGetThreadAsync(Task<bool> antecedent)
            {
                bool result = await antecedent.ConfigureAwaitRunInline();
                Assert.True(result);
                return Environment.CurrentManagedThreadId;
            }
        }

        [Fact]
        public async Task AwaitTaskScheduler_UnsafeOnCompleted_DoesNotCaptureExecutionContext()
        {
            var taskResultSource = new TaskCompletionSource<object?>();
            AsyncLocal<object?> asyncLocal = new AsyncLocal<object?>();
            asyncLocal.Value = "expected";
            TaskScheduler.Default.GetAwaiter().UnsafeOnCompleted(delegate
            {
                try
                {
                    Assert.Null(asyncLocal.Value);
                    taskResultSource.SetResult(null);
                }
                catch (Exception ex)
                {
                    taskResultSource.SetException(ex);
                }
            });
            asyncLocal.Value = null;
            await taskResultSource.Task;
        }

        [Theory, CombinatorialData]
        public async Task AwaitTaskScheduler_OnCompleted_CapturesExecutionContext(bool defaultTaskScheduler)
        {
            var taskResultSource = new TaskCompletionSource<object?>();
            AsyncLocal<object?> asyncLocal = new AsyncLocal<object?>();
            asyncLocal.Value = "expected";
            TaskScheduler scheduler = defaultTaskScheduler ? TaskScheduler.Default : new MockTaskScheduler();
            scheduler.GetAwaiter().OnCompleted(delegate
            {
                try
                {
                    Assert.Equal("expected", asyncLocal.Value);
                    taskResultSource.SetResult(null);
                }
                catch (Exception ex)
                {
                    taskResultSource.SetException(ex);
                }
            });
            asyncLocal.Value = null;
            await taskResultSource.Task;
        }

        private class MockTaskScheduler : TaskScheduler
        {
            internal int QueueTaskInvocations { get; set; }

            protected override IEnumerable<Task> GetScheduledTasks()
            {
                return Enumerable.Empty<Task>();
            }

            protected override void QueueTask(Task task)
            {
                this.QueueTaskInvocations++;
                ThreadPool.QueueUserWorkItem(state => this.TryExecuteTask((Task)state!), task);
            }

            protected override bool TryExecuteTaskInline(Task task, bool taskWasPreviouslyQueued)
            {
                return false;
            }
        }
    }
}
