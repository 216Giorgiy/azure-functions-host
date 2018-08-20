// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the MIT License. See License.txt in the project root for license information.

using System.Collections.ObjectModel;
using System.Linq;
using System.Net;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.Azure.WebJobs.Extensions.Http;
using Microsoft.Azure.WebJobs.Script.Config;
using Microsoft.Azure.WebJobs.Script.Description;
using Microsoft.Azure.WebJobs.Script.Diagnostics;
using Microsoft.Azure.WebJobs.Script.Scale;
using Microsoft.Azure.WebJobs.Script.WebHost.Features;
using Microsoft.Azure.WebJobs.Script.WebHost.Middleware;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Primitives;
using Microsoft.WebJobs.Script.Tests;
using Moq;
using Xunit;

namespace Microsoft.Azure.WebJobs.Script.Tests
{
    public class WebHostHttpThrottleMiddlewareTests
    {
        private readonly WebHostHttpThrottleMiddleware _middleware;
        private readonly Mock<IMetricsLogger> _metricsLogger;
        private readonly Mock<HostPerformanceManager> _performanceManager;
        private readonly FunctionDescriptor _functionDescriptor;
        private readonly HttpOptions _httpOptions;
        private readonly TestLoggerProvider _loggerProvider;
        private readonly LoggerFactory _loggerFactory;
        private readonly Mock<IScriptJobHost> _scriptHost;

        public WebHostHttpThrottleMiddlewareTests()
        {
            _functionDescriptor = new FunctionDescriptor("Test", null, null, new Collection<ParameterDescriptor>(), null, null, null);
            _scriptHost = new Mock<IScriptJobHost>(MockBehavior.Strict);
            _metricsLogger = new Mock<IMetricsLogger>(MockBehavior.Strict);
            var environment = SystemEnvironment.Instance;
            var healthMonitorOptions = new HostHealthMonitorOptions();
            _performanceManager = new Mock<HostPerformanceManager>(MockBehavior.Strict, new object[] { environment, new OptionsWrapper<HostHealthMonitorOptions>(healthMonitorOptions) });
            _httpOptions = new HttpOptions();
            _loggerFactory = new LoggerFactory();
            _loggerProvider = new TestLoggerProvider();
            _loggerFactory.AddProvider(_loggerProvider);
            RequestDelegate next = (ctxt) =>
            {
                ctxt.Response.StatusCode = (int)HttpStatusCode.Accepted;
                return Task.CompletedTask;
            };
            _middleware = new WebHostHttpThrottleMiddleware(next, new OptionsWrapper<HttpOptions>(_httpOptions), _performanceManager.Object, _metricsLogger.Object, _loggerFactory, 1);
        }

        [Fact]
        public async Task ProcessRequestAsync_PerformanceThrottle_ReturnsExpectedResult()
        {
            _httpOptions.DynamicThrottlesEnabled = true;

            bool highLoad = false;
            int highLoadQueryCount = 0;
            _performanceManager.Setup(p => p.IsUnderHighLoad(It.IsAny<Collection<string>>(), It.IsAny<ILogger>()))
                .Callback<Collection<string>, ILogger>((exceededCounters, tw) =>
                {
                    if (highLoad)
                    {
                        exceededCounters.Add("Threads");
                        exceededCounters.Add("Processes");
                    }
                }).Returns(() =>
                {
                    highLoadQueryCount++;
                    return highLoad;
                });
            int throttleMetricCount = 0;
            _metricsLogger.Setup(p => p.LogEvent(MetricEventNames.FunctionInvokeThrottled, "Test")).Callback(() =>
            {
                throttleMetricCount++;
            });

            // issue some requests while not under high load
            for (int i = 0; i < 3; i++)
            {
                var httpContext = CreateHttpContext();
                await _middleware.Invoke(httpContext);
                Assert.Equal(HttpStatusCode.Accepted, (HttpStatusCode)httpContext.Response.StatusCode);
                await Task.Delay(100);
            }
            Assert.Equal(1, highLoadQueryCount);
            Assert.Equal(0, throttleMetricCount);

            // signal high load and verify requests are rejected
            await Task.Delay(1000);
            highLoad = true;
            for (int i = 0; i < 3; i++)
            {
                var httpContext = CreateHttpContext();
                await _middleware.Invoke(httpContext);

                httpContext.Response.Headers.TryGetValue(ScriptConstants.AntaresScaleOutHeaderName, out StringValues values);
                string scaleOutHeader = values.Single();
                Assert.Equal("1", scaleOutHeader);
                Assert.Equal(HttpStatusCode.TooManyRequests, (HttpStatusCode)httpContext.Response.StatusCode);
                await Task.Delay(100);
            }
            Assert.Equal(2, highLoadQueryCount);
            Assert.Equal(3, throttleMetricCount);
            var log = _loggerProvider.GetAllLogMessages().Last();
            Assert.Equal("Thresholds for the following counters have been exceeded: [Threads, Processes]", log.FormattedMessage);

            await Task.Delay(1000);
            highLoad = false;
            for (int i = 0; i < 3; i++)
            {
                var httpContext = CreateHttpContext();
                await _middleware.Invoke(httpContext);
                Assert.Equal(HttpStatusCode.Accepted, (HttpStatusCode)httpContext.Response.StatusCode);
                await Task.Delay(100);
            }
            Assert.Equal(3, highLoadQueryCount);
            Assert.Equal(3, throttleMetricCount);
        }

        private HttpContext CreateHttpContext()
        {
            var httpContext = new DefaultHttpContext();

            var executionFeature = new FunctionExecutionFeature(_scriptHost.Object, _functionDescriptor, SystemEnvironment.Instance, _loggerFactory);
            httpContext.Features.Set<IFunctionExecutionFeature>(executionFeature);

            return httpContext;
        }
    }
}