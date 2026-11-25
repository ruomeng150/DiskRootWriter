# 磁盘定时写入

Windows 托盘工具，按指定间隔在所选磁盘的根目录写入零字节文件。

## 使用方法

1. 从 [Releases](../../releases/latest) 下载 `DiskRootWriter.exe`，放到固定位置后双击运行。
2. 选择目标磁盘，输入分钟和秒，点击 **开始写入**。首次写入立即执行，之后按设定间隔重复。
3. 如需写入后自动删除，勾选 **写入后删除文件**，填写等待秒数，然后点击 **保存设置**。等待时间默认 3 秒，可设为 1 至 3600 秒。
4. 如需登录 Windows 后自动在后台写入，勾选 **开机自启**，然后点击 **保存设置**。

关闭窗口会将程序留在系统托盘。双击托盘图标可重新打开设置，右键可暂停或退出。退出时会清理仍在等待删除的临时文件。

关闭自动删除时，程序会重复更新根目录里的 `DiskRootWriter.empty`，遇到同名的非空文件会报错并保留其内容。开启自动删除时，每次写入使用独立的零字节文件，可以各自等待删除。写入需要当前 Windows 用户对目标磁盘根目录有权限。

## 从源码构建

在 Windows PowerShell 中运行下方命令。需要系统提供的 .NET Framework C# 编译器。构建结果为当前目录下的 `DiskRootWriter.exe`。

```powershell
$compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework\v4.0.30319\csc.exe'
& $compiler /nologo /target:winexe /platform:anycpu /optimize+ /codepage:65001 /out:DiskRootWriter.exe DiskRootWriter.cs
```
