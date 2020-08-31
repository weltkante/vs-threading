﻿/********************************************************
*                                                        *
*   © Copyright (C) Microsoft. All rights reserved.      *
*                                                        *
*********************************************************/

namespace Microsoft.VisualStudio.Threading
{
    using System;
    using System.Collections.Generic;
    using System.Globalization;
    using System.Linq;
    using System.Text;
    using System.Threading;
    using System.Threading.Tasks;
    using System.Xml.Linq;

    partial class AsyncReaderWriterLock : IHangReportContributor
    {
#pragma warning disable CS1591
        public class HangReport : HangReportContribution
        {
            public class Node
            {
                public string ID;
                public string Label;
                public string[] Flags;
                public string Group;

                public Node(string id, string label, string[] flags, string group)
                {
                    ID = id;
                    Label = label;
                    Flags = flags;
                    Group = group;
                }
            }

            public sealed class Link
            {
                public string Source;
                public string Target;

                public Link(string source, string target)
                {
                    Source = source;
                    Target = target;
                }
            }

            public bool LockAcquired;
            public List<Node> Nodes;
            public List<Link> Links;

            public HangReport(bool lockAcquired, List<Node> nodes, List<Link> links)
            {
                LockAcquired = lockAcquired;
                Nodes = nodes;
                Links = links;
            }

            public override string ToString()
            {
                var sb = new StringBuilder();

                if (!LockAcquired)
                    sb.AppendLine("WARNING: failed to acquire our own lock in formulating this report.");

                foreach (var node in Nodes)
                    sb.AppendLine($"group {node.Group} node {node.ID} label {node.Label} flags {node.Flags}");

                foreach (var link in Links)
                    sb.AppendLine($"dependency {link.Source} -> {link.Target}");

                return sb.ToString();
            }
        }
#pragma warning restore CS1591

        [Flags]
        private enum AwaiterCollection
        {
            None = 0x0,
            Waiting = 0x1,
            Issued = 0x2,
            Released = 0x4,
            ReadLock = 0x10,
            UpgradeableReadLock = 0x20,
            WriteLock = 0x40,
        }

        /// <summary>
        /// Gets a <see cref="SynchronizationContext"/> which, when applied,
        /// suppresses any message pump that may run during synchronous blocks
        /// of the calling thread.
        /// </summary>
        /// <remarks>
        /// The default implementation of this property is effective
        /// in builds of this assembly that target the .NET Framework.
        /// But on builds that target the portable profile, it should be
        /// overridden to provide an effective platform-specific solution.
        /// </remarks>
        protected internal virtual SynchronizationContext NoMessagePumpSynchronizationContext
        {
            get { return NoMessagePumpSyncContext.Default; }
        }

        /// <summary>
        /// Contributes data for a hang report.
        /// </summary>
        /// <returns>The hang report contribution. Null values should be ignored.</returns>
        HangReportContribution IHangReportContributor.GetHangReport()
        {
            return this.GetHangReport();
        }

        /// <summary>
        /// Contributes data for a hang report.
        /// </summary>
        /// <returns>The hang report contribution. Null values should be ignored.</returns>
        [System.Diagnostics.CodeAnalysis.SuppressMessage("Microsoft.Maintainability", "CA1502:AvoidExcessiveComplexity"), System.Diagnostics.CodeAnalysis.SuppressMessage("Microsoft.Design", "CA1024:UsePropertiesWhereAppropriate"), System.Diagnostics.CodeAnalysis.SuppressMessage("Microsoft.Maintainability", "CA1502:AvoidExcessiveComplexity"), System.Diagnostics.CodeAnalysis.SuppressMessage("Microsoft.Design", "CA1024:UsePropertiesWhereAppropriate")]
        protected virtual HangReportContribution GetHangReport()
        {
            using (this.NoMessagePumpSynchronizationContext.Apply())
            {
                // It's possible that the hang is due to a deadlock on our own private lock,
                // so while we're reporting the hang, don't accidentally deadlock ourselves
                // while trying to do the right thing by taking the lock.
                bool lockAcquired = false;
                try
                {
                    Monitor.TryEnter(this.syncObject, 1000, ref lockAcquired);

                    var liveAwaiterMetadata = new HashSet<AwaiterMetadata>();
                    liveAwaiterMetadata.UnionWith(this.waitingReaders.Select(a => new AwaiterMetadata(a, AwaiterCollection.Waiting | AwaiterCollection.ReadLock)));
                    liveAwaiterMetadata.UnionWith(this.waitingUpgradeableReaders.Select(a => new AwaiterMetadata(a, AwaiterCollection.Waiting | AwaiterCollection.UpgradeableReadLock)));
                    liveAwaiterMetadata.UnionWith(this.waitingWriters.Select(a => new AwaiterMetadata(a, AwaiterCollection.Waiting | AwaiterCollection.WriteLock)));
                    liveAwaiterMetadata.UnionWith(this.issuedReadLocks.Select(a => new AwaiterMetadata(a, AwaiterCollection.Issued | AwaiterCollection.ReadLock)));
                    liveAwaiterMetadata.UnionWith(this.issuedUpgradeableReadLocks.Select(a => new AwaiterMetadata(a, AwaiterCollection.Issued | AwaiterCollection.UpgradeableReadLock)));
                    liveAwaiterMetadata.UnionWith(this.issuedWriteLocks.Select(a => new AwaiterMetadata(a, AwaiterCollection.Issued | AwaiterCollection.WriteLock)));

                    var liveAwaiters = liveAwaiterMetadata.Select(am => am.Awaiter);
                    var releasedAwaiterMetadata = new HashSet<AwaiterMetadata>(liveAwaiters.SelectMany(GetLockStack).Distinct().Except(liveAwaiters).Select(AwaiterMetadata.Released));
                    var allAwaiterMetadata = new HashSet<AwaiterMetadata>(liveAwaiterMetadata.Concat(releasedAwaiterMetadata));

                    // Add each lock awaiter.
                    var nodes = allAwaiterMetadata.Select(am => CreateAwaiterNode(am.Awaiter, am.Categories.ToArray(), am.GroupId)).ToList();

                    // Link the lock stacks among themselves.
                    var links = allAwaiterMetadata.Where(a => a.Awaiter.NestingLock != null).Select(a => new HangReport.Link(GetAwaiterId(a.Awaiter.NestingLock!), GetAwaiterId(a.Awaiter))).ToList();

                    return new HangReport(lockAcquired, nodes, links);
                }
                finally
                {
                    if (lockAcquired)
                    {
                        Monitor.Exit(this.syncObject);
                    }
                }
            }
        }

        /// <summary>
        /// Appends details of a given collection of awaiters to the hang report.
        /// </summary>
        [System.Diagnostics.CodeAnalysis.SuppressMessage("Microsoft.Design", "CA1031:DoNotCatchGeneralExceptionTypes")]
        private static HangReport.Node CreateAwaiterNode(Awaiter awaiter, string[] categories, string containedBy)
        {
            Requires.NotNull(awaiter, nameof(awaiter));

            var label = new StringBuilder();
            label.AppendLine(awaiter.Kind.ToString());
            if (awaiter.Options != LockFlags.None)
            {
                label.AppendLine("Options: " + awaiter.Options);
            }

            Delegate? lockWaitingContinuation;
            if (awaiter.RequestingStackTrace != null)
            {
                label.AppendLine(awaiter.RequestingStackTrace.ToString());
            }

            if ((lockWaitingContinuation = awaiter.LockRequestingContinuation) != null)
            {
                try
                {
                    foreach (var frame in lockWaitingContinuation.GetAsyncReturnStackFrames())
                    {
                        label.AppendLine(frame);
                    }
                }
                catch (Exception ex)
                {
                    // Just eat the exception so we don't crash during a hang report.
                    Report.Fail("GetAsyncReturnStackFrames threw exception: ", ex);
                }
            }

            if (label.Length >= Environment.NewLine.Length)
            {
                label.Length -= Environment.NewLine.Length;
            }

            return new HangReport.Node(GetAwaiterId(awaiter), label.ToString(), categories, containedBy);
        }

        private static string GetAwaiterId(Awaiter awaiter)
        {
            Requires.NotNull(awaiter, nameof(awaiter));
            return awaiter.GetHashCode().ToString(CultureInfo.InvariantCulture);
        }

        private static string GetAwaiterGroupId(Awaiter awaiter)
        {
            Requires.NotNull(awaiter, nameof(awaiter));
            while (awaiter.NestingLock != null)
            {
                awaiter = awaiter.NestingLock;
            }

            return "LockStack" + GetAwaiterId(awaiter);
        }

        private static IEnumerable<Awaiter> GetLockStack(Awaiter awaiter)
        {
            Requires.NotNull(awaiter, nameof(awaiter));
            for (Awaiter? current = awaiter; current != null; current = current.NestingLock)
            {
                yield return awaiter;
            }
        }

        private class AwaiterMetadata
        {
            internal AwaiterMetadata(Awaiter awaiter, AwaiterCollection membership)
            {
                Requires.NotNull(awaiter, nameof(awaiter));

                this.Awaiter = awaiter;
                this.Membership = membership;
            }

            public Awaiter Awaiter { get; private set; }

            public AwaiterCollection Membership { get; private set; }

            public IEnumerable<string> Categories
            {
                get
                {
#pragma warning disable CS8605 // Unboxing a possibly null value.
                    foreach (AwaiterCollection value in Enum.GetValues(typeof(AwaiterCollection)))
#pragma warning restore CS8605 // Unboxing a possibly null value.
                    {
                        if (this.Membership.HasFlag(value))
                        {
                            yield return value.ToString();
                        }
                    }
                }
            }

            public string GroupId
            {
                get { return GetAwaiterGroupId(this.Awaiter); }
            }

            public override int GetHashCode()
            {
                return this.Awaiter.GetHashCode();
            }

            public override bool Equals(object? obj)
            {
                var otherAwaiter = obj as AwaiterMetadata;
                return otherAwaiter != null && this.Awaiter.Equals(otherAwaiter.Awaiter);
            }

            internal static AwaiterMetadata Released(Awaiter awaiter)
            {
                Requires.NotNull(awaiter, nameof(awaiter));

                var membership = AwaiterCollection.Released;
                switch (awaiter.Kind)
                {
                    case LockKind.Read:
                        membership |= AwaiterCollection.ReadLock;
                        break;
                    case LockKind.UpgradeableRead:
                        membership |= AwaiterCollection.UpgradeableReadLock;
                        break;
                    case LockKind.Write:
                        membership |= AwaiterCollection.WriteLock;
                        break;
                    default:
                        break;
                }

                return new AwaiterMetadata(awaiter, membership);
            }
        }
    }
}
