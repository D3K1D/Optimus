using System.Text.Json.Nodes;

namespace OptimizationTool.Engine.Agent;

// The only path by which evidence leaves the PC (contracts/m3-agent.md §3.5). The gateway calls
// EvidenceProjection.Project for every evidence item in a tool result, finding page or review preview.

/// <summary>What the task's grant lets leave the PC beyond the always-allowed typed measurements.</summary>
public sealed record ShareOptions(bool Names);

/// <summary>
/// Task-stable aliases: the same app/process/device/publisher name maps to the same "App A"/"Device B" label for
/// the whole task, so the model can follow it across tool calls without ever seeing the name.
/// </summary>
public interface IAliasMap
{
    /// <summary>kind: app | process | device | publisher | display | volume | service | task.</summary>
    string Alias(string kind, string realName);
}

/// <summary>A projected evidence item. Display is regenerated from typed fields, never the collector's sentence.</summary>
public sealed record ProjectedEvidence(string Id, string Subject, string Source, string ObservedAt, string Quality, JsonObject Value, string Display);

/// <summary>One per collector. Returning null means "never share this item" (e.g. local-only or unknown shape).</summary>
public interface IEvidenceProjector
{
    /// <summary>Collector IDs this projector understands, e.g. "win.security.startup".</summary>
    IReadOnlyCollection<string> CollectorIds { get; }
    ProjectedEvidence? Project(EvidenceItem item, IAliasMap aliases, ShareOptions share);
}
