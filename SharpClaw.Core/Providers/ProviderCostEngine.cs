using SharpClaw.Contracts.DTOs.Providers;
using SharpClaw.Contracts.Providers;

namespace SharpClaw.Core.Providers;

/// <summary>
/// Store-neutral provider cost reporting rules used by SharpClaw runtimes.
/// </summary>
public sealed class ProviderCostEngine
{
    /// <summary>
    /// Currency reported when no provider cost feed data is available.
    /// </summary>
    public const string DefaultFallbackCurrency = "usd";

    /// <summary>Resolves the requested cost period.</summary>
    public (DateTimeOffset Start, DateTimeOffset End) ResolvePeriod(
        int days,
        DateTimeOffset? startDate,
        DateTimeOffset? endDate,
        DateTimeOffset? now = null)
    {
        var end = endDate ?? now ?? DateTimeOffset.UtcNow;
        var start = startDate ?? end.AddDays(-days);
        return (start, end);
    }

    /// <summary>Projects a successful provider cost feed result.</summary>
    public ProviderCostResponse CreateFeedResponse(
        ProviderCostProvider provider,
        bool isLocal,
        DateTimeOffset periodStart,
        DateTimeOffset periodEnd,
        ProviderCostResult result)
    {
        ArgumentNullException.ThrowIfNull(result);

        return new ProviderCostResponse(
            provider.Id,
            provider.Name,
            provider.ProviderKey,
            IsLocal: isLocal,
            CostApiSupported: true,
            TotalCost: result.TotalAmount,
            Currency: result.Currency,
            PeriodStart: periodStart,
            PeriodEnd: periodEnd,
            DailyBreakdown: result.DailyBuckets
                .Select(bucket => new CostDailyBucket(
                    bucket.Start,
                    bucket.End,
                    bucket.Amount,
                    result.Currency))
                .ToList(),
            Note: isLocal ? "Local provider - no cloud API costs incurred." : null);
    }

    /// <summary>Creates the response for a supported feed whose key lacks billing permissions.</summary>
    public ProviderCostResponse CreatePermissionDeniedResponse(
        ProviderCostProvider provider,
        bool isLocal,
        DateTimeOffset periodStart,
        DateTimeOffset periodEnd,
        string permissionDeniedNote)
    {
        return new ProviderCostResponse(
            provider.Id,
            provider.Name,
            provider.ProviderKey,
            IsLocal: isLocal,
            CostApiSupported: true,
            TotalCost: 0,
            Currency: DefaultFallbackCurrency,
            PeriodStart: periodStart,
            PeriodEnd: periodEnd,
            DailyBreakdown: null,
            Note: permissionDeniedNote);
    }

    /// <summary>Creates the response for a provider that has no cost API surface.</summary>
    public ProviderCostResponse CreateUnsupportedResponse(
        ProviderCostProvider provider,
        bool isLocal,
        DateTimeOffset periodStart,
        DateTimeOffset periodEnd)
    {
        return new ProviderCostResponse(
            provider.Id,
            provider.Name,
            provider.ProviderKey,
            IsLocal: isLocal,
            CostApiSupported: false,
            TotalCost: 0,
            Currency: DefaultFallbackCurrency,
            PeriodStart: periodStart,
            PeriodEnd: periodEnd,
            DailyBreakdown: null,
            Note: $"Provider key '{provider.ProviderKey}' does not expose a cost API. "
                + "Check the provider's dashboard for billing information.");
    }

    /// <summary>Aggregates per-provider cost responses into the total response.</summary>
    public ProviderCostTotalResponse CreateTotalResponse(
        DateTimeOffset periodStart,
        DateTimeOffset periodEnd,
        IReadOnlyList<ProviderCostResponse> providers)
    {
        ArgumentNullException.ThrowIfNull(providers);

        var totalCost = providers.Sum(provider => provider.TotalCost);
        var currency = providers
            .FirstOrDefault(provider => provider.CostApiSupported && provider.TotalCost > 0)
            ?.Currency
            ?? DefaultFallbackCurrency;

        return new ProviderCostTotalResponse(
            totalCost,
            currency,
            periodStart,
            periodEnd,
            providers);
    }
}

/// <summary>
/// Store-neutral provider identity needed to shape provider cost responses.
/// </summary>
public readonly record struct ProviderCostProvider(
    Guid Id,
    string Name,
    string ProviderKey);
