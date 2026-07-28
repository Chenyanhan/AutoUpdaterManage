using AutoUpdater.Client;

var installationDirectory = AppContext.BaseDirectory;
var versionFile = Path.Combine(installationDirectory, "version.txt");
var currentVersion = File.Exists(versionFile)
    ? File.ReadAllText(versionFile).Trim()
    : "1.0.0.0";
var deviceId = args.FirstOrDefault() ?? $"TEST-{Environment.MachineName}";
var updaterPath = args.Skip(1).FirstOrDefault()
                  ?? Environment.GetEnvironmentVariable("AUTOUPDATER_TEST_UPDATER")
                  ?? Path.Combine(
                      installationDirectory, "AutoUpdater", "AutoUpdater.Updater.exe");
var listenPort = args.Length > 2 && int.TryParse(args[2], out var parsedPort)
    ? parsedPort
    : EmbeddedUpdateClient.DefaultPort;
var restartExecutable = Path.GetFileName(Environment.ProcessPath);
var automatedDecision = Environment.GetEnvironmentVariable(
    "AUTOUPDATER_TEST_DECISION");
var databaseConnectionString = Environment.GetEnvironmentVariable(
    "AUTOUPDATER_TEST_DATABASE");

if (string.IsNullOrWhiteSpace(restartExecutable))
{
    Console.Error.WriteLine("无法确定测试宿主可执行文件名称。");
    return 2;
}

using var client = new EmbeddedUpdateClient(new EmbeddedClientOptions(
    deviceId,
    Environment.MachineName,
    currentVersion,
    updaterPath,
    listenPort,
    installationDirectory,
    restartExecutable,
    databaseConnectionString));

var shutdownRequested = new TaskCompletionSource(
    TaskCreationOptions.RunContinuationsAsynchronously);
var manualExitRequested = new TaskCompletionSource(
    TaskCreationOptions.RunContinuationsAsynchronously);

client.UpdateDecisionRequired += command =>
{
    Console.WriteLine($"收到更新：{command.UpdatePath}");
    Console.WriteLine("测试宿主自动接受更新。");
    return automatedDecision?.Equals(
        "postpone", StringComparison.OrdinalIgnoreCase) == true
        ? Task.FromResult(UpdateDecision.Postpone)
        : DesktopUpdatePrompt.ShowUpdateAsync(command, Environment.MachineName);
};
client.RollbackDecisionRequired += command =>
{
    Console.WriteLine($"收到回退：{command.TargetVersion ?? "最近备份"}");
    Console.WriteLine("测试宿主自动接受回退。");
    return automatedDecision?.Equals(
        "postpone", StringComparison.OrdinalIgnoreCase) == true
        ? Task.FromResult(UpdateDecision.Postpone)
        : DesktopUpdatePrompt.ShowRollbackAsync(command, Environment.MachineName);
};
client.ShutdownRequested += () =>
{
    Console.WriteLine("更新器已启动，测试宿主准备退出。");
    shutdownRequested.TrySetResult();
};
client.Error += ex => Console.WriteLine($"通信错误：{ex.Message}");

await client.StartAsync();
Console.WriteLine($"测试设备：{deviceId}");
Console.WriteLine($"当前版本：{currentVersion}");
Console.WriteLine($"安装目录：{installationDirectory}");
Console.WriteLine($"更新器：{updaterPath}");
Console.WriteLine(
    string.IsNullOrWhiteSpace(databaseConnectionString)
        ? "数据库同步：未配置（设置 AUTOUPDATER_TEST_DATABASE）"
        : "数据库同步：已配置MySQL连接");
Console.WriteLine($"正在监听 UDP {listenPort}。输入 exit 或按 Ctrl+C 可手动退出。");

Console.CancelKeyPress += (_, eventArgs) =>
{
    eventArgs.Cancel = true;
    manualExitRequested.TrySetResult();
};

if (!Console.IsInputRedirected)
{
    _ = Task.Run(() =>
    {
        while (true)
        {
            var line = Console.ReadLine();
            if (line is null) return;
            if (!string.Equals(line, "exit", StringComparison.OrdinalIgnoreCase)) continue;
            manualExitRequested.TrySetResult();
            return;
        }
    });
}

await Task.WhenAny(shutdownRequested.Task, manualExitRequested.Task);

// 接受响应已经发送；短暂等待底层 UDP 发送完成后释放监听器并退出。
await Task.Delay(200);
return 0;
