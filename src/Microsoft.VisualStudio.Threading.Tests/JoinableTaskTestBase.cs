﻿namespace Microsoft.VisualStudio.Threading.Tests
{
    using System;
    using System.Collections.Generic;
    using System.Diagnostics;
    using System.Linq;
    using System.Runtime.ExceptionServices;
    using System.Text;
    using System.Threading;
    using System.Threading.Tasks;
    using System.Xml.Linq;
    using Xunit;
    using Xunit.Abstractions;

    public abstract class JoinableTaskTestBase : TestBase
    {
        protected const string DgmlNamespace = "http://schemas.microsoft.com/vs/2009/dgml";

        protected JoinableTaskContext context;
        protected JoinableTaskFactory asyncPump;
        protected JoinableTaskCollection? joinableCollection;

        protected int originalThreadManagedId;
        protected SynchronizationContext dispatcherContext;
        protected SingleThreadedTestSynchronizationContext.IFrame testFrame;

        protected JoinableTaskTestBase(ITestOutputHelper logger)
            : base(logger)
        {
            this.dispatcherContext = SingleThreadedTestSynchronizationContext.New();
            SynchronizationContext.SetSynchronizationContext(this.dispatcherContext);
            this.context = this.CreateJoinableTaskContext();
            this.joinableCollection = this.context.CreateCollection();
            this.asyncPump = this.context.CreateFactory(this.joinableCollection);
            this.originalThreadManagedId = Environment.CurrentManagedThreadId;
            this.testFrame = SingleThreadedTestSynchronizationContext.NewFrame();

            // Suppress the assert dialog that appears and causes test runs to hang.
            Trace.Listeners.OfType<DefaultTraceListener>().Single().AssertUiEnabled = false;
        }

        protected virtual JoinableTaskContext CreateJoinableTaskContext()
        {
            return new JoinableTaskContext();
        }

        protected int GetPendingTasksCount()
        {
            IHangReportContributor hangContributor = this.context;
            var contribution = Assert.IsType<JoinableTaskContext.HangReport>(hangContributor.GetHangReport());
            return contribution.Nodes.OfType<JoinableTaskContext.HangReport.TaskNode>().Count();
        }

        protected void SimulateUIThread(Func<Task> testMethod)
        {
            Verify.Operation(this.originalThreadManagedId == Environment.CurrentManagedThreadId, "We can only simulate the UI thread if you're already on it (the starting thread for the test).");

            Exception? failure = null;
            this.dispatcherContext.Post(async delegate
            {
                try
                {
                    await testMethod();
                }
                catch (Exception ex)
                {
                    failure = ex;
                }
                finally
                {
                    this.testFrame.Continue = false;
                }
            }, null);

            SingleThreadedTestSynchronizationContext.PushFrame(this.dispatcherContext, this.testFrame);
            if (failure != null)
            {
                // Rethrow original exception without rewriting the callstack.
                ExceptionDispatchInfo.Capture(failure).Throw();
            }
        }

        protected void PushFrame()
        {
            SingleThreadedTestSynchronizationContext.PushFrame(this.dispatcherContext, this.testFrame);
        }

        protected void PushFrameTillQueueIsEmpty()
        {
            this.dispatcherContext.Post(s => this.testFrame.Continue = false, null);
            this.PushFrame();
        }
    }
}
