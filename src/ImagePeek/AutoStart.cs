using System;
using System.Reflection;
using Microsoft.Win32;

namespace ImagePeek
{
    /// <summary>开机自启动：HKCU Run 键（免管理员），带 --minimized 参数启动到托盘。</summary>
    public static class AutoStartManager
    {
        private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
        private const string ValueName = "ImagePeek";

        public static bool IsEnabled()
        {
            using (var k = Registry.CurrentUser.OpenSubKey(RunKey))
            {
                return k != null && k.GetValue(ValueName) != null;
            }
        }

        public static void Set(bool enabled)
        {
            using (var k = Registry.CurrentUser.CreateSubKey(RunKey))
            {
                if (enabled)
                {
                    string exe = Assembly.GetExecutingAssembly().Location;
                    k.SetValue(ValueName, "\"" + exe + "\" --minimized");
                }
                else if (k.GetValue(ValueName) != null)
                {
                    k.DeleteValue(ValueName, false);
                }
            }
        }
    }
}
