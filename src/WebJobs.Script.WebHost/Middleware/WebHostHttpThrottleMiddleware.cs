// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the MIT License. See License.txt in the project root for license information.

using System;
using System.Collections.ObjectModel;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.Azure.WebJobs.Extensions.Http;
using Microsoft.Azure.WebJobs.Extensions.Http.Middleware;
using Microsoft.Azure.WebJobs.Script.Diagnostics;
using Microsoft.Azure.WebJobs.Script.Scale;
using Microsoft.Azure.WebJobs.Script.WebHost.Features;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Microsoft.Azure.WebJobs.Script.WebHost.Middleware
{
    public class WebHostHttpThrottleMiddleware : HttpThrottleMiddleware
    {
        private readonly IOptions<HttpOptions> _httpOptions;
        private readonly HostPerformanceManager _performanceManager;
        private readonly IMetricsLogger _metricsLogger;
        private readonly int _performanceCheckPeriodSeconds;
        private DateTime _lastPerformanceCheck;
        private bool _rejectRequests;

        public WebHostHttpThrottleMiddleware(RequestDelegate next, IOptions<HttpOptions> httpOptions, HostPerformanceManager performanceManager, IMetricsLogger metricsLogger, ILoggerFactory loggerFactory, int performanceCheckPeriodSeconds = 15)
            : base(next, httpOptions, loggerFactory)
        {
            _httpOptions = httpOptions;
            _performanceManager = performanceManager;
            _metricsLogger = metricsLogger;
            _performanceCheckPeriodSeconds = performanceCheckPeriodSeconds;
        }

        public override async Task Invoke(HttpContext httpContext)
        {
            if (_httpOptions.Value.DynamicThrottlesEnabled &&
               ((DateTime.UtcNow - _lastPerformanceCheck) > TimeSpan.FromSeconds(_performanceCheckPeriodSeconds)))
            {
                // only check host status periodically
                Collection<string> exceededCounters = new Collection<string>();
                _rejectRequests = _performanceManager.IsUnderHighLoad(exceededCounters);
                _lastPerformanceCheck = DateTime.UtcNow;
                if (_rejectRequests)
                {
                    Logger.LogWarning($"Thresholds for the following counters have been exceeded: [{string.Join(", ", exceededCounters)}]");
                }
            }

            if (_rejectRequests)
            {
                // we're currently in reject mode, so reject the request and
                // call the next delegate without calling base
                RejectRequest(httpContext);
                return;
            }

            await base.Invoke(httpContext);
        }

        protected override void RejectRequest(HttpContext httpContext)
        {
            IFunctionExecutionFeature functionExecution = httpContext.Features.Get<IFunctionExecutionFeature>();
            var function = functionExecution.Descriptor;
            _metricsLogger.LogEvent(MetricEventNames.FunctionInvokeThrottled, function.Name);

            base.RejectRequest(httpContext);
            httpContext.Response.Headers.Add(ScriptConstants.AntaresScaleOutHeaderName, "1");
        }
    }
}
