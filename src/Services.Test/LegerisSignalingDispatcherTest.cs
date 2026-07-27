using System;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Marketplace.SaaS.Accelerator.DataAccess.Entities;
using Marketplace.SaaS.Accelerator.Services.Configurations;
using Marketplace.SaaS.Accelerator.Services.Contracts;
using Marketplace.SaaS.Accelerator.Services.Services;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Marketplace.SaaS.Accelerator.Services.Test;

[TestClass]
public class LegerisSignalingDispatcherTest
{
    /// <summary>Returns a canned response so the classifier can be exercised without a network.</summary>
    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly HttpResponseMessage response;

        public StubHandler(HttpStatusCode status, string body = "", Uri location = null)
        {
            this.response = new HttpResponseMessage(status)
            {
                Content = new StringContent(body ?? string.Empty),
            };
            if (location != null)
            {
                this.response.Headers.Location = location;
            }
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(this.response);
    }

    private static DispatchResult Dispatch(HttpStatusCode status, string body = "", Uri location = null)
    {
        var config = new SaaSApiClientConfiguration
        {
            LegerisSignalingEndpointUrl = "https://example.invalid/api/saasaccelerator/event",
            LegerisSignalingHmacSecret = "test-secret",
        };
        var client = new HttpClient(new StubHandler(status, body, location));
        var dispatcher = new LegerisSignalingDispatcher(client, config);

        return dispatcher.TryDispatchAsync(
            new NotificationOutbox { EventType = "Activated", EventJson = "{}", IdempotencyKey = "k" },
            CancellationToken.None).GetAwaiter().GetResult();
    }

    [TestMethod]
    public void NotFoundIsTransientNotPermanent()
    {
        // A dropped ngrok tunnel answers 404 on a still-resolvable domain, as does a receiver
        // mid-deploy. Either clears on its own, so it must not be a zero-retry dead-letter.
        Assert.AreEqual(DispatchOutcome.Transient, Dispatch(HttpStatusCode.NotFound).Outcome);
    }

    [TestMethod]
    public void RedirectIsTransientAndReportsItsTarget()
    {
        var result = Dispatch(
            HttpStatusCode.Found,
            "Bad signature",
            new Uri("https://login.microsoftonline.com/common/oauth2/v2.0/authorize?client_id=x"));

        Assert.AreEqual(DispatchOutcome.Transient, result.Outcome);
        StringAssert.Contains(result.Error, "302");
        StringAssert.Contains(result.Error, "login.microsoftonline.com");
    }

    [TestMethod]
    public void SuccessIsDelivered()
    {
        Assert.AreEqual(DispatchOutcome.Delivered, Dispatch(HttpStatusCode.OK).Outcome);
        Assert.AreEqual(DispatchOutcome.Delivered, Dispatch(HttpStatusCode.Accepted).Outcome);
    }

    [TestMethod]
    public void ConflictIsTreatedAsDeliveredForIdempotentDuplicates()
    {
        Assert.AreEqual(DispatchOutcome.Delivered, Dispatch(HttpStatusCode.Conflict).Outcome);
    }

    [TestMethod]
    public void ServerErrorsAndThrottlingStayTransient()
    {
        Assert.AreEqual(DispatchOutcome.Transient, Dispatch(HttpStatusCode.ServiceUnavailable).Outcome);
        Assert.AreEqual(DispatchOutcome.Transient, Dispatch(HttpStatusCode.TooManyRequests).Outcome);
        Assert.AreEqual(DispatchOutcome.Transient, Dispatch(HttpStatusCode.RequestTimeout).Outcome);
    }

    [TestMethod]
    public void OtherClientErrorsStillDeadLetter()
    {
        Assert.AreEqual(DispatchOutcome.Permanent, Dispatch(HttpStatusCode.BadRequest).Outcome);
        Assert.AreEqual(DispatchOutcome.Permanent, Dispatch(HttpStatusCode.Unauthorized).Outcome);
        Assert.AreEqual(DispatchOutcome.Permanent, Dispatch(HttpStatusCode.Forbidden).Outcome);
    }

    [TestMethod]
    public void UnconfiguredEndpointIsPermanent()
    {
        var dispatcher = new LegerisSignalingDispatcher(
            new HttpClient(new StubHandler(HttpStatusCode.OK)),
            new SaaSApiClientConfiguration { LegerisSignalingEndpointUrl = null });

        var result = dispatcher.TryDispatchAsync(
            new NotificationOutbox { EventType = "Activated", EventJson = "{}" },
            CancellationToken.None).GetAwaiter().GetResult();

        Assert.AreEqual(DispatchOutcome.Permanent, result.Outcome);
    }
}
