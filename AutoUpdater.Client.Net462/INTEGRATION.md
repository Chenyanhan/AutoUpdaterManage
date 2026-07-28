# 卷绕机 WinForms 集成说明

## 兼容范围

- 目标框架：.NET Framework 4.6.2
- 可被 .NET Framework 4.6.2、4.7.2 和更高 4.x 项目引用
- 管理端 UDP 协议：V1，与 `AutoUpdater.Protocol` 保持兼容

## 项目引用

在卷绕机项目中引用：

```xml
<ProjectReference Include="..\AutoUpdater.Client.Net462\AutoUpdater.Client.Net462.csproj" />
```

也可以引用发布目录中的 `AutoUpdater.Client.Net462.dll` 及其依赖。

## 启动

建议在主窗体 `Load` 中创建一次客户端：

```csharp
private AutoUpdater.Client.Net462.EmbeddedUpdateClient _updateClient;

private void MainForm_Load(object sender, EventArgs e)
{
    _updateClient = new AutoUpdater.Client.Net462.EmbeddedUpdateClient(
        new AutoUpdater.Client.Net462.EmbeddedClientOptions
        {
            DeviceId = "WINDER-" + Environment.MachineName,
            DeviceName = Environment.MachineName,
            CurrentVersion = Application.ProductVersion,
            Port = 45678,
            InstallationDirectory = AppDomain.CurrentDomain.BaseDirectory,
            RestartExecutablePath = "卷绕机.exe",
            DatabaseConfigFileName = "卷绕机.exe.config"
        });

    _updateClient.ShutdownRequested += () => BeginInvoke(
        new Action(Close));
    _updateClient.Error += exception => WriteLog(exception.ToString());
    _updateClient.Start();
}
```

没有注册确认事件时，客户端使用 WinForms `MessageBox` 询问更新和回退。

## 退出

```csharp
private void MainForm_FormClosed(object sender, FormClosedEventArgs e)
{
    if (_updateClient == null) return;
    _updateClient.Dispose();
    _updateClient = null;
}
```

## 文件结构

```text
卷绕机.exe
卷绕机.exe.config
AutoUpdater.Client.Net462.dll
Newtonsoft.Json.dll
MySqlConnector.dll
其他由 NuGet 复制的依赖

AutoUpdater\
└─ AutoUpdater.Updater.exe
```

发布时应复制 `AutoUpdater.Client.Net462\bin\Release\net462` 中的依赖，
不要只复制主 DLL。

## 数据库配置

默认读取：

```text
卷绕机.exe.config
```

支持标准 `connectionStrings/add connectionString` 结构。数据库同步只允许
`data_result` 和 `plc_user_manage`，同步包最大 100 MB。
