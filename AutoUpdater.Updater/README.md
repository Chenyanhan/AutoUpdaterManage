# AutoUpdater.Updater

独立更新执行器。更新源是一个本地路径或 HTTP(S) JSON 清单，清单指向 ZIP 更新包。

## 发布

```powershell
dotnet publish .\AutoUpdater.Updater -c Release -r win-x64 --self-contained true
```

统一部署到上位机运行目录下：

```text
<上位机运行目录>\AutoUpdater\AutoUpdater.Updater.exe
```

## 更新包

ZIP 根目录应直接包含更新后的应用文件，不能额外套一层版本目录。生成 SHA-256：

```powershell
Get-FileHash .\application-2.0.0.zip -Algorithm SHA256
```

## 设计约束

- `AutoUpdater` 文件夹是受保护目录，不参与普通更新、备份覆盖或版本回退。
- 更新器自身升级需要使用单独的自升级流程。
- 上位机必须在启动更新器后退出，否则更新器将在 60 秒后报告失败。
- `.autoupdater` 保存备份、工作文件、安装版本和日志，不会被普通更新包覆盖。
- ZIP 解压会阻止目录穿越路径。
- SHA-256 只能验证完整性；正式发布还应增加数字签名验证以确认发布者身份。

## 端到端测试

在仓库根目录执行：

```powershell
.\scripts\Prepare-EndToEndTest.ps1
```

然后启动 `D:\UpdaterLab\App\AutoUpdater.Client.TestHost.exe`，管理端更新清单填写
`D:\UpdaterLab\Server\manifest.json`。测试宿主收到命令后会退出，更新器将其升级到
`2.0.0.0` 并重新启动。
