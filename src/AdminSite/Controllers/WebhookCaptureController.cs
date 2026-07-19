using System;
using System.Net.Http;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using System.Web;
using Marketplace.SaaS.Accelerator.DataAccess.Contracts;
using Marketplace.SaaS.Accelerator.Services.Configurations;
using Marketplace.SaaS.Accelerator.Services.Contracts;
using Marketplace.SaaS.Accelerator.Services.Services;
using Marketplace.SaaS.Accelerator.Services.Utilities;
using Microsoft.AspNetCore.Mvc;

namespace Marketplace.SaaS.Accelerator.AdminSite.Controllers;

/// <summary>
/// Lists archived inbound webhooks (<see cref="DataAccess.Entities.WebhookCapture"/>) and replays
/// one back to the CustomerSite /api/AzureWebhook - buffer-HMAC-signed, with a fresh OperationId -
/// so the full webhook lifecycle can be re-run repeatedly without creating real subscriptions.
/// </summary>
public class WebhookCaptureController : BaseController
{
    private static readonly HttpClient HttpClient = new();

    private readonly IWebhookCaptureRepository captureRepo;
    private readonly SaaSApiClientConfiguration config;
    private readonly SaaSClientLogger<WebhookCaptureController> logger;

    public WebhookCaptureController(
        IWebhookCaptureRepository captureRepo,
        SaaSApiClientConfiguration config,
        IApplicationConfigRepository applicationConfigRepository,
        IAppVersionService appVersionService,
        SaaSClientLogger<WebhookCaptureController> logger) : base(applicationConfigRepository, appVersionService)
    {
        this.captureRepo = captureRepo;
        this.config = config;
        this.logger = logger;
    }

    public IActionResult Index()
    {
        if (this.User?.Identity?.IsAuthenticated != true)
        {
            return this.RedirectToAction("Index", "Home");
        }

        var rows = this.captureRepo.ListRecent(200);
        return this.View(rows);
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Replay(int id)
    {
        if (this.User?.Identity?.IsAuthenticated != true)
        {
            return this.RedirectToAction("Index", "Home");
        }

        var capture = this.captureRepo.Get(id);
        if (capture == null)
        {
            this.TempData["ReplayError"] = $"Capture {id} not found.";
            return this.RedirectToAction(nameof(this.Index));
        }

        var baseUrl = this.config?.CustomerSiteBaseUrl?.TrimEnd('/');
        if (string.IsNullOrWhiteSpace(baseUrl))
        {
            this.TempData["ReplayError"] = "SaaSApiConfiguration:CustomerSiteBaseUrl is not configured, cannot replay.";
            return this.RedirectToAction(nameof(this.Index));
        }

        var secret = this.config?.WebhookBufferHmacSecret;
        if (string.IsNullOrWhiteSpace(secret))
        {
            this.TempData["ReplayError"] = "SaaSApiConfiguration:WebhookBufferHmacSecret is not configured, cannot sign the replay.";
            return this.RedirectToAction(nameof(this.Index));
        }

        // Fresh identifiers so the portal's idempotency guard does not short-circuit the replay.
        JsonNode node;
        try
        {
            node = JsonNode.Parse(capture.PayloadJson);
        }
        catch (Exception ex)
        {
            this.TempData["ReplayError"] = $"Capture {id} payload is not valid JSON: {ex.Message}";
            return this.RedirectToAction(nameof(this.Index));
        }

        node["Id"] = Guid.NewGuid().ToString();
        node["activityId"] = Guid.NewGuid().ToString();
        node["timeStamp"] = DateTime.UtcNow.ToString("o");
        var body = node.ToJsonString();

        // Sign the exact bytes we POST (BufferHmacFilter verifies the raw body).
        var signature = HmacSigner.ComputeSignature(body, secret);

        using var request = new HttpRequestMessage(HttpMethod.Post, $"{baseUrl}/api/AzureWebhook")
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };
        request.Headers.TryAddWithoutValidation("X-Webhook-Source", "Buffer");
        request.Headers.TryAddWithoutValidation("X-Signature", "sha256=" + signature);

        try
        {
            using var response = await HttpClient.SendAsync(request).ConfigureAwait(false);
            var responseBody = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
            var snippet = responseBody.Length > 300 ? responseBody.Substring(0, 300) : responseBody;

            var summary = $"Replayed capture {id} ({capture.Action}) to {baseUrl}/api/AzureWebhook -> {(int)response.StatusCode} {response.ReasonPhrase}. {snippet}";
            if (response.IsSuccessStatusCode)
            {
                this.TempData["ReplayResult"] = summary;
            }
            else
            {
                this.TempData["ReplayError"] = summary;
            }

            this.logger.Info(HttpUtility.HtmlEncode($"Webhook capture {id} replayed by {this.CurrentUserEmailAddress}: {(int)response.StatusCode} {response.ReasonPhrase}"));
        }
        catch (Exception ex)
        {
            this.TempData["ReplayError"] = $"Replay of capture {id} failed: {ex.Message}";
            this.logger.Error(HttpUtility.HtmlEncode($"Webhook capture {id} replay error: {ex.Message}"));
        }

        return this.RedirectToAction(nameof(this.Index));
    }
}
