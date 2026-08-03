using System;

namespace Banking.Domain.Workflows;

/// <summary>
/// Sistemde otomatik keşfedilen (Auto-Discovered) iş akışlarının meta verilerini temsil eden model.
/// </summary>
public record WorkflowMetadata(string WorkflowName, string Description, Type SlotType);
