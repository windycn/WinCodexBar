using CodexBarWin.Models;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace CodexBarWin.Services;

public sealed record UsageRefreshReport(
    int Updated,
    int Unauthorized,
    int Forbidden,
    int Failed,
    bool AnyChanged);

/// <summary>
/// 协调多账号的 wham/usage 刷新：并发抓取，命中 401 时自动尝试 OAuth refresh 一次再重抓。
/// </summary>
public sealed class UsageRefreshCoordinator
{
    private readonly OpenAIUsageService _usageService;
    private readonly OpenAIOAuthRefreshService _oauthRefreshService;
    private readonly AccountRegistry _registry;

    public UsageRefreshCoordinator(
        OpenAIUsageService usageService,
        OpenAIOAuthRefreshService oauthRefreshService,
        AccountRegistry registry)
    {
        _usageService = usageService ?? throw new ArgumentNullException(nameof(usageService));
        _oauthRefreshService = oauthRefreshService ?? throw new ArgumentNullException(nameof(oauthRefreshService));
        _registry = registry ?? throw new ArgumentNullException(nameof(registry));
    }

    public Task<UsageRefreshReport> RefreshAllAsync(int maxParallel = 3, CancellationToken cancellationToken = default)
    {
        var snapshot = _registry.Accounts.ToArray();
        return RefreshAsync(snapshot, maxParallel, cancellationToken);
    }

    public async Task<UsageRefreshReport> RefreshAsync(
        IReadOnlyList<TokenAccount> accounts,
        int maxParallel = 3,
        CancellationToken cancellationToken = default)
    {
        if (accounts is null || accounts.Count == 0)
        {
            return new UsageRefreshReport(0, 0, 0, 0, false);
        }

        var concurrency = Math.Clamp(maxParallel, 1, Math.Max(1, accounts.Count));
        using var semaphore = new SemaphoreSlim(concurrency, concurrency);

        var updated = 0;
        var unauthorized = 0;
        var forbidden = 0;
        var failed = 0;

        var tasks = new List<Task>();
        foreach (var account in accounts)
        {
            await semaphore.WaitAsync(cancellationToken).ConfigureAwait(false);
            tasks.Add(Task.Run(async () =>
            {
                try
                {
                    var outcome = await RefreshSingleAsync(account, cancellationToken).ConfigureAwait(false);
                    switch (outcome)
                    {
                        case UsageRefreshOutcome.Updated:
                            Interlocked.Increment(ref updated);
                            break;
                        case UsageRefreshOutcome.Unauthorized:
                            Interlocked.Increment(ref unauthorized);
                            break;
                        case UsageRefreshOutcome.Forbidden:
                            Interlocked.Increment(ref forbidden);
                            break;
                        default:
                            Interlocked.Increment(ref failed);
                            break;
                    }
                }
                catch
                {
                    Interlocked.Increment(ref failed);
                }
                finally
                {
                    semaphore.Release();
                }
            }, cancellationToken));
        }

        await Task.WhenAll(tasks).ConfigureAwait(false);

        var anyChanged = updated > 0 || unauthorized > 0 || forbidden > 0;
        if (anyChanged)
        {
            _registry.Save();
        }

        return new UsageRefreshReport(updated, unauthorized, forbidden, failed, anyChanged);
    }

    public async Task<UsageRefreshOutcome> RefreshSingleAsync(TokenAccount account, CancellationToken cancellationToken = default)
    {
        var outcome = await _usageService.RefreshUsageAsync(account, cancellationToken).ConfigureAwait(false);
        if (outcome != UsageRefreshOutcome.Unauthorized)
        {
            return outcome;
        }

        // 401 → 尝试自动刷新一次再抓。
        var refreshResult = await _oauthRefreshService.RefreshAsync(account, cancellationToken).ConfigureAwait(false);
        if (refreshResult.Outcome != OAuthRefreshOutcome.Refreshed)
        {
            return outcome;
        }

        return await _usageService.RefreshUsageAsync(account, cancellationToken).ConfigureAwait(false);
    }
}
