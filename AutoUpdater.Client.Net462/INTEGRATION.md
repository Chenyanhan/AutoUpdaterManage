# Windows 上位机自动更新集成指南

本文说明如何把自动更新客户端集成到 WinForms 或 WPF 上位机中。示例程序名称为“卷绕机.exe”。

## 1. 适用范围

- 上位机 UI：WinForms 或 WPF
- 运行环境：.NET Framework 4.6.2–4.8，或者 .NET 8 Windows
- 设备通信：UDP V1 协议
- 默认设备监听端口：`45678`
- 独立更新程序：`AutoUpdater\AutoUpdater.Updater.exe`

### 1.1 客户端 DLL 选择

引用哪个 DLL 主要取决于上位机的目标框架，不是只看 WinForms 或 WPF：

| 上位机类型 | 目标框架 | 使用的客户端 |
| --- | --- | --- |
| WinForms | .NET Framework 4.6.2–4.8 | `AutoUpdater.Client.Net462.dll` |
| WPF | .NET Framework 4.6.2–4.8 | `AutoUpdater.Client.Net462.dll` |
| WinForms | .NET 8 Windows | `AutoUpdater.Client.dll` |
| WPF | .NET 8 Windows | `AutoUpdater.Client.dll` |

需要注意：

- `AutoUpdater.Client.Net462.dll` 内置的默认确认窗口由原生 WinForms 实现。它可以在 .NET Framework WPF 程序中运行，但默认窗口仍是 WinForms 窗口。
- WPF 上位机如果需要完全一致的 WPF 风格，可以注册更新和回退决策事件，由 WPF 主程序显示自己的窗口。
- `AutoUpdater.Client.dll` 目标框架是 `net8.0-windows`，不能被 .NET Framework 项目引用。
- `AutoUpdater.Client.Net462.dll` 面向传统 .NET Framework，不应作为 .NET 8 项目的首选客户端。

本文后续代码以现场使用的 `.NET Framework 4.6.2 WinForms` 和 `AutoUpdater.Client.Net462.dll` 为主。WPF 的客户端配置项相同，但启动、退出和 UI 调度代码需要使用对应框架的写法。

客户端随“卷绕机.exe”启动并监听管理端指令。用户确认立即更新或回退后，客户端先启动独立更新程序，再通知上位机正常退出。Updater 等待上位机完全退出后执行备份、安装或回退，最后重新启动“卷绕机.exe”。

## 2. 添加客户端引用

### 2.1 .NET Framework 4.6.2–4.8

WinForms 和 WPF 均引用：

```text
AutoUpdater.Client.Net462.dll
```

如果上位机项目和本仓库位于同一个解决方案，可以添加项目引用：

```xml
<ProjectReference Include="..\AutoUpdater.Client.Net462\AutoUpdater.Client.Net462.csproj" />
```

现场项目通常直接引用编译后的 DLL：

```text
AutoUpdater.Client.Net462.dll
```

在 Visual Studio 中：

1. 右键单击上位机项目的“引用”。
2. 选择“添加引用”。
3. 选择“浏览”。
4. 选择 `AutoUpdater.Client.Net462.dll`。
5. 确认该引用的“复制本地”属性为 `True`。

### 2.2 .NET 8 Windows

WinForms 和 WPF 均引用：

```text
AutoUpdater.Client.dll
```

项目引用写法：

```xml
<ProjectReference Include="..\AutoUpdater.Client\AutoUpdater.Client.csproj" />
```

`.NET 8` 客户端使用异步启动方法 `StartAsync()`，其构造参数和事件签名也与 Net462 版本略有区别，不能直接照抄本文后面的 Net462 启动代码。

## 3. 部署文件

下面是 `.NET Framework 4.6.2–4.8` 的部署结构。不能只复制 `AutoUpdater.Client.Net462.dll`，请把客户端发布目录中的运行依赖一起复制到“卷绕机.exe”目录。

```text
卷绕机.exe
卷绕机.exe.config
AutoUpdater.Client.Net462.dll
Microsoft.Bcl.AsyncInterfaces.dll
Microsoft.Extensions.DependencyInjection.Abstractions.dll
Microsoft.Extensions.Logging.Abstractions.dll
MySqlConnector.dll
Newtonsoft.Json.dll
System.Buffers.dll
System.Diagnostics.DiagnosticSource.dll
System.Memory.dll
System.Numerics.Vectors.dll
System.Runtime.CompilerServices.Unsafe.dll
System.Threading.Tasks.Extensions.dll

AutoUpdater\
└── AutoUpdater.Updater.exe
```

`.pdb` 文件只用于调试，现场运行时可以不发布。

必须保证 Updater 位于：

```text
上位机运行目录\AutoUpdater\AutoUpdater.Updater.exe
```

## 4. 在主窗体中添加字段

在主窗体代码文件顶部添加：

```csharp
using AutoUpdater.Client.Net462;
```

在主窗体类中添加两个字段：

```csharp
private EmbeddedUpdateClient _updateClient;
private bool _closingForUpdate;
```

`_updateClient` 必须是窗体字段，不能定义成局部变量，否则可能被回收，也无法在退出时正确释放。

## 5. 启动自动更新客户端

在主窗体 `Load` 事件中启动：

```csharp
private void MainForm_Load(object sender, EventArgs e)
{
    StartAutoUpdaterAgent();
}
```

添加启动方法：

```csharp
private void StartAutoUpdaterAgent()
{
    if (_updateClient != null)
        return;

    _updateClient = new EmbeddedUpdateClient(
        new EmbeddedClientOptions
        {
            // 每台设备必须唯一。
            DeviceId = "WINDER-" + Environment.MachineName,

            // 显示在管理端设备列表中的名称。
            DeviceName = Environment.MachineName,

            // 当前上位机版本。
            CurrentVersion = Application.ProductVersion,

            // 设备端监听管理指令的 UDP 端口。
            Port = 45678,

            // 卷绕机.exe 所在目录。
            InstallationDirectory =
                AppDomain.CurrentDomain.BaseDirectory,

            // 更新或回退完成后重新启动的程序。
            RestartExecutablePath = "卷绕机.exe",

            // 上位机数据库连接字符串所在的配置文件。
            DatabaseConfigFileName = "卷绕机.exe.config",

            // 可选：直接传入数据库连接字符串。
            // 留空或不设置时，客户端会从上面的
            // DatabaseConfigFileName 指定的配置文件中自动读取。
            // 只有需要覆盖配置文件连接串时才直接赋值。
            DatabaseConnectionString = null
        });

    _updateClient.ShutdownRequested +=
        OnUpdateShutdownRequested;
    _updateClient.Error += OnAutoUpdaterError;
    _updateClient.Start();
}
```

数据库连接字符串的使用规则：

1. `DatabaseConnectionString` 有内容时，客户端直接使用它，`DatabaseConfigFileName` 不再参与本次数据库连接。
2. `DatabaseConnectionString` 为 `null`、空字符串或没有设置时，客户端读取：

   ```text
   InstallationDirectory\DatabaseConfigFileName
   ```

   上面的示例最终读取：

   ```text
   卷绕机.exe 所在目录\卷绕机.exe.config
   ```

3. 推荐正式上位机不设置 `DatabaseConnectionString`，把账号密码保存在 `卷绕机.exe.config` 中。
4. 只有临时测试、配置由其他系统动态生成，或者需要强制覆盖配置文件连接串时，才建议直接设置 `DatabaseConnectionString`。
5. 连接字符串只在设备收到数据库同步任务时使用。设备检索、软件更新和版本回退不依赖数据库连接字符串。

需要直接传入时，把示例中的 `DatabaseConnectionString = null` 替换为：

```csharp
DatabaseConnectionString =
    "Server=127.0.0.1;Port=3306;" +
    "Database=leadchina_project;" +
    "User ID=root;Password=root;SSL Mode=None;"
```

正式项目不建议在源代码中明文保存数据库密码。

客户端应当在 WinForms UI 线程中创建。建议直接在主窗体 `Load` 事件中创建，这样更新确认窗口可以正确居中、置顶并获得前台焦点。

不要在定时器中重复调用 `StartAutoUpdaterAgent()`。同一个进程只需要一个客户端实例。

### 5.1 .NET Framework WPF 启动示例

`.NET Framework 4.6.2–4.8 WPF` 同样引用 `AutoUpdater.Client.Net462.dll`，可以在主窗口 `Loaded` 事件中创建客户端：

```csharp
using System;
using System.Diagnostics;
using System.Reflection;
using System.Windows;
using AutoUpdater.Client.Net462;

private EmbeddedUpdateClient _updateClient;

private void MainWindow_Loaded(
    object sender,
    RoutedEventArgs e)
{
    string executablePath =
        Assembly.GetEntryAssembly().Location;
    string currentVersion =
        FileVersionInfo.GetVersionInfo(executablePath)
            .FileVersion;

    _updateClient = new EmbeddedUpdateClient(
        new EmbeddedClientOptions
        {
            DeviceId = "WINDER-" + Environment.MachineName,
            DeviceName = Environment.MachineName,
            CurrentVersion = currentVersion,
            Port = 45678,
            InstallationDirectory =
                AppDomain.CurrentDomain.BaseDirectory,
            RestartExecutablePath = "卷绕机.exe",
            DatabaseConfigFileName = "卷绕机.exe.config"
        });

    _updateClient.ShutdownRequested += delegate
    {
        Application.Current.Dispatcher.BeginInvoke(
            new Action(delegate
            {
                Application.Current.Shutdown();
            }));
    };
    _updateClient.Error += delegate(Exception exception)
    {
        // TODO：写入上位机日志。
    };
    _updateClient.Start();
}
```

在 WPF 主窗口关闭时释放：

```csharp
private void MainWindow_Closed(
    object sender,
    EventArgs e)
{
    if (_updateClient == null)
        return;

    _updateClient.Dispose();
    _updateClient = null;
}
```

这个示例会继续使用 Net462 客户端内置的原生确认窗口。如果要使用上位机自己的 WPF 确认窗口，可以注册 `UpdateDecisionRequired` 和 `RollbackDecisionRequired`。

## 6. 使用内置更新确认窗口

默认情况下，不需要注册：

```csharp
UpdateDecisionRequired
RollbackDecisionRequired
```

未注册这两个事件时，客户端会显示内置确认窗口：

- 更新：“立即更新”和“稍后更新”
- 回退：“立即回退”和“稍后处理”

测试环境变量 `AUTOUPDATER_TEST_DECISION` 只供测试宿主自动化使用。正式上位机不应设置该变量。

## 7. 收到立即更新后的退出处理

用户点击“立即更新”或“立即回退”后，客户端会先启动 Updater，然后触发 `ShutdownRequested`。

在主窗体中添加：

```csharp
private void OnUpdateShutdownRequested()
{
    if (InvokeRequired)
    {
        BeginInvoke(new Action(OnUpdateShutdownRequested));
        return;
    }

    _closingForUpdate = true;

    // 根据上位机实际情况关闭业务资源。
    StopPlcCommunication();
    StopBackgroundTasks();
    CloseDatabaseConnections();

    // 关闭主窗体，使卷绕机.exe 完全退出。
    Close();
}
```

上面三个业务清理方法是示例：

```csharp
StopPlcCommunication();
StopBackgroundTasks();
CloseDatabaseConnections();
```

请替换为上位机已有的停止 PLC 通信、停止工作线程、关闭串口、关闭数据库连接等方法。如果目前没有对应逻辑，可以暂时删除。

不要在 `OnUpdateShutdownRequested` 中再次启动 Updater。客户端触发这个事件前已经启动了独立更新程序。

## 8. 避免退出确认阻挡更新

如果上位机关闭时原本会弹出“是否退出”，更新退出时必须跳过，否则 Updater 会一直等待上位机退出并最终超时。

```csharp
private void MainForm_FormClosing(
    object sender,
    FormClosingEventArgs e)
{
    if (_closingForUpdate)
        return;

    DialogResult result = MessageBox.Show(
        "确定退出卷绕机程序吗？",
        "退出确认",
        MessageBoxButtons.YesNo,
        MessageBoxIcon.Question);

    if (result != DialogResult.Yes)
        e.Cancel = true;
}
```

如果上位机已有 `FormClosing` 事件，不要再创建第二套退出逻辑。只需把：

```csharp
if (_closingForUpdate)
    return;
```

放到现有退出确认逻辑的最前面。

## 9. 释放客户端

在主窗体 `FormClosed` 事件中释放客户端：

```csharp
private void MainForm_FormClosed(
    object sender,
    FormClosedEventArgs e)
{
    if (_updateClient == null)
        return;

    _updateClient.ShutdownRequested -=
        OnUpdateShutdownRequested;
    _updateClient.Error -= OnAutoUpdaterError;

    _updateClient.Dispose();
    _updateClient = null;
}
```

## 10. 记录客户端异常

添加异常处理：

```csharp
private void OnAutoUpdaterError(Exception exception)
{
    // 替换为上位机现有的日志方法。
    WriteLog(
        "自动更新客户端异常：" +
        exception);
}
```

不建议在这里直接弹出 MessageBox。UDP 或网络异常可能连续发生，反复弹框会影响上位机操作。

## 11. 检查窗体事件绑定

确保主窗体已经绑定以下事件：

```csharp
this.Load += MainForm_Load;
this.FormClosing += MainForm_FormClosing;
this.FormClosed += MainForm_FormClosed;
```

如果这些事件已经在 WinForms 设计器中绑定，不要在构造函数中重复绑定。

## 12. 数据库配置

客户端支持两种数据库连接字符串传入方式。

### 12.1 方式一：代码直接传入

在创建客户端时设置 `DatabaseConnectionString`：

```csharp
_updateClient = new EmbeddedUpdateClient(
    new EmbeddedClientOptions
    {
        DeviceId = "WINDER-001",
        DeviceName = Environment.MachineName,
        CurrentVersion = Application.ProductVersion,
        Port = 45678,
        InstallationDirectory =
            AppDomain.CurrentDomain.BaseDirectory,
        RestartExecutablePath = "卷绕机.exe",

        DatabaseConnectionString =
            "Server=127.0.0.1;Port=3306;" +
            "Database=leadchina_project;" +
            "User ID=root;Password=root;SSL Mode=None;"
    });
```

只要 `DatabaseConnectionString` 不是空字符串，数据库同步就会直接使用它，不再读取配置文件。

这种方式最直观，但不建议在正式项目源代码中明文保存数据库密码。

### 12.2 方式二：从上位机配置文件自动读取（推荐）

创建客户端时不设置 `DatabaseConnectionString`，只指定配置文件：

```csharp
InstallationDirectory =
    AppDomain.CurrentDomain.BaseDirectory,
DatabaseConfigFileName = "卷绕机.exe.config"
```

最终读取路径为：

```text
上位机运行目录\卷绕机.exe.config
```

配置文件支持标准的 `connectionStrings`：

```xml
<?xml version="1.0" encoding="utf-8" ?>
<configuration>
  <connectionStrings>
    <add name="DefaultConnection"
         connectionString="Server=127.0.0.1;Port=3306;Database=leadchina_project;User ID=root;Password=root;SSL Mode=None;"
         providerName="MySqlConnector" />
  </connectionStrings>
</configuration>
```

当前读取器不是通过 `name="DefaultConnection"` 精确定位，而是：

1. 打开 `DatabaseConfigFileName` 指定的配置文件。
2. 扫描配置文件中的所有 `<add>` 节点。
3. 依次读取节点的 `connectionString` 和 `value` 属性。
4. 尝试使用 `MySqlConnectionStringBuilder` 解析每个值。
5. 返回第一个同时包含 `Server`、`Database` 和 `User ID` 的有效 MySQL 连接字符串。

例如下面的 `DeviceId` 会被跳过，数据库连接字符串会被选中：

```xml
<configuration>
  <appSettings>
    <add key="DeviceId"
         value="WINDER-001" />
  </appSettings>

  <connectionStrings>
    <add name="LeadChinaDatabase"
         connectionString="Server=127.0.0.1;Port=3306;Database=leadchina_project;User ID=root;Password=root;SSL Mode=None;"
         providerName="MySqlConnector" />
  </connectionStrings>
</configuration>
```

如果配置文件中有多个有效的 MySQL 连接字符串，当前实现使用扫描到的第一个。因此，建议上位机配置文件只保留一个符合上述条件的业务数据库连接字符串。

### 12.3 两种方式的优先级

```text
DatabaseConnectionString 有内容
        ↓
直接使用代码传入的连接字符串

DatabaseConnectionString 为空
        ↓
读取 InstallationDirectory
    + DatabaseConfigFileName
```

连接字符串是在设备实际收到数据库同步任务时读取的，不是在客户端启动时读取。因此，配置错误不会影响设备检索和软件更新，但会导致数据库同步任务失败并返回错误信息。

数据库同步目前只允许以下表：

- `data_result`
- `plc_user_manage`

数据库同步包最大为 100 MB。

## 13. 防火墙设置

现场设备需要允许设备端监听端口：

```text
协议：UDP
方向：入站
端口：45678
```

所有设备可以使用相同端口，因为每台设备拥有不同 IP。

如果现场网络有多个网卡，管理端广播地址应选择与设备网段对应的地址。

## 14. DeviceId 设计建议

`DeviceId` 必须稳定且唯一。简单方案：

```csharp
DeviceId = "WINDER-" + Environment.MachineName
```

如果现场可能修改电脑名称，建议读取设备编号配置：

```csharp
DeviceId = ConfigurationManager.AppSettings["DeviceId"];
```

配置示例：

```xml
<appSettings>
  <add key="DeviceId" value="WINDER-001" />
</appSettings>
```

不要使用随机 GUID 作为每次启动的 DeviceId，否则管理端会把同一台设备识别成多台设备。

## 15. 兼容其他上位机程序

这套客户端不只支持“卷绕机.exe”。程序名称、设备名称、配置文件名称和安装目录都可以通过 `EmbeddedClientOptions` 设置。

例如另一个上位机叫“分切机.exe”：

```csharp
_updateClient = new EmbeddedUpdateClient(
    new EmbeddedClientOptions
    {
        DeviceId = "SLITTER-" + Environment.MachineName,
        DeviceName = "分切机-" + Environment.MachineName,
        CurrentVersion = Application.ProductVersion,
        Port = 45678,
        InstallationDirectory =
            AppDomain.CurrentDomain.BaseDirectory,
        RestartExecutablePath = "分切机.exe",
        DatabaseConfigFileName = "分切机.exe.config"
    });
```

### 15.1 可以直接兼容的程序

- .NET Framework 4.6.2 WinForms
- .NET Framework 4.6.2 WPF
- .NET Framework 4.7、4.7.2、4.8 WinForms
- .NET Framework 4.7、4.7.2、4.8 WPF
- .NET 8 Windows WinForms（使用 `AutoUpdater.Client.dll`）
- .NET 8 Windows WPF（使用 `AutoUpdater.Client.dll`）
- 使用标准 EXE 启动和退出流程的上位机
- 主程序名称不是“卷绕机.exe”的程序
- 安装在不同目录的程序
- 使用同一套 UDP V1 管理协议的设备

### 15.2 可以兼容但需要适配的程序

- WPF：可以直接使用对应目标框架的客户端，但主窗口关闭应使用 WPF 的 `Application.Current.Shutdown()`，自定义确认界面也应由 WPF 主程序负责。
- 多进程上位机：Updater 默认等待传入的主进程退出；其他会锁定更新文件的进程也必须先关闭。
- 托盘程序：关闭主窗口不一定代表进程退出，需要显式调用应用程序退出逻辑。
- 有 PLC、串口、相机或长期工作线程的程序：收到 `ShutdownRequested` 后必须先安全停止这些资源。
- 多个 MySQL 数据库的程序：当前配置读取器选择第一个有效 MySQL 连接串，建议后续增加按连接名称选择。

### 15.3 不能直接使用 Net462 客户端的程序

- .NET 6、.NET 7、.NET 8 或更高版本程序：应使用 `AutoUpdater.Client`，不要引用 `AutoUpdater.Client.Net462`。
- 非 Windows 程序：当前独立 Updater 是 Windows x64 WPF 程序。
- 32 位且无法启动 x64 子进程的特殊环境：需要另外发布 `win-x86` Updater。
- 服务程序或无人值守程序：不适合直接使用桌面确认窗口，需要改为服务端决策或专用交互程序。

### 15.4 数据库同步兼容限制

软件更新和数据库同步是相互独立的。

其他上位机即使数据库结构不同，仍然可以使用设备检索、软件更新和版本回退。但是当前数据库同步只允许：

- `data_result`
- `plc_user_manage`

如果其他上位机使用不同数据库、不同表名或 SQL Server/SQLite，需要扩展数据库同步白名单和执行器。不能直接把现有 MySQL 同步逻辑用于不同数据库。

### 15.5 每个上位机需要修改的配置

至少确认以下选项：

```csharp
DeviceId
DeviceName
CurrentVersion
Port
InstallationDirectory
RestartExecutablePath
DatabaseConfigFileName
```

同一台电脑上如果同时运行多个上位机，每个程序必须使用不同的 `DeviceId` 和不同的 UDP 监听端口，否则会发生端口冲突或设备身份混淆。

## 16. CurrentVersion 说明

默认使用：

```csharp
CurrentVersion = Application.ProductVersion
```

请在上位机项目的程序集信息中维护版本：

```csharp
[assembly: AssemblyVersion("1.0.0.0")]
[assembly: AssemblyFileVersion("1.0.0.0")]
```

制作更新包时填写的版本应与新版“卷绕机.exe”的文件版本一致。

客户端会把当前版本传给 Updater，用于生成备份元数据。旧备份如果没有正确记录版本，Updater 会在回退后尝试从主 EXE 的文件版本识别。

## 17. 推荐的首次集成测试

### 17.1 启动检查

1. 启动“卷绕机.exe”。
2. 确认程序正常运行，没有缺少 DLL。
3. 在管理端输入正确广播地址。
4. 点击“检索设备”。
5. 确认设备名称、IP 和版本正确显示。

### 17.2 更新确认检查

1. 在管理端选择该设备。
2. 下发更新。
3. 确认更新窗口显示在最前面。
4. 点击“稍后更新”。
5. 确认上位机不会退出，管理端收到稍后处理回执。

### 17.3 完整更新检查

1. 先备份现场上位机目录。
2. 制作只包含一个测试文件的增量更新包。
3. 下发更新并点击“立即更新”。
4. 确认“卷绕机.exe”正常退出。
5. 确认 Updater 进度窗口出现。
6. 确认文件被替换。
7. 确认“卷绕机.exe”自动重新启动。
8. 确认管理端显示更新成功和正确版本。

### 17.4 回退检查

1. 在管理端下发版本回退。
2. 点击“立即回退”。
3. 确认上位机退出。
4. 确认备份文件被恢复。
5. 确认上位机重新启动。
6. 确认管理端显示回退后的实际版本，而不是 `unknown`。

## 18. 常见问题

### 检索不到设备

- 检查上位机是否调用了 `_updateClient.Start()`。
- 检查 UDP 45678 入站防火墙。
- 检查管理端广播地址。
- 检查设备与管理端是否位于同一网段。
- 检查端口是否被其他程序占用。

### 能检索但收不到更新

- 检查 `DeviceId` 是否与管理端目标设备一致。
- 检查客户端和管理端协议版本。
- 检查是否启动了多个客户端实例。
- 检查更新路径是否是设备可访问的共享路径。

### 提示找不到独立更新程序

确认文件存在：

```text
上位机运行目录\AutoUpdater\AutoUpdater.Updater.exe
```

### 点击立即更新后 Updater 一直等待

- 检查主窗体是否处理了 `ShutdownRequested`。
- 检查 `FormClosing` 是否取消了退出。
- 检查后台线程、托盘程序或隐藏窗体是否阻止进程结束。
- 检查 PLC、串口和数据库连接是否在退出时正确释放。

### 更新后没有重新启动

- 检查 `RestartExecutablePath` 是否为 `卷绕机.exe`。
- 检查更新包是否错误删除了主 EXE。
- 查看 `.autoupdater\logs` 中的更新日志。

## 19. 最小完整示例

```csharp
using System;
using System.Windows.Forms;
using AutoUpdater.Client.Net462;

public partial class MainForm : Form
{
    private EmbeddedUpdateClient _updateClient;
    private bool _closingForUpdate;

    public MainForm()
    {
        InitializeComponent();
    }

    private void MainForm_Load(object sender, EventArgs e)
    {
        _updateClient = new EmbeddedUpdateClient(
            new EmbeddedClientOptions
            {
                DeviceId = "WINDER-" + Environment.MachineName,
                DeviceName = Environment.MachineName,
                CurrentVersion = Application.ProductVersion,
                Port = 45678,
                InstallationDirectory =
                    AppDomain.CurrentDomain.BaseDirectory,
                RestartExecutablePath = "卷绕机.exe",
                DatabaseConfigFileName = "卷绕机.exe.config"
            });

        _updateClient.ShutdownRequested +=
            OnUpdateShutdownRequested;
        _updateClient.Error += OnAutoUpdaterError;
        _updateClient.Start();
    }

    private void OnUpdateShutdownRequested()
    {
        if (InvokeRequired)
        {
            BeginInvoke(new Action(OnUpdateShutdownRequested));
            return;
        }

        _closingForUpdate = true;
        Close();
    }

    private void OnAutoUpdaterError(Exception exception)
    {
        // TODO：写入上位机日志。
    }

    private void MainForm_FormClosing(
        object sender,
        FormClosingEventArgs e)
    {
        if (_closingForUpdate)
            return;

        if (MessageBox.Show(
                "确定退出卷绕机程序吗？",
                "退出确认",
                MessageBoxButtons.YesNo,
                MessageBoxIcon.Question) != DialogResult.Yes)
        {
            e.Cancel = true;
        }
    }

    private void MainForm_FormClosed(
        object sender,
        FormClosedEventArgs e)
    {
        if (_updateClient == null)
            return;

        _updateClient.Dispose();
        _updateClient = null;
    }
}
```
