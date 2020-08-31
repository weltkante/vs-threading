/********************************************************
*                                                        *
*   © Copyright (C) Microsoft. All rights reserved.      *
*                                                        *
*********************************************************/

namespace Microsoft.VisualStudio.Threading
{
    using System;
    using System.Collections.Generic;
    using System.IO;
    using System.Linq;
    using System.Text;
    using System.Threading.Tasks;

    /// <summary>
    /// A contribution to an aggregate hang report.
    /// </summary>
    public class HangReportContribution
    {
        /// <summary>
        /// Initializes a new instance of the <see cref="HangReportContribution"/> class.
        /// </summary>
        public HangReportContribution()
        {
        }

        /// <summary>
        /// Initializes a new instance of the <see cref="HangReportContribution"/> class.
        /// </summary>
        /// <param name="nestedReports">Nested reports.</param>
        public HangReportContribution(params HangReportContribution[]? nestedReports)
        {
            this.NestedReports = nestedReports;
        }

        /// <summary>
        /// Gets the nested hang reports, if any.
        /// </summary>
        /// <value>A read only collection, or <c>null</c>.</value>
        public IReadOnlyCollection<HangReportContribution>? NestedReports { get; private set; }
    }
}
