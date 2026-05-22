// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License. See LICENSE file in the project root for license information.

namespace Marketplace.SaaS.Accelerator.WebhookBuffer.Options;

/// <summary>
/// Buffer-wide settings. Bound from the "BufferOptions" config section.
/// </summary>
public class BufferOptions
{
    public const string SectionName = "BufferOptions";

    public string QueueName { get; set; } = "marketplace-webhook";

    public int MaxDeliveryCount { get; set; } = 10;
}
