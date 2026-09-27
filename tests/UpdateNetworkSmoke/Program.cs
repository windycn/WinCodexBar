using CodexBarWin.Services;

var updater = new AppUpdateService();
var result = await updater.CheckAsync();
Console.WriteLine(result is null
    ? $"PASS: {AppUpdateService.DisplayVersion} has no newer stable release"
    : $"PASS: stable update {result.Version} is available for {AppUpdateService.ArchitectureName}");
