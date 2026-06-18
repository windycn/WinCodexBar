using CodexBarWin.Models;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace CodexBarWin.Interop;

public readonly record struct ParsedOpenAIAccountCSV(
    IReadOnlyList<TokenAccount> Accounts,
    string? ActiveAccountId,
    int RowCount,
    OAuthAccountImportInterchangeContext InteropContext
);

public class OAuthAccountInteropMetadata
{
    public string? ProxyKey { get; set; }
    public string? Notes { get; set; }
    public int? Concurrency { get; set; }
    public int? Priority { get; set; }
    public double? RateMultiplier { get; set; }
    public bool? AutoPauseOnExpired { get; set; }
    public string? CredentialsJSON { get; set; }
    public string? ExtraJSON { get; set; }
    public string? OriginalAccountJSON { get; set; }
}

public class OAuthAccountImportInterchangeContext
{
    public Dictionary<string, OAuthAccountInteropMetadata> AccountMetadataByID { get; set; } = new();
    public string? ProxiesJSON { get; set; }

    public bool IsEmpty =>
        AccountMetadataByID.Count == 0 && (ProxiesJSON == null || string.IsNullOrWhiteSpace(ProxiesJSON));
}

public enum OpenAIAccountImportError
{
    EmptyFile,
    UnsupportedDataType,
    UnsupportedFormatVersion,
    NoImportableAccounts,
    MissingRequiredColumns,
    MissingRequiredValue,
    InvalidCSV,
    InvalidActiveValue,
    DuplicateAccountId,
    MultipleActiveAccounts,
    InvalidAccount,
    AccountIdMismatch,
    EmailMismatch,
}

public class OpenAIAccountImportException : Exception
{
    public OpenAIAccountImportError Code { get; }

    public OpenAIAccountImportException(OpenAIAccountImportError code, string message)
        : base(message)
    {
        Code = code;
    }
}

public static class OpenAIAccountCSVService
{
    public const string FormatVersion = "v1";

    public static readonly string[] HeaderOrder =
    {
        "format_version",
        "email",
        "account_id",
        "access_token",
        "refresh_token",
        "id_token",
        "is_active",
    };

    public static ParsedOpenAIAccountCSV Parse(string text)
    {
        var normalized = Normalize(text);
        if (string.IsNullOrWhiteSpace(normalized))
        {
            throw new OpenAIAccountImportException(OpenAIAccountImportError.EmptyFile, "文件内容为空");
        }

        var trimmed = normalized.TrimStart().TrimStart('\uFEFF');
        if (trimmed.StartsWith("{", StringComparison.Ordinal))
        {
            return ParseInteropJSON(trimmed);
        }

        return ParseLegacyCSV(normalized);
    }

    public static string ExportInteropBundle(
        IReadOnlyList<TokenAccount> accounts,
        IReadOnlyDictionary<string, OAuthAccountInteropMetadata> metadataById,
        string? proxiesJSON,
        string? activeAccountId = null,
        DateTimeOffset? now = null
    )
    {
        var payload = new JsonObject();
        var proxyObjects = new List<JsonObject>();
        var proxyKeys = new HashSet<string>(StringComparer.Ordinal);

        if (!string.IsNullOrWhiteSpace(proxiesJSON))
        {
            try
            {
                var parsed = JsonNode.Parse(proxiesJSON!);
                if (parsed is JsonArray array)
                {
                    foreach (var item in array)
                    {
                        if (item is JsonObject obj)
                        {
                            proxyObjects.Add(obj);

                            if (obj.TryGetPropertyValue("proxy_key", out var proxyKeyNode)
                                && proxyKeyNode?.GetValue<string>() is { } key
                                && string.IsNullOrWhiteSpace(key) == false)
                            {
                                proxyKeys.Add(key);
                            }
                        }
                    }
                }
            }
            catch
            {
                // 与 Swift 兼容：上游异常输入不影响导入，只是导出空 proxies
                proxyObjects.Clear();
            }
        }

        var accountsNode = new JsonArray();
        foreach (var account in accounts)
        {
            var metadata = metadataById.GetValueOrDefault(account.AccountId);
            accountsNode.Add(MakeInteropAccountObject(account, metadata, proxyKeys));
        }

        payload["exported_at"] = (now ?? DateTimeOffset.UtcNow).ToString("o", CultureInfo.InvariantCulture);
        if (!string.IsNullOrWhiteSpace(activeAccountId))
        {
            payload["active_account_id"] = activeAccountId;
        }
        payload["proxies"] = new JsonArray(proxyObjects.Cast<JsonNode>().ToArray());
        payload["accounts"] = accountsNode;

        var options = new JsonSerializerOptions
        {
            WriteIndented = true,
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        };

        return payload.ToJsonString(options) + "\n";
    }

    private static JsonObject MakeInteropAccountObject(
        TokenAccount account,
        OAuthAccountInteropMetadata? metadata,
        ISet<string> availableProxyKeys
    )
    {
        var credentials = ParseJsonObject(metadata?.CredentialsJSON) ?? new JsonObject();
        var tokenMetadata = ParseTokenMetadata(
            account.AccessToken,
            account.RefreshToken,
            account.IdToken
        );
        var emailForExport = string.IsNullOrWhiteSpace(account.Email)
            ? tokenMetadata.Email
            : account.Email;
        var accountIdForExport = string.IsNullOrWhiteSpace(account.OpenAIAccountId)
            ? tokenMetadata.OpenAIAccountId
            : account.OpenAIAccountId;

        if (!string.IsNullOrWhiteSpace(account.AccessToken))
        {
            credentials["access_token"] = account.AccessToken;
        }

        if (!string.IsNullOrWhiteSpace(account.RefreshToken))
        {
            credentials["refresh_token"] = account.RefreshToken;
        }

        if (!string.IsNullOrWhiteSpace(account.IdToken))
        {
            credentials["id_token"] = account.IdToken;
        }

        if (string.IsNullOrWhiteSpace(accountIdForExport) == false)
        {
            credentials["chatgpt_account_id"] = accountIdForExport;
        }

        if (string.IsNullOrWhiteSpace(tokenMetadata.ChatGPTUserId) == false)
        {
            credentials["chatgpt_user_id"] = tokenMetadata.ChatGPTUserId;
        }

        if (!string.IsNullOrWhiteSpace(account.OAuthClientId))
        {
            credentials["client_id"] = account.OAuthClientId;
        }

        if (string.IsNullOrWhiteSpace(emailForExport) == false)
        {
            credentials["email"] = emailForExport;
        }

        if (!string.IsNullOrWhiteSpace(account.PlanType))
        {
            credentials["plan_type"] = account.PlanType;
        }

        if (account.ExpiresAt is not null)
        {
            var unix = Math.Max(0, account.ExpiresAt.Value.ToUnixTimeSeconds());
            credentials["expires_at"] = unix;
        }

        var extra = ParseJsonObject(metadata?.ExtraJSON);
        if (extra is null)
        {
            if (string.IsNullOrWhiteSpace(emailForExport) == false)
            {
                extra = new JsonObject { ["email"] = emailForExport };
            }
            else
            {
                extra = new JsonObject();
            }
        }
        else if (string.IsNullOrWhiteSpace(GetNodeText(extra["email"]))
                 && string.IsNullOrWhiteSpace(emailForExport) == false)
        {
            extra["email"] = emailForExport;
        }

        var exported = ParseJsonObject(metadata?.OriginalAccountJSON) ?? new JsonObject();
        exported["name"] = string.IsNullOrWhiteSpace(account.Email) ? account.AccountId : account.Email;
        exported["platform"] = "openai";
        exported["type"] = "oauth";
        exported["credentials"] = credentials;
        exported["concurrency"] = metadata?.Concurrency ?? ParseInt(exported["concurrency"]) ?? 1;
        exported["priority"] = metadata?.Priority ?? ParseInt(exported["priority"]) ?? 1;
        exported["rate_multiplier"] = metadata?.RateMultiplier ?? ParseDouble(exported["rate_multiplier"]) ?? 1;
        exported["auto_pause_on_expired"] = metadata?.AutoPauseOnExpired ?? ParseBool(exported["auto_pause_on_expired"]) ?? true;

        if (extra is not null && extra.Count > 0)
        {
            exported["extra"] = extra;
        }

        if (!string.IsNullOrWhiteSpace(metadata?.Notes))
        {
            exported["notes"] = metadata.Notes;
        }

        if (!string.IsNullOrWhiteSpace(metadata?.ProxyKey) && availableProxyKeys.Contains(metadata.ProxyKey))
        {
            exported["proxy_key"] = metadata.ProxyKey;
        }

        if (account.ExpiresAt is not null)
        {
            exported["expires_at"] = Math.Max(0, account.ExpiresAt.Value.ToUnixTimeSeconds());
        }

        return exported;
    }

    private static JsonObject? ParseJsonObject(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        try
        {
            var node = JsonNode.Parse(text);
            return node as JsonObject;
        }
        catch
        {
            return null;
        }
    }

    private static ParsedOpenAIAccountCSV ParseInteropJSON(string text)
    {
        JsonObject payload;
        try
        {
            payload = JsonNode.Parse(text) as JsonObject
                ?? throw new OpenAIAccountImportException(OpenAIAccountImportError.UnsupportedDataType, "JSON 格式无效");
        }
        catch (JsonException ex)
        {
            throw new OpenAIAccountImportException(OpenAIAccountImportError.UnsupportedDataType, $"JSON 格式无效：{ex.Message}");
        }

        if (payload.TryGetPropertyValue("type", out var typeNode))
        {
            var typeString = GetTrimmedString(GetNodeText(typeNode));
            if (string.IsNullOrWhiteSpace(typeString) == false
                && string.Equals(typeString, "rhino2api-data", StringComparison.OrdinalIgnoreCase) == false
                && string.Equals(typeString, "rhino2api-bundle", StringComparison.OrdinalIgnoreCase) == false)
            {
                throw new OpenAIAccountImportException(OpenAIAccountImportError.UnsupportedDataType, "不支持的导入类型");
            }
        }

        if (!payload.TryGetPropertyValue("accounts", out var accountNodes)
            || accountNodes is not JsonArray accountArray)
        {
            throw new OpenAIAccountImportException(OpenAIAccountImportError.NoImportableAccounts, "未发现 accounts");
        }

        var accounts = new List<TokenAccount>(capacity: accountArray.Count);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var interopMetadataByID = new Dictionary<string, OAuthAccountInteropMetadata>(StringComparer.Ordinal);

        var accountIndex = 0;
        foreach (var item in accountArray)
        {
            accountIndex += 1;
            if (item is not JsonObject obj)
            {
                continue;
            }

            var platform = GetTrimmedString(GetNodeText(obj["platform"])).ToLowerInvariant();
            var type = GetTrimmedString(GetNodeText(obj["type"])).ToLowerInvariant();

            if (platform != "openai")
            {
                continue;
            }

            if (string.IsNullOrWhiteSpace(type) == false && type != "oauth")
            {
                continue;
            }

            if (obj["credentials"] is not JsonObject credentials)
            {
                throw new OpenAIAccountImportException(OpenAIAccountImportError.MissingRequiredValue, "账号缺少 credentials");
            }

            var accessToken = GetTrimmedString(GetNodeText(credentials["access_token"]));
            var refreshToken = GetTrimmedString(GetNodeText(credentials["refresh_token"]));
            var idToken = GetTrimmedString(GetNodeText(credentials["id_token"]));
            if (string.IsNullOrEmpty(accessToken) || string.IsNullOrEmpty(refreshToken) || string.IsNullOrEmpty(idToken))
            {
                throw new OpenAIAccountImportException(OpenAIAccountImportError.MissingRequiredValue, "OAuth 凭证字段缺失");
            }

            var tokenMetadata = ParseTokenMetadata(accessToken, refreshToken, idToken);
            var declaredAccountId = GetTrimmedString(GetNodeText(credentials["account_id"]));
            if (string.IsNullOrWhiteSpace(declaredAccountId))
            {
                declaredAccountId = GetTrimmedString(GetNodeText(credentials["chatgpt_account_id"]));
            }
            var declaredEmail = GetNodeText(credentials["email"]);
            if (string.IsNullOrWhiteSpace(declaredEmail) && obj["extra"] is JsonObject extraForEmail)
            {
                declaredEmail = GetNodeText(extraForEmail["email"]);
            }
            declaredEmail = GetTrimmedString(declaredEmail);
            var accountId = NormalizeClaimedAccountId(declaredAccountId, tokenMetadata.AccountId, tokenMetadata.OpenAIAccountId);
            var email = string.IsNullOrWhiteSpace(declaredEmail) ? tokenMetadata.Email ?? string.Empty : declaredEmail;
            var openAIAccountId = tokenMetadata.OpenAIAccountId;

            if (string.IsNullOrWhiteSpace(declaredAccountId) == false
                && declaredAccountId != accountId
                && declaredAccountId != openAIAccountId)
            {
                throw new OpenAIAccountImportException(
                    OpenAIAccountImportError.AccountIdMismatch,
                    $"第 {accountIndex} 条记录 account_id 与 token 中解析值不一致"
                );
            }

            if (string.IsNullOrWhiteSpace(declaredEmail) == false
                && tokenMetadata.Email is not null
                && string.Equals(declaredEmail, tokenMetadata.Email, StringComparison.Ordinal) == false)
            {
                throw new OpenAIAccountImportException(
                    OpenAIAccountImportError.EmailMismatch,
                    $"第 {accountIndex} 条记录 email 与 token 中解析值不一致"
                );
            }

            if (string.IsNullOrEmpty(accountId))
            {
                throw new OpenAIAccountImportException(OpenAIAccountImportError.InvalidAccount, $"第 {accountIndex} 条记录无法解析 account_id");
            }

            if (seen.Add(accountId) == false)
            {
                throw new OpenAIAccountImportException(OpenAIAccountImportError.DuplicateAccountId, $"第 {accountIndex} 条记录 account_id 重复：{accountId}");
            }

            var expiresAt = ParseUnixSeconds(credentials["expires_at"])
                ?? ParseUnixSeconds(obj["expires_at"])
                ?? tokenMetadata.ExpiresAt;

            var proxyKey = GetTrimmedString(GetNodeText(obj["proxy_key"]));
            var notes = GetTrimmedString(GetNodeText(obj["notes"]));
            var concurrency = ParseInt(obj["concurrency"]);
            var priority = ParseInt(obj["priority"]);
            var rateMultiplier = ParseDouble(obj["rate_multiplier"]);
            var autoPause = ParseBool(obj["auto_pause_on_expired"]);

            var parsedAccount = new TokenAccount
            {
                Email = email ?? string.Empty,
                AccountId = accountId,
                OpenAIAccountId = openAIAccountId ?? accountId,
                AccessToken = accessToken,
                RefreshToken = refreshToken,
                IdToken = idToken,
                ExpiresAt = expiresAt,
                OAuthClientId = GetTrimmedString(GetNodeText(credentials["client_id"])) ?? tokenMetadata.OAuthClientId,
                PlanType = GetTrimmedString(GetNodeText(credentials["plan_type"])) ?? tokenMetadata.PlanType ?? "free",
                IsActive = false,
            };

            accounts.Add(parsedAccount);
            interopMetadataByID[accountId] = new OAuthAccountInteropMetadata
            {
                ProxyKey = proxyKey,
                Notes = notes,
                Concurrency = concurrency,
                Priority = priority,
                RateMultiplier = rateMultiplier,
                AutoPauseOnExpired = autoPause,
                CredentialsJSON = credentials.ToJsonString(),
                ExtraJSON = obj["extra"]?.ToJsonString(),
                OriginalAccountJSON = obj.ToJsonString(),
            };
        }

        if (accounts.Count == 0)
        {
            throw new OpenAIAccountImportException(OpenAIAccountImportError.NoImportableAccounts, "未发现可导入的 openai oauth 账号");
        }

        string? active = null;
        if (payload.TryGetPropertyValue("active_account_id", out var activeNode))
        {
            active = GetTrimmedString(GetNodeText(activeNode));
            if (active != null && accounts.Any(a => a.AccountId == active) == false)
            {
                active = null;
            }
        }

        return new ParsedOpenAIAccountCSV(
            accounts,
            active,
            accounts.Count,
            new OAuthAccountImportInterchangeContext
            {
                AccountMetadataByID = interopMetadataByID,
                ProxiesJSON = payload["proxies"]?.ToJsonString(),
            });
    }

    private static ParsedOpenAIAccountCSV ParseLegacyCSV(string text)
    {
        var lines = Normalize(text).Split('\n');
        var headerIndex = Array.FindIndex(lines, line => string.IsNullOrWhiteSpace(line.Trim()) == false);
        if (headerIndex < 0)
        {
            throw new OpenAIAccountImportException(OpenAIAccountImportError.EmptyFile, "文件内容为空");
        }

        var headers = SplitCsvLine(lines[headerIndex]);
        var headerSet = new HashSet<string>(headers.Select(v => v.Trim()), StringComparer.Ordinal);
        foreach (var required in HeaderOrder)
        {
            if (headerSet.Contains(required) == false)
            {
                throw new OpenAIAccountImportException(OpenAIAccountImportError.MissingRequiredColumns, "缺少必填列");
            }
        }

        var headerMap = headers
            .Select((name, index) => new { name, index })
            .Where(item => string.IsNullOrWhiteSpace(item.name) == false)
            .ToDictionary(item => item.name.Trim(), item => item.index, StringComparer.Ordinal);

        var accounts = new List<TokenAccount>(Math.Max(0, lines.Length - headerIndex - 1));
        var seen = new HashSet<string>(StringComparer.Ordinal);
        string? activeAccountId = null;

        for (var row = headerIndex + 1; row < lines.Length; row += 1)
        {
            if (string.IsNullOrWhiteSpace(lines[row]))
            {
                continue;
            }

            var rowNo = row + 1;
            var columns = SplitCsvLine(lines[row]);
            if (columns.Count != headers.Count)
            {
                throw new OpenAIAccountImportException(OpenAIAccountImportError.InvalidCSV, $"第 {rowNo} 行列数不正确");
            }

            string Value(string key)
            {
                if (headerMap.TryGetValue(key, out var idx) == false)
                {
                    throw new InvalidOperationException($"缺少列 {key}");
                }

                return columns[idx].Trim();
            }

            if (!string.Equals(Value("format_version"), FormatVersion, StringComparison.OrdinalIgnoreCase))
            {
                throw new OpenAIAccountImportException(OpenAIAccountImportError.UnsupportedFormatVersion, $"第 {rowNo} 行 format_version 不是 {FormatVersion}");
            }

            var email = Value("email");
            var accountId = Value("account_id");
            var accessToken = Value("access_token");
            var refreshToken = Value("refresh_token");
            var idToken = Value("id_token");

            if (string.IsNullOrWhiteSpace(accessToken)
                || string.IsNullOrWhiteSpace(refreshToken)
                || string.IsNullOrWhiteSpace(idToken))
            {
                throw new OpenAIAccountImportException(OpenAIAccountImportError.MissingRequiredValue, $"第 {rowNo} 行 token 字段缺失");
            }

            var tokenMetadata = ParseTokenMetadata(accessToken, refreshToken, idToken);
            var parsedAccountId = NormalizeClaimedAccountId(accountId, tokenMetadata.AccountId, tokenMetadata.OpenAIAccountId);

            if (string.IsNullOrWhiteSpace(accountId))
            {
                accountId = parsedAccountId ?? string.Empty;
            }
            else if (accountId != parsedAccountId && accountId != tokenMetadata.OpenAIAccountId)
            {
                throw new OpenAIAccountImportException(OpenAIAccountImportError.AccountIdMismatch, $"第 {rowNo} 行 account_id 与 token 中解析值不一致");
            }

            if (string.IsNullOrWhiteSpace(email))
            {
                email = tokenMetadata.Email ?? string.Empty;
            }

            if (string.IsNullOrWhiteSpace(accountId))
            {
                throw new OpenAIAccountImportException(OpenAIAccountImportError.InvalidAccount, $"第 {rowNo} 行无法解析 account_id");
            }

            if (seen.Add(accountId) == false)
            {
                throw new OpenAIAccountImportException(OpenAIAccountImportError.DuplicateAccountId, $"第 {rowNo} 行 account_id 重复: {accountId}");
            }

            var isActive = ParseActive(Value("is_active"), rowNo);
            if (isActive)
            {
                if (string.IsNullOrWhiteSpace(activeAccountId) == false)
                {
                    throw new OpenAIAccountImportException(OpenAIAccountImportError.MultipleActiveAccounts, "文件包含多个 is_active=true");
                }

                activeAccountId = accountId;
            }

            accounts.Add(new TokenAccount
            {
                Email = email,
                AccountId = accountId,
                OpenAIAccountId = tokenMetadata.OpenAIAccountId ?? accountId,
                AccessToken = accessToken,
                RefreshToken = refreshToken,
                IdToken = idToken,
                ExpiresAt = tokenMetadata.ExpiresAt,
                PlanType = tokenMetadata.PlanType ?? "free",
                OAuthClientId = tokenMetadata.OAuthClientId,
                IsActive = false,
            });
        }

        if (accounts.Count == 0)
        {
            throw new OpenAIAccountImportException(OpenAIAccountImportError.EmptyFile, "未发现可导入账号");
        }

        return new ParsedOpenAIAccountCSV(
            accounts,
            activeAccountId,
            accounts.Count,
            new OAuthAccountImportInterchangeContext()
        );
    }

    private static List<string> SplitCsvLine(string line)
    {
        var fields = new List<string>();
        var current = new StringBuilder();
        var inQuotes = false;

        for (var i = 0; i < line.Length; i += 1)
        {
            var ch = line[i];

            if (inQuotes)
            {
                if (ch == '"')
                {
                    if (i + 1 < line.Length && line[i + 1] == '"')
                    {
                        current.Append('"');
                        i += 1;
                    }
                    else
                    {
                        inQuotes = false;
                    }
                }
                else
                {
                    current.Append(ch);
                }

                continue;
            }

            if (ch == '"')
            {
                if (current.Length > 0)
                {
                    throw new OpenAIAccountImportException(OpenAIAccountImportError.InvalidCSV, "CSV 格式非法：引号错误");
                }

                inQuotes = true;
                continue;
            }

            if (ch == ',')
            {
                fields.Add(current.ToString());
                current.Clear();
                continue;
            }

            current.Append(ch);
        }

        if (inQuotes)
        {
            throw new OpenAIAccountImportException(OpenAIAccountImportError.InvalidCSV, "CSV 格式非法：未闭合的引号");
        }

        fields.Add(current.ToString());
        return fields;
    }

    private static bool ParseActive(string value, int rowNo)
    {
        if (string.Equals(value, "true", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (string.Equals(value, "false", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        throw new OpenAIAccountImportException(OpenAIAccountImportError.InvalidActiveValue, $"第 {rowNo} 行 is_active 值非法");
    }

    private static string? ParseJsonString(JsonNode? node)
    {
        var value = GetNodeText(node);
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        return value;
    }

    private static string? GetNodeText(JsonNode? node)
    {
        if (node is null)
        {
            return null;
        }

        if (node is not JsonValue value)
        {
            return null;
        }

        var raw = value.GetValue<object>();
        if (raw is null)
        {
            return null;
        }

        if (raw is string text)
        {
            return GetTrimmedString(text);
        }

        if (raw is bool boolValue)
        {
            return boolValue.ToString().ToLowerInvariant();
        }

        if (raw is int intValue)
        {
            return intValue.ToString(CultureInfo.InvariantCulture);
        }

        if (raw is long longValue)
        {
            return longValue.ToString(CultureInfo.InvariantCulture);
        }

        if (raw is double doubleValue)
        {
            return doubleValue.ToString(CultureInfo.InvariantCulture);
        }

        if (raw is decimal decimalValue)
        {
            return decimalValue.ToString(CultureInfo.InvariantCulture);
        }

        return value.TryGetValue<string>(out var fallback)
            ? GetTrimmedString(fallback)
            : null;
    }

    private static int? ParseInt(JsonNode? node)
    {
        if (ParseJsonString(node) is not { } value)
        {
            return null;
        }

        if (long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var longValue)
            && longValue is >= int.MinValue and <= int.MaxValue)
        {
            return (int)longValue;
        }

        if (double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var doubleValue)
            && doubleValue is >= int.MinValue and <= int.MaxValue)
        {
            return (int)doubleValue;
        }

        return null;
    }

    private static double? ParseDouble(JsonNode? node)
    {
        return ParseJsonString(node) is { } value && double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var doubleValue)
            ? doubleValue
            : null;
    }

    private static bool? ParseBool(JsonNode? node)
    {
        if (node is JsonValue boolNode && boolNode.TryGetValue<bool>(out var directBool))
        {
            return directBool;
        }

        if (ParseJsonString(node) is { } value && bool.TryParse(value, out var valueBool))
        {
            return valueBool;
        }

        return null;
    }

    private static DateTimeOffset? ParseUnixSeconds(JsonNode? node)
    {
        var numeric = ParseJsonString(node);
        if (string.IsNullOrWhiteSpace(numeric))
        {
            return null;
        }

        try
        {
            if (long.TryParse(numeric, NumberStyles.Integer, CultureInfo.InvariantCulture, out var seconds))
            {
                return DateTimeOffset.FromUnixTimeSeconds(seconds);
            }

            if (double.TryParse(numeric, NumberStyles.Float, CultureInfo.InvariantCulture, out var secondsFloat))
            {
                return DateTimeOffset.FromUnixTimeSeconds((long)secondsFloat);
            }

            return null;
        }
        catch
        {
            return null;
        }
    }

    private static ParsedTokenMetadata ParseTokenMetadata(string accessToken, string refreshToken, string idToken)
    {
        _ = refreshToken;
        var accessClaims = ParseJwtPayload(accessToken);
        var idClaims = ParseJwtPayload(idToken);
        var accessAuthClaims = accessClaims?["https://api.openai.com/auth"] as JsonObject;
        var idAuthClaims = idClaims?["https://api.openai.com/auth"] as JsonObject;
        var authClaims = accessAuthClaims ?? idAuthClaims;

        var accountId = ResolveAccountId(authClaims);
        var openAIAccountId = ResolveOpenAIAccountId(authClaims);
        var planType = ResolveClaim(accessAuthClaims?["chatgpt_plan_type"]) ?? "free";
        var clientId = ResolveClaim(accessClaims?["client_id"]);
        var email = ResolveEmail(idClaims, idAuthClaims);
        var expiresAt = ResolveExpiresAt(accessClaims, idClaims, idAuthClaims);
        var chatGPTUserId = ResolveChatGPTUserId(accessClaims, idClaims, accessAuthClaims, idAuthClaims);

        return new ParsedTokenMetadata
        {
            AccountId = accountId,
            OpenAIAccountId = openAIAccountId ?? accountId,
            Email = email,
            OAuthClientId = clientId,
            PlanType = planType,
            ExpiresAt = expiresAt,
            ChatGPTUserId = chatGPTUserId,
        };
    }

    private static string? ResolveChatGPTUserId(
        JsonObject? accessClaims,
        JsonObject? idClaims,
        JsonObject? accessAuthClaims,
        JsonObject? idAuthClaims
    )
    {
        var fromAccessAuth = ResolveClaim(accessAuthClaims?["chatgpt_user_id"]);
        var fromIdAuth = ResolveClaim(idAuthClaims?["chatgpt_user_id"]);
        if (string.IsNullOrWhiteSpace(fromAccessAuth) == false)
        {
            return fromAccessAuth;
        }

        if (string.IsNullOrWhiteSpace(fromIdAuth) == false)
        {
            return fromIdAuth;
        }

        var fromAccessClaims = ResolveClaim(accessClaims?["chatgpt_user_id"]) ?? ResolveClaim(accessClaims?["user_id"]);
        var fromIdClaims = ResolveClaim(idClaims?["chatgpt_user_id"]) ?? ResolveClaim(idClaims?["user_id"]);
        if (string.IsNullOrWhiteSpace(fromAccessClaims) == false)
        {
            return fromAccessClaims;
        }

        return fromIdClaims;
    }

    private static string? ResolveAccountId(JsonObject? claims)
    {
        if (claims is null)
        {
            return null;
        }

        var accountUserId = ResolveClaim(claims["chatgpt_account_user_id"]);
        if (string.IsNullOrWhiteSpace(accountUserId))
        {
            var remoteAccountId = ResolveClaim(claims["chatgpt_account_id"]);
            var userId = ResolveClaim(claims["chatgpt_user_id"]) ?? ResolveClaim(claims["user_id"]);
            if (string.IsNullOrWhiteSpace(remoteAccountId) == false
                && string.IsNullOrWhiteSpace(userId) == false)
            {
                return $"{userId}__{remoteAccountId}";
            }

            return remoteAccountId ?? userId;
        }

        return accountUserId;
    }

    private static string? ResolveOpenAIAccountId(JsonObject? claims)
    {
        return ResolveClaim(claims?["chatgpt_account_id"]) ?? ResolveAccountId(claims);
    }

    private static string? ResolveEmail(JsonObject? idClaims, JsonObject? idAuthClaims)
    {
        var email = ResolveClaim(idClaims?["email"]);
        return string.IsNullOrWhiteSpace(email)
            ? ResolveClaim(idAuthClaims?["email"])
            : email;
    }

    private static DateTimeOffset? ResolveExpiresAt(
        JsonObject? accessClaims,
        JsonObject? idClaims,
        JsonObject? idAuthClaims
    )
    {
        var accessExpiresAt = ParseUnixSeconds(accessClaims?["exp"]);
        var idExpiresAt = ParseUnixSeconds(idClaims?["exp"]);

        if (accessExpiresAt is null && idExpiresAt is null)
        {
            return ParseIsoDateTime(ResolveClaim(idAuthClaims?["chatgpt_subscription_active_until"]));
        }

        if (accessExpiresAt is null)
        {
            return idExpiresAt;
        }

        if (idExpiresAt is null)
        {
            return accessExpiresAt;
        }

        return accessExpiresAt < idExpiresAt ? accessExpiresAt : idExpiresAt;
    }

    private static string? ResolveClaim(JsonNode? claim)
    {
        return GetNodeText(claim);
    }

    private static JsonObject? ParseJwtPayload(string token)
    {
        var parts = token.Split('.');
        if (parts.Length < 2)
        {
            return null;
        }

        try
        {
            var payloadBytes = Base64UrlDecode(parts[1]);
            var payloadText = Encoding.UTF8.GetString(payloadBytes);
            return JsonNode.Parse(payloadText) as JsonObject;
        }
        catch
        {
            return null;
        }
    }

    private static DateTimeOffset? ParseIsoDateTime(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        if (DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var dateTime))
        {
            return dateTime;
        }

        if (DateTimeOffset.TryParse(value, out dateTime))
        {
            return dateTime;
        }

        return null;
    }

    private static string NormalizeClaimedAccountId(string? declaredAccountId, string? accountId, string? openAIAccountId)
    {
        var localAccountId = GetTrimmedString(accountId);
        if (string.IsNullOrWhiteSpace(localAccountId))
        {
            return GetTrimmedString(openAIAccountId);
        }

        return localAccountId;
    }

    private sealed class ParsedTokenMetadata
    {
        public string? AccountId { get; init; }
        public string? OpenAIAccountId { get; init; }
        public string? Email { get; init; }
        public string? OAuthClientId { get; init; }
        public string? PlanType { get; init; }
        public DateTimeOffset? ExpiresAt { get; init; }
        public string? ChatGPTUserId { get; init; }
    }

    private static string Normalize(string text)
    {
        var normalized = text.Replace("\r\n", "\n").Replace("\r", "\n");
        if (normalized.Length > 0 && normalized[0] == '\uFEFF')
        {
            normalized = normalized.Substring(1);
        }

        return normalized;
    }

    private static string GetTrimmedString(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        return value.Trim();
    }

    private static byte[] Base64UrlDecode(string input)
    {
        var b64 = input.Replace('-', '+').Replace('_', '/');
        var remainder = b64.Length % 4;
        if (remainder > 0)
        {
            b64 = b64.PadRight(b64.Length + 4 - remainder, '=');
        }

        return Convert.FromBase64String(b64);
    }
}
