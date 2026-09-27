using CodexBarWin.Models;
using CodexBarWin.Interop;
using CodexBarWin.Services;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;

var scratch = Path.Combine(Path.GetTempPath(), "wincodexbar-test-" + Guid.NewGuid().ToString("N"));
Environment.SetEnvironmentVariable("CODEXBAR_HOME", scratch);
Directory.CreateDirectory(scratch);
var passed = 0;
void Check(bool result, string name) { if (!result) throw new Exception(name); Console.WriteLine("PASS " + name); passed++; }
void Reject(Action action, string name) { try { action(); } catch (InvalidDataException) { Check(true, name); return; } throw new Exception("Expected rejection: " + name); }
try
{
    var defaults = new CodexBarConfig();
    Check(defaults.TrayIconStyle == "ringpercent", "new installs default to ring with number tray icon");
    Check(!defaults.KeepAwakeEnabled && !defaults.AdvancedKeepAwakeEnabled, "keep-awake modes are disabled for new installs");
    Check(!defaults.ImageStudioEnabled && !defaults.VectorStudioEnabled && !defaults.QualityCheckEnabled, "creative tools are disabled for new installs");
    Check(defaults.VectorStudio.Model == "gpt-6-luna" && defaults.VectorStudio.Effort == "low" && defaults.ImageStudio.VectorModel == "gpt-6-luna" && defaults.ImageStudio.VectorEffort == "low", "SVG conversion keeps its original default model and low reasoning");
    Check(defaults.AwayModeDelaySeconds == 0, "away mode starts with no countdown");
    var settings = new CodexBarConfigStore();
    settings.Update(c => { c.KeepAwakeEnabled = false; c.AwayModeDelaySeconds = 15; c.AdvancedKeepAwakeEnabled = false; c.StartWithWindows = false; c.AutoCheckUpdates = false; c.UiScalePercent = 200; c.TrayIconStyle = "percent"; c.Global.DefaultModel = "custom-model"; c.ImageStudioEnabled = true; c.VectorStudioEnabled = true; c.QualityCheckEnabled = true; });
    AppDataMigration.EnsureVersionBackup();
    AppDataMigration.EnsureVersionBackup();
    Check(Directory.GetDirectories(Path.Combine(CodexPaths.CodexBarRoot,"backups")).Length == 1, "upgrade backup runs once per version");
    settings.Load();
    Check(!settings.Config.KeepAwakeEnabled && !settings.Config.AdvancedKeepAwakeEnabled && !settings.Config.StartWithWindows && !settings.Config.AutoCheckUpdates, "false settings survive restart");
    Check(settings.Config.AwayModeDelaySeconds == 15, "away delay choice survives restart");
    Check(settings.Config.UiScalePercent == 200 && settings.Config.TrayIconStyle == "percent", "appearance survives restart");
    Check(settings.Config.ImageStudioEnabled && settings.Config.VectorStudioEnabled && settings.Config.QualityCheckEnabled && settings.Config.Clone().VectorStudioEnabled, "creative tool switches survive restart");
    Check(settings.Config.Global.DefaultModel == "custom-model", "upgrade preserves custom model");
    Check(settings.Config.OpenAI.TokenPricePresets.ContainsKey("gpt-6-astra") && settings.Config.OpenAI.TokenPricePresets["gpt-6-sol"].OutputUsdPerMillion == 10, "GPT-6 presets merged");
    Check(settings.Config.Clone().AwayModeDelaySeconds == 15, "away delay survives settings drafts");
    var clone = settings.Config.Clone(); clone.OpenAI.TokenPricePresets["gpt-6-sol"].OutputUsdPerMillion = 1;
    Check(settings.Config.OpenAI.TokenPricePresets["gpt-6-sol"].OutputUsdPerMillion == 10, "draft pricing isolated");
    File.WriteAllText(CodexPaths.WindowsSettingsPath, "{\"KeepAwakeEnabled\":false,\"away_mode_delay_seconds\":9999,\"openai\":{\"auto_refresh_interval_seconds\":2147483647},\"ui_scale_percent\":-5}");
    settings.Load();
    Check(!settings.Config.KeepAwakeEnabled && settings.Config.AwayModeDelaySeconds == 0 && settings.Config.OpenAI.AutoRefreshIntervalSeconds == 86400 && settings.Config.UiScalePercent == 0, "legacy settings and invalid ranges");
    File.WriteAllText(CodexPaths.WindowsSettingsPath, "{\"away_mode_delay_seconds\":999}");
    settings.Load();
    Check(settings.Config.AwayModeDelaySeconds == 999, "custom away delay survives restart");
    File.WriteAllText(Path.Combine(CodexPaths.CodexRoot,"models_cache.json"), "{\"models\":[{\"slug\":\"gpt-future\",\"supported_reasoning_levels\":[{\"effort\":\"ultra\"}],\"service_tiers\":[{\"id\":\"priority\"}]}]}");
    Check(CodexModelCatalog.Models().Contains("gpt-future") && CodexModelCatalog.Efforts("gpt-future").Contains("ultra"), "local model catalog extends choices");
    Check(CodexModelCatalog.ConfigTier("gpt-future","standard") is null && CodexModelCatalog.ConfigTier("gpt-future","priority") == "fast", "service tier writes supported values only");
    var toml = "# settings\n[profiles.test]\nmodel = \"profile-model\"\n";
    var edited = TomlRootEditor.Set(toml,"model","\"gpt-6-sol\"");
    Check(edited.IndexOf("gpt-6-sol", StringComparison.Ordinal) < edited.IndexOf("[profiles", StringComparison.Ordinal) && edited.Contains("profile-model"), "model inserted at root preserves profiles");
    var removed = TomlRootEditor.Set("service_tier = \"fast\"\n[profiles.test]\nservice_tier = \"priority\"\n","service_tier",null);
    Check(!removed.Contains("fast") && removed.Contains("priority"), "remove root tier preserves profile tier");
    var multiline = "instructions = \"\"\"\n[example]\nmodel = example\n\"\"\"\n[profiles.test]\n";
    Check(TomlRootEditor.Set(multiline,"model","\"gpt-6-sol\"").StartsWith(multiline[..multiline.IndexOf("[profiles",StringComparison.Ordinal)]), "multiline instructions remain unchanged");
    var weekly = new TokenAccount { PrimaryUsedPercent = 95, PrimaryWindowAvailable = true };
    using var weeklyJson = JsonDocument.Parse("""{"rate_limit":{"primary_window":null,"secondary_window":{"used_percent":0,"limit_window_seconds":604800}}}""");
    OpenAIUsageService.ApplyUsagePayload(weekly, weeklyJson.RootElement);
    Check(AccountUsageHelpers.Windows(weekly).Count == 1 && AccountUsageHelpers.Windows(weekly)[0].Label == "7d", "missing five-hour window hidden and stale data cleared");
    Check(AccountUsageHelpers.UsageText(weekly, UsageDisplayMode.Used) == "7d 0.0%", "real zero usage remains visible");
    Check(AccountUsageHelpers.ResetText(weekly).Contains(AccountUsageHelpers.ExactTime(weekly.PrimaryResetAt)), "reset summary uses calendar date");
    Check(!AccountUsageHelpers.ResetText(weekly).Contains("5h"), "missing five-hour reset hidden");
    using var primaryWeekly = JsonDocument.Parse("""{"rate_limits":{"primary":{"used_percent":42,"window_minutes":10080},"secondary":null}}""");
    OpenAIUsageService.ApplyUsagePayload(weekly, primaryWeekly.RootElement);
    Check(AccountUsageHelpers.UsageText(weekly, UsageDisplayMode.Used) == "7d 42.0%", "weekly primary bucket labeled by window duration");
    var both = new TokenAccount { PrimaryWindowAvailable = true, SecondaryWindowAvailable = true, PrimaryUsedPercent = 10, SecondaryUsedPercent = 20 };
    Check(AccountUsageHelpers.AverageText(new[] { weekly, both }, UsageDisplayMode.Used) == "5h 10.0% · 7d 31.0%", "averages exclude missing windows");
    Check(AccountUsageHelpers.Windows(new TokenAccount()).Count == 0, "unknown quota is not zero usage");
    var alerts = new QuotaAlertTracker();
    var low = new TokenAccount { AccountId = "quota-fixture", PrimaryWindowAvailable = true, PrimaryUsedPercent = 72 };
    Check(alerts.Evaluate(low, 70, 90).Single().RemainingPercent == 28, "low quota alerts at configured warning threshold");
    Check(alerts.Evaluate(low, 70, 90).Count == 0, "unchanged low quota does not repeat notification");
    low.PrimaryUsedPercent = 91;
    Check(alerts.Evaluate(low, 70, 90).Single().Critical, "critical quota escalation alerts once");
    low.PrimaryUsedPercent = 3;
    Check(alerts.Evaluate(low, 70, 90).Count == 0, "quota recovery clears alert level silently");
    low.PrimaryUsedPercent = 75;
    Check(alerts.Evaluate(low, 70, 90).Count == 1, "later threshold crossing alerts again");
    Check(AccountUsageHelpers.Windows(JsonSerializer.Deserialize<TokenAccount>(JsonSerializer.Serialize(weekly))!).Single().Label == "7d", "window availability survives restart");
    using var cards = JsonDocument.Parse("""{"available_count":3,"credits":[{"status":"available","expires_at":"2026-10-04T08:00:00Z"},{"status":"redeemed","expires_at":"2026-10-03T08:00:00Z"},{"status":"available","expires_at":null}]}""");
    Check(OpenAIUsageService.ApplyResetCreditPayload(weekly, cards.RootElement), "read-only reset credit data parsed");
    Check(weekly.ResetCreditsAvailable == 3 && weekly.ResetCreditDetails!.Count == 2, "credit count independent from capped detail list");
    Check(weekly.ResetCreditDetails![0].ExpiresAt == DateTimeOffset.Parse("2026-10-04T08:00:00Z") && weekly.ResetCreditDetails[1].ExpirationKnown, "expiry instant and explicit unlimited expiry preserved");
    var detailsText = AccountUsageHelpers.DetailsText(weekly, UsageDisplayMode.Used);
    Check(detailsText.Contains("其余重置卡") && detailsText.Contains("无到期限制") && detailsText.Contains("2026-10-04"), "details show precise expiry and missing detail notice");
    using var onlyCount = JsonDocument.Parse("""{"available_count":4}""");
    OpenAIUsageService.ApplyResetCreditPayload(weekly, onlyCount.RootElement, details:false);
    Check(weekly.ResetCreditsAvailable == 4 && weekly.ResetCreditDetailsStale, "changed count marks old expiry details as cached");
    using var noCards = JsonDocument.Parse("""{"available_count":0,"credits":[]}""");
    OpenAIUsageService.ApplyResetCreditPayload(weekly,noCards.RootElement);
    Check(weekly.ResetCreditsAvailable == 0 && weekly.ResetCreditDetails!.Count == 0 && !weekly.ResetCreditDetailsStale, "zero credits clear stale expirations");
    Check(AccountUsageHelpers.ExactTime(DateTimeOffset.Parse("2026-10-04T08:01:02Z")).Contains(":02 "), "exact times include seconds and UTC offset");
    using var handler = new UsageFixtureHandler();
    var reader = new OpenAIUsageService(new HttpClient(handler));
    var fixtureAccount = new TokenAccount { AccessToken = "fixture-token", AccountId = "fixture-account" };
    Check(await reader.RefreshUsageAsync(fixtureAccount) == UsageRefreshOutcome.Updated && fixtureAccount.ResetCreditsAvailable == 2, "usage refresh reads reset credit endpoint");
    Check(handler.Requests.Any(r => r.EndsWith("/rate-limit-reset-credits")) && handler.OnlyGets, "reset credit integration sends GET requests only");
    var registry = new AccountRegistry();
    registry.UpsertAccount(new TokenAccount { AccountId = "fake-a", Email = "a@example.test" }, true);
    registry.UpsertAccount(new TokenAccount { AccountId = "fake-b", Email = "b@example.test" }, false);
    Check(registry.RemoveAccount("fake-a") && registry.ActiveAccountId == "fake-b", "delete active chooses remaining account");
    registry.Save(); registry.Load(); Check(registry.Accounts.Count == 1 && registry.Accounts[0].AccountId == "fake-b", "deleted account stays deleted after reload");
    Check(registry.RemoveAccount("fake-b") && registry.ActiveAccountId is null, "delete last account clears selection");
    Check(!registry.RemoveAccount("fake-b"), "repeat delete harmless");
    using var radar = JsonDocument.Parse("{\"window_open\":true,\"window\":{\"opened_at\":\"2026-09-26T08:07:13+08:00\"},\"prediction\":{\"level\":\"low\",\"updated_at\":\"2026-07-13T22:08:06+08:00\"}}");
    var prediction = CodexRadarService.Parse(radar.RootElement);
    Check(prediction.WindowOpen == true && prediction.DisplayText.Contains("速蹬窗口开启") && !prediction.DisplayText.Contains("低"), "current window takes precedence over stale probability");
    using var closed = JsonDocument.Parse("{\"window_open\":false,\"prediction\":null}");
    Check(CodexRadarService.Parse(closed.RootElement).DisplayText.Contains("未开启"), "explicit closed window supported without prediction");
    Check(CodexRadarPrediction.Loading.DisplayText.Contains("获取中") && CodexRadarPrediction.Unavailable.DisplayText.Contains("暂不可用"), "radar state remains visible without data");
    var radarAlerts = new CodexRadarAlertTracker();
    Check(!radarAlerts.Observe(CodexRadarPrediction.Unavailable), "unavailable radar does not trigger alert");
    Check(radarAlerts.Observe(prediction), "newly opened radar window triggers alert");
    Check(!radarAlerts.Observe(prediction) && !radarAlerts.Observe(prediction with { IsStale = true }), "repeated and stale radar polls stay quiet");
    Check(!radarAlerts.Observe(CodexRadarPrediction.Unavailable with { IsAvailable = true, WindowOpen = false }), "closed radar window resets tracker silently");
    Check(radarAlerts.Observe(prediction), "reopened radar window alerts again");
    using var empty = JsonDocument.Parse("{}"); Check(!CodexRadarService.Parse(empty.RootElement).IsAvailable, "missing radar data unavailable");
    string Release(string tag, string architecture = "x64", bool preview = false) => JsonSerializer.Serialize(new {
        tag_name = tag, draft = false, prerelease = preview,
        assets = new[] { new { name = $"WinCodexBar-{tag.TrimStart('v')}-win-{architecture}.zip", browser_download_url = $"https://github.com/windycn/WinCodexBar/releases/download/{tag}/WinCodexBar-{tag.TrimStart('v')}-win-{architecture}.zip" },
            new { name = "SHA256SUMS.txt", browser_download_url = $"https://github.com/windycn/WinCodexBar/releases/download/{tag}/SHA256SUMS.txt" } }
    });
    foreach (var arch in new[] { "x86", "x64", "arm64" })
        Check(AppUpdateService.ParseRelease(Release("v0.3.0", arch), new Version(0,2,0,0), arch)?.AssetName.EndsWith(arch + ".zip") == true, "matching update architecture " + arch);
    Check(AppUpdateService.ParseRelease(Release("v0.2.1"), new Version(0,2,0,0), "x64")?.Version == new Version(0,2,1), "0.2.0 can update directly to 0.2.1");
    Check(AppUpdateService.ParseRelease(Release("v0.2.0"), new Version(0,2,0,0), "x64") is null, "same version not updated");
    Check(AppUpdateService.IsNewBuild(new string('a', 40), null), "same-version release with build marker updates old installation");
    Check(!AppUpdateService.IsNewBuild(new string('a', 40), new string('A', 40)), "matching build marker skips update");
    Check(!AppUpdateService.IsNewBuild("invalid", null), "invalid build marker is ignored");
    Check(AppUpdateService.ParseRelease(Release("v0.3.0", preview:true), new Version(0,2,0), "x64") is null, "prerelease ignored");
    Reject(() => AppUpdateService.ParseRelease(Release("v0.3.0").Replace("https://github.com/", "https://example.test/"), new Version(0,2,0), "x64"), "foreign download rejected");
    Reject(() => AppUpdateService.ParseRelease(Release("v0.3.0"), new Version(0,2,0), "arm64"), "missing architecture rejected");
    var latestPage = new Uri("https://github.com/windycn/WinCodexBar/releases/tag/v0.3.0");
    Check(AppUpdateService.ParseLatestReleaseUri(latestPage, new Version(0,2,0), "x64")?.Version == new Version(0,3,0), "public release fallback parses version");
    Check(AppUpdateService.ParseLatestReleaseUri(latestPage, new Version(0,3,0), "x64") is null, "public release fallback ignores same version");
    Reject(() => AppUpdateService.ParseLatestReleaseUri(new Uri("https://example.test/windycn/WinCodexBar/releases/tag/v0.3.0"), new Version(0,2,0), "x64"), "public release fallback rejects foreign host");
    Reject(() => AppUpdateService.ParseLatestReleaseUri(new Uri("https://github.com/other/WinCodexBar/releases/tag/v0.3.0"), new Version(0,2,0), "x64"), "public release fallback rejects foreign repository");
    var zip = Path.Combine(scratch, "test.zip");
    using (var archive = ZipFile.Open(zip, ZipArchiveMode.Create)) { using var writer = new StreamWriter(archive.CreateEntry("WinCodexBar.exe").Open()); writer.Write("fake binary"); }
    var hash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(zip)));
    AppUpdateService.VerifyChecksum(zip, "test.zip", hash + "  test.zip"); Check(true, "matching checksum accepted");
    Reject(() => AppUpdateService.VerifyChecksum(zip, "test.zip", new string('0',64) + "  test.zip"), "corrupt download rejected");
    AppUpdateService.ExtractPackage(zip, Path.Combine(scratch,"valid")); Check(File.Exists(Path.Combine(scratch,"valid","WinCodexBar.exe")), "valid update package extracted");
    var bad = Path.Combine(scratch,"bad.zip"); using (var archive = ZipFile.Open(bad, ZipArchiveMode.Create)) archive.CreateEntry("../escape");
    Reject(() => AppUpdateService.ExtractPackage(bad, Path.Combine(scratch,"bad")), "zip traversal rejected");
    var script = AppUpdateService.BuildInstallScript("C:\\O'Brien\\package", "C:\\app", "C:\\data", 1234);
    Check(script.Contains("O''Brien") && script.Contains("previous-program") && script.Contains("before-update-"), "installer escapes paths and includes backups");
    static string Jwt(string json) => "header." + Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(json)).TrimEnd('=').Replace('+', '-').Replace('/', '_') + ".signature";
    var importFixture = new TokenAccount
    {
        Email = "fixture@example.test", AccountId = "user__acct", OpenAIAccountId = "acct",
        AccessToken = Jwt("""{"https://api.openai.com/auth":{"chatgpt_account_id":"acct","chatgpt_user_id":"user"},"exp":1900000000}"""),
        IdToken = Jwt("""{"email":"fixture@example.test"}"""), RefreshToken = "rt-fixture"
    };
    var flatExport = OpenAIAccountCSVService.ExportFlatJSON([importFixture]);
    Check(OpenAIAccountCSVService.Parse(flatExport).Accounts.Single().AccountId == "user__acct", "Codex2API flat JSON import/export roundtrip");
    var csvExport = OpenAIAccountCSVService.ExportLegacyCSV([importFixture], importFixture.AccountId);
    Check(OpenAIAccountCSVService.Parse(csvExport).ActiveAccountId == importFixture.AccountId, "legacy CSV import/export roundtrip");
    var bundleExport = OpenAIAccountCSVService.ExportInteropBundle([importFixture], new Dictionary<string, OAuthAccountInteropMetadata>(), null, importFixture.AccountId);
    Check(OpenAIAccountCSVService.Parse(bundleExport).Accounts.Single().Email == importFixture.Email, "codexbar JSON remains default compatible format");
    Console.WriteLine($"{passed} checks passed");
}
finally { Directory.Delete(scratch, true); }

internal sealed class UsageFixtureHandler : HttpMessageHandler
{
    public List<string> Requests { get; } = new();
    public bool OnlyGets { get; private set; } = true;
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        OnlyGets &= request.Method == HttpMethod.Get;
        var path = request.RequestUri!.AbsolutePath; Requests.Add(path);
        var json = path.EndsWith("/rate-limit-reset-credits")
            ? """{"available_count":2,"credits":[{"status":"available","expires_at":"2026-10-04T08:00:00Z"}]}"""
            : path.EndsWith("/usage") ? """{"rate_limit":{"primary_window":null,"secondary_window":{"used_percent":42,"limit_window_seconds":604800}},"rate_limit_reset_credits":{"available_count":2}}"""
            : "{}";
        return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = new StringContent(json) });
    }
}
