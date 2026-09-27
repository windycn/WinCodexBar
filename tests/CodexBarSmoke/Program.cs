using CodexBarWin.Interop;
using CodexBarWin.Services;

if (args.Length != 2)
{
    Console.Error.WriteLine("Usage: CodexBarSmoke <account-export.json> <isolated-home>");
    return 2;
}

var exportPath = Path.GetFullPath(args[0]);
var isolatedHome = Path.GetFullPath(args[1]);
if (!File.Exists(exportPath))
{
    Console.Error.WriteLine("Account export file was not found.");
    return 2;
}

Directory.CreateDirectory(isolatedHome);
Environment.SetEnvironmentVariable("CODEXBAR_HOME", isolatedHome);
var imported = OpenAIAccountCSVService.Parse(await File.ReadAllTextAsync(exportPath));
var registry = new AccountRegistry();
registry.MergeImportedAccounts(imported.Accounts, imported.InteropContext);
registry.SetActive(imported.ActiveAccountId ?? registry.Accounts.FirstOrDefault()?.AccountId);
registry.Save();
Console.WriteLine($"Imported {registry.Accounts.Count} account(s) into the isolated test home. Credentials were not printed.");
return registry.Accounts.Count > 0 ? 0 : 1;
