using System.Collections.ObjectModel;
using System.Text.Json;
using AIUsageMonitor.Models;
using Microsoft.Extensions.DependencyInjection;

namespace AIUsageMonitor.Services;

public class CodexAccountManagerService
{
    private const string FileName = "codex_accounts.json";
    private readonly string _filePath;
    private readonly TokenStorageService _tokenStorage;
    private readonly SemaphoreSlim _saveSemaphore = new SemaphoreSlim(1, 1);
    public ObservableCollection<CodexAccount> Accounts { get; } = new();

    public CodexAccountManagerService()
    {
        _filePath = Path.Combine(FileSystem.AppDataDirectory, FileName);
        _tokenStorage = MauiProgram.Services.GetRequiredService<TokenStorageService>();
    }

    public async Task LoadAccountsAsync()
    {
        if (!File.Exists(_filePath)) return;
        try
        {
            var json = await File.ReadAllTextAsync(_filePath);
            var list = JsonSerializer.Deserialize<List<CodexAccount>>(json);
            if (list != null)
            {
                using var doc = JsonDocument.Parse(json);
                Accounts.Clear();
                foreach (var acc in list)
                {
                    string? oldAccess = null;
                    string? oldRefresh = null;
                    
                    if (doc.RootElement.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var element in doc.RootElement.EnumerateArray())
                        {
                            if (element.TryGetProperty("id", out var idProp) && idProp.GetString() == acc.id)
                            {
                                oldAccess = element.TryGetProperty("access_token", out var aProp) ? aProp.GetString() : null;
                                oldRefresh = element.TryGetProperty("refresh_token", out var rProp) ? rProp.GetString() : null;
                                break;
                            }
                        }
                    }

                    // Migration logic
                    var (secureAccess, secureRefresh, _) = await _tokenStorage.LoadTokensAsync(acc.id);
                    
                    Log.Info($"Migration: Acc: {acc.email ?? acc.name} | JSON(acc:{!string.IsNullOrEmpty(oldAccess)}, ref:{!string.IsNullOrEmpty(oldRefresh)}) | Secure(acc:{!string.IsNullOrEmpty(secureAccess)}, ref:{!string.IsNullOrEmpty(secureRefresh)})");

                    if (string.IsNullOrEmpty(secureAccess) && (!string.IsNullOrEmpty(oldAccess) || !string.IsNullOrEmpty(oldRefresh)))
                    {
                        Log.Info($"Migration: -> MIGRATING old tokens to SecureStorage for {acc.email ?? acc.name}");
                        await _tokenStorage.SaveTokensAsync(acc.id, oldAccess ?? "", oldRefresh ?? "");
                        acc.access_token = oldAccess ?? "";
                        acc.refresh_token = oldRefresh ?? "";
                    }
                    else
                    {
                        Log.Info($"Migration: -> KEEPING SecureStorage tokens for {acc.email ?? acc.name}");
                        acc.access_token = secureAccess ?? "";
                        acc.refresh_token = secureRefresh ?? "";
                    }
                    Accounts.Add(acc);
                }
            }
        }
        catch (Exception ex)
        {
            Log.Error("Failed to load codex accounts", ex);
        }
    }

    public async Task SaveAccountsAsync()
    {
        await _saveSemaphore.WaitAsync();
        try
        {
            // Ensure all tokens are in SecureStorage before saving JSON
            foreach (var acc in Accounts)
            {
                var (_, _, expiresAt) = await _tokenStorage.LoadTokensAsync(acc.id);
                int? expiresInSeconds = null;
                if (expiresAt.HasValue)
                {
                    var remaining = expiresAt.Value - DateTime.UtcNow;
                    expiresInSeconds = remaining > TimeSpan.Zero
                        ? (int)Math.Ceiling(remaining.TotalSeconds)
                        : 0;
                }

                await _tokenStorage.SaveTokensAsync(acc.id, acc.access_token, acc.refresh_token, expiresInSeconds);
            }

            var json = JsonSerializer.Serialize(Accounts.ToList());
            await File.WriteAllTextAsync(_filePath, json);
        }
        catch (Exception ex)
        {
            Log.Error("Failed to save codex accounts", ex);
        }
        finally
        {
            _saveSemaphore.Release();
        }
    }

    public void AddOrUpdateAccount(CodexAccount account)
    {
        var existing = Accounts.FirstOrDefault(a => a.id == account.id || (!string.IsNullOrEmpty(a.access_token) && a.access_token == account.access_token));
        if (existing != null)
        {
            existing.access_token = account.access_token;
            existing.refresh_token = account.refresh_token;
            existing.name = account.name;
            existing.email = account.email;
            existing.plan_type = account.plan_type;
            existing.credits = account.credits;
            existing.has_credits = account.has_credits;
            existing.unlimited_credits = account.unlimited_credits;
            existing.primaryUsedPercent = account.primaryUsedPercent;
            existing.primaryWindowLabel = account.primaryWindowLabel;
            existing.primaryResetDescription = account.primaryResetDescription;
            existing.secondaryUsedPercent = account.secondaryUsedPercent;
            existing.secondaryWindowLabel = 
                string.IsNullOrWhiteSpace(account.secondaryWindowLabel) ? 
                string.Empty : $"{account.secondaryWindowLabel} ";
            existing.secondaryResetDate = account.secondaryResetDate;
            if (!string.IsNullOrEmpty(account.refresh_token))
                existing.refresh_token = account.refresh_token;
            if (!string.IsNullOrEmpty(account.login_method))
                existing.login_method = account.login_method;
            existing.last_updated = DateTime.Now;
        }
        else
        {
            if (string.IsNullOrEmpty(account.id)) account.id = Guid.NewGuid().ToString();
            account.last_updated = DateTime.Now;
            Accounts.Add(account);
        }
        _ = SaveAccountsAsync();
        SortAccounts();
    }

    public void SortAccounts()
    {
        if (Accounts.Count <= 1) return;

        // 1. Get a sorted list
        var sorted = Accounts.OrderBy(a => GetTierPriority(a.plan_type))
                            .ThenByDescending(a => a.primaryUsedPercent)
                            .ThenBy(a => GetResetTimestamp(a))
                            .ToList();

        // 2. Synchronize ObservableCollection without clearing (to maintain scroll position if possible)
        for (int i = 0; i < sorted.Count; i++)
        {
            var oldIndex = Accounts.IndexOf(sorted[i]);
            if (oldIndex != i)
            {
                Accounts.Move(oldIndex, i);
            }
        }

        _ = SaveAccountsAsync();
    }

    private int GetTierPriority(string? planType)
    {
        if (string.IsNullOrEmpty(planType)) return 99;
        return planType.ToLower() switch
        {
            "plus" => 1,
            "pro" => 1,
            "team" => 1,
            "free" => 2,
            _ => 3
        };
    }

    private long GetResetTimestamp(CodexAccount acc)
    {
        // Use the stored timestamp. If it's 0 (no limit or not loaded), use a very far future date.
        if (acc.PrimaryResetAt <= 0) return DateTimeOffset.MaxValue.ToUnixTimeSeconds();
        return acc.PrimaryResetAt;
    }

    public void RemoveAccount(string accountId)
    {
        var acc = Accounts.FirstOrDefault(a => a.id == accountId);
        if (acc != null)
        {
            Accounts.Remove(acc);
            _tokenStorage.RemoveTokens(accountId);
            _ = SaveAccountsAsync();
        }
    }

    public async Task ExportAccountsAsync(string targetPath)
    {
        try
        {
            var json = JsonSerializer.Serialize(Accounts.ToList());
            await File.WriteAllTextAsync(targetPath, json);
        }
        catch (Exception ex)
        {
            Log.Error("Failed to export codex accounts", ex);
            throw;
        }
    }

    public async Task ImportAccountsAsync(string sourcePath)
    {
        try
        {
            var json = await File.ReadAllTextAsync(sourcePath);
            var list = JsonSerializer.Deserialize<List<CodexAccount>>(json);
            if (list != null)
            {
                using var doc = JsonDocument.Parse(json);
                foreach (var acc in list)
                {
                    if (doc.RootElement.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var element in doc.RootElement.EnumerateArray())
                        {
                            if (element.TryGetProperty("id", out var idProp) && idProp.GetString() == acc.id)
                            {
                                var oldAccess = element.TryGetProperty("access_token", out var aProp) ? aProp.GetString() : null;
                                var oldRefresh = element.TryGetProperty("refresh_token", out var rProp) ? rProp.GetString() : null;
                                
                                if (!string.IsNullOrEmpty(oldAccess)) acc.access_token = oldAccess;
                                if (!string.IsNullOrEmpty(oldRefresh)) acc.refresh_token = oldRefresh;
                                break;
                            }
                        }
                    }
                    AddOrUpdateAccount(acc);
                }
            }
        }
        catch (Exception ex)
        {
            Log.Error("Failed to import codex accounts", ex);
            throw;
        }
    }
}
