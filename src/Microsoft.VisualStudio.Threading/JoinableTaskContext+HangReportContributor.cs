/********************************************************
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
    using System.Threading.Tasks;
    using System.Xml.Linq;

    partial class JoinableTaskContext : IHangReportContributor
    {
#pragma warning disable CS1591
        public class HangReport : HangReportContribution
        {
            public class Node
            {
                public string ID;
                public string Label;

                public Node(string id, string label)
                {
                    ID = id;
                    Label = label;
                }

                public override string ToString()
                {
                    return $"node {ID} label {Label}";
                }
            }

            public class CollectionNode : Node
            {
                public CollectionNode(string id, string label)
                    : base(id, label)
                {
                }

                public override string ToString()
                {
                    return $"group {ID} label {Label}";
                }
            }

            public class TaskNode : Node
            {
                public bool NonEmptyQueue;
                public bool MainThreadBlocking;

                public TaskNode(string id, string label)
                    : base(id, label)
                {
                }

                public override string ToString()
                {
                    var result = $"task {ID} label {Label}";
                    if (NonEmptyQueue)
                        result += " " + nameof(NonEmptyQueue);
                    if (MainThreadBlocking)
                        result += " " + nameof(MainThreadBlocking);
                    return result;
                }
            }

            public class Link
            {
                public string Source;
                public string Target;

                public Link(Node source, Node target)
                {
                    Source = source.ID;
                    Target = target.ID;
                }

                public override string ToString()
                {
                    return $"{Source} -> {Target}";
                }
            }

            public class ContainingLink : Link
            {
                public ContainingLink(Node group, Node task)
                    : base(group, task)
                {
                }

                public override string ToString()
                {
                    return $"{Source} contains {Target}";
                }
            }

            public List<Node> Nodes;
            public List<Link> Links;

            public HangReport(List<Node> nodes, List<Link> links)
            {
                Nodes = nodes;
                Links = links;
            }

            public override string ToString()
            {
                var sb = new StringBuilder();

                foreach (var node in Nodes)
                    sb.AppendLine(node.ToString());

                foreach (var link in Links)
                    sb.AppendLine(link.ToString());

                return sb.ToString();
            }
        }
#pragma warning restore CS1591

        /// <summary>
        /// Contributes data for a hang report.
        /// </summary>
        /// <returns>The hang report contribution.</returns>
        HangReportContribution IHangReportContributor.GetHangReport()
        {
            return this.GetHangReport();
        }

        /// <summary>
        /// Contributes data for a hang report.
        /// </summary>
        /// <returns>The hang report contribution. Null values should be ignored.</returns>
        [System.Diagnostics.CodeAnalysis.SuppressMessage("Microsoft.Design", "CA1024:UsePropertiesWhereAppropriate"), System.Diagnostics.CodeAnalysis.SuppressMessage("Microsoft.Design", "CA1024:UsePropertiesWhereAppropriate")]
        protected virtual HangReportContribution GetHangReport()
        {
            using (this.NoMessagePumpSynchronizationContext.Apply())
            {
                lock (this.SyncContextLock)
                {
                    var nodes = new List<HangReport.Node>();
                    var links = new List<HangReport.Link>();

                    var pendingTasksElements = this.CreateNodesForPendingTasks();
                    var taskLabels = CreateNodeLabels(pendingTasksElements);
                    var pendingTaskCollections = CreateNodesForJoinableTaskCollections(pendingTasksElements.Keys);
                    nodes.AddRange(pendingTasksElements.Values);
                    nodes.AddRange(pendingTaskCollections.Values);
                    nodes.AddRange(taskLabels.Select(t => t.Item1));
                    links.AddRange(CreatesLinksBetweenNodes(pendingTasksElements));
                    links.AddRange(CreateCollectionContainingTaskLinks(pendingTasksElements, pendingTaskCollections));
                    links.AddRange(taskLabels.Select(t => t.Item2));

                    return new HangReport(nodes, links);
                }
            }
        }

        private static ICollection<HangReport.Link> CreatesLinksBetweenNodes(Dictionary<JoinableTask, HangReport.Node> pendingTasksElements)
        {
            Requires.NotNull(pendingTasksElements, nameof(pendingTasksElements));

            var links = new List<HangReport.Link>();
            foreach (var joinableTaskAndElement in pendingTasksElements)
            {
                foreach (var joinedTask in JoinableTaskDependencyGraph.GetAllDirectlyDependentJoinableTasks(joinableTaskAndElement.Key))
                {
                    if (pendingTasksElements.TryGetValue(joinedTask, out var joinedTaskElement))
                    {
                        links.Add(new HangReport.Link(joinableTaskAndElement.Value, joinedTaskElement!));
                    }
                }
            }

            return links;
        }

        private static ICollection<HangReport.Link> CreateCollectionContainingTaskLinks(Dictionary<JoinableTask, HangReport.Node> tasks, Dictionary<JoinableTaskCollection, HangReport.Node> collections)
        {
            Requires.NotNull(tasks, nameof(tasks));
            Requires.NotNull(collections, nameof(collections));

            var result = new List<HangReport.Link>();
            foreach (var task in tasks)
            {
                foreach (var collection in task.Key.ContainingCollections)
                {
                    var collectionElement = collections[collection];
                    result.Add(new HangReport.ContainingLink(collectionElement, task.Value));
                }
            }

            return result;
        }

        private static Dictionary<JoinableTaskCollection, HangReport.Node> CreateNodesForJoinableTaskCollections(IEnumerable<JoinableTask> tasks)
        {
            Requires.NotNull(tasks, nameof(tasks));

            var collectionsSet = new HashSet<JoinableTaskCollection>(tasks.SelectMany(t => t.ContainingCollections));
            var result = new Dictionary<JoinableTaskCollection, HangReport.Node>(collectionsSet.Count);
            int collectionId = 0;
            foreach (var collection in collectionsSet)
            {
                collectionId++;
                var label = string.IsNullOrEmpty(collection.DisplayName) ? "Collection #" + collectionId : collection.DisplayName ?? "";
                var element = new HangReport.CollectionNode("Collection#" + collectionId, label);
                result.Add(collection, element);
            }

            return result;
        }

        private static List<Tuple<HangReport.Node, HangReport.Link>> CreateNodeLabels(Dictionary<JoinableTask, HangReport.Node> tasksAndElements)
        {
            Requires.NotNull(tasksAndElements, nameof(tasksAndElements));

            var result = new List<Tuple<HangReport.Node, HangReport.Link>>();
            foreach (var tasksAndElement in tasksAndElements)
            {
                var pendingTask = tasksAndElement.Key;
                var node = tasksAndElement.Value;
                int queueIndex = 0;
                foreach (var pendingTasksElement in pendingTask.MainThreadQueueContents)
                {
                    queueIndex++;
                    var callstackNode = new HangReport.Node(node.ID + "MTQueue#" + queueIndex, GetAsyncReturnStack(pendingTasksElement));
                    var callstackLink = new HangReport.Link(callstackNode, node);
                    result.Add(Tuple.Create(callstackNode, callstackLink));
                }

                foreach (var pendingTasksElement in pendingTask.ThreadPoolQueueContents)
                {
                    queueIndex++;
                    var callstackNode = new HangReport.Node(node.ID + "TPQueue#" + queueIndex, GetAsyncReturnStack(pendingTasksElement));
                    var callstackLink = new HangReport.Link(callstackNode, node);
                    result.Add(Tuple.Create(callstackNode, callstackLink));
                }
            }

            return result;
        }

        private Dictionary<JoinableTask, HangReport.Node> CreateNodesForPendingTasks()
        {
            var pendingTasksElements = new Dictionary<JoinableTask, HangReport.Node>();
            lock (this.pendingTasks)
            {
                int taskId = 0;
                foreach (var pendingTask in this.pendingTasks)
                {
                    taskId++;

                    string methodName = string.Empty;
                    var entryMethodInfo = pendingTask.EntryMethodInfo;
                    if (entryMethodInfo != null)
                    {
                        methodName = string.Format(
                            CultureInfo.InvariantCulture,
                            " ({0}.{1})",
                            entryMethodInfo.DeclaringType?.FullName,
                            entryMethodInfo.Name);
                    }

                    var node = new HangReport.TaskNode("Task#" + taskId, "Task #" + taskId + methodName);
                    node.NonEmptyQueue = pendingTask.HasNonEmptyQueue;
                    node.MainThreadBlocking = pendingTask.State.HasFlag(JoinableTask.JoinableTaskFlags.SynchronouslyBlockingMainThread);

                    pendingTasksElements.Add(pendingTask, node);
                }
            }

            return pendingTasksElements;
        }

        [System.Diagnostics.CodeAnalysis.SuppressMessage("Microsoft.Design", "CA1031:DoNotCatchGeneralExceptionTypes")]
        private static string GetAsyncReturnStack(JoinableTaskFactory.SingleExecuteProtector singleExecuteProtector)
        {
            Requires.NotNull(singleExecuteProtector, nameof(singleExecuteProtector));

            var stringBuilder = new StringBuilder();
            try
            {
                foreach (var frame in singleExecuteProtector.WalkAsyncReturnStackFrames())
                {
                    stringBuilder.AppendLine(frame);
                }
            }
            catch (Exception ex)
            {
                // Just eat the exception so we don't crash during a hang report.
                Report.Fail("GetAsyncReturnStackFrames threw exception: ", ex);
            }

            return stringBuilder.ToString().TrimEnd();
        }
    }
}
