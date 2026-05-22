
#r "System.Runtime.InteropServices"
#r "System.Drawing.Common"

using System;
using System.Runtime.InteropServices;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;

Console.WriteLine("开始测试 UWP 应用图标获取...");

// 先试一下 Shell.Application 的方法
var shellType = Type.GetTypeFromProgID("Shell.Application");
if (shellType == null)
{
    Console.WriteLine("Shell.Application 不可用");
}
else
{
    Console.WriteLine("Shell.Application 可用，尝试获取图标...");
    dynamic shell = Activator.CreateInstance(shellType);
    dynamic appsFolder = shell.NameSpace("shell:AppsFolder");
    
    // 测试一个已知的 AUMID
    string testAumid = "Microsoft.WindowsCalculator_8wekyb3d8bbwe!App";
    Console.WriteLine($"测试 AUMID: {testAumid}");
    
    try
    {
        dynamic item = appsFolder.ParseName(testAumid);
        if (item == null)
        {
            Console.WriteLine("ParseName 返回 null");
        }
        else
        {
            Console.WriteLine($"获取到项目: {item.Name}");
            
            // 尝试获取图标
            // 方法1: 看看 item 有没有直接的方法
            // 方法2: 使用 IShellItemImageFactory
        }
    }
    catch (Exception ex)
    {
        Console.WriteLine($"Shell.Application 方法出错: {ex.Message}");
    }
}

Console.WriteLine("\n测试完成！");
