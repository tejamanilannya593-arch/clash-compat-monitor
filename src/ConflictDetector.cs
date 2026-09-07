using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using Microsoft.Win32;

public sealed class RuntimeSnapshot
{
    public RuntimeSnapshot(bool pipeAvailable, string systemProxy, string portOwner, bool otherVpnRoute)
    {
        PipeAvailable = pipeAvailable;
        SystemProxy = systemProxy ?? "";
        PortOwner = portOwner ?? "";
        OtherVpnRoute = otherVpnRoute;
    }
    public bool PipeAvailable { get; private set; }
    public string SystemProxy { get; private set; }
    public string PortOwner { get; private set; }
    public bool OtherVpnRoute { get; private set; }
}

public sealed class ConflictResult
{
    public ConflictResult(bool paused, string reason) { Paused = paused; Reason = reason; }
    public bool Paused { get; private set; }
    public string Reason { get; private set; }
}

public sealed class ConflictDetector
{
    public ConflictResult Evaluate(RuntimeSnapshot snapshot)
    {
        if (!snapshot.PipeAvailable) return new ConflictResult(true, "mihomo pipe unavailable");
        if (!string.Equals(snapshot.PortOwner, "verge-mihomo", StringComparison.OrdinalIgnoreCase)) return new ConflictResult(true, "port 7897 has unexpected owner");
        if (snapshot.SystemProxy.Length > 0 && !string.Equals(snapshot.SystemProxy, "127.0.0.1:7897", StringComparison.OrdinalIgnoreCase))
            return new ConflictResult(true, "system proxy differs");
        if (snapshot.OtherVpnRoute) return new ConflictResult(true, "another VPN controls routing");
        return new ConflictResult(false, "ok");
    }
}

public static class RuntimeInspector
{
    public static RuntimeSnapshot Capture(IMihomoClient client)
    {
        string proxy = "";
        using (RegistryKey key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Internet Settings"))
        {
            if (key != null && Convert.ToInt32(key.GetValue("ProxyEnable", 0)) != 0) proxy = Convert.ToString(key.GetValue("ProxyServer", ""));
        }
        return new RuntimeSnapshot(client.IsAvailable(), proxy, FindPortOwner(7897), false);
    }

    private static string FindPortOwner(int port)
    {
        try
        {
            var start = new ProcessStartInfo("netstat.exe", "-ano -p tcp") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true };
            using (Process process = Process.Start(start))
            {
                string output = process.StandardOutput.ReadToEnd();
                process.WaitForExit(3000);
                foreach (string line in output.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
                {
                    string[] fields = line.Split((char[])null, StringSplitOptions.RemoveEmptyEntries);
                    if (fields.Length < 5 || !fields[1].EndsWith(":" + port, StringComparison.Ordinal) || fields[3] != "LISTENING") continue;
                    int pid;
                    if (int.TryParse(fields[4], out pid)) return Process.GetProcessById(pid).ProcessName;
                }
            }
        }
        catch { }
        return "";
    }
}

public static class OptionalServiceActivator
{
    public static IList<ServiceKind> FromProcessNames(IEnumerable<string> processNames)
    {
        var result = new List<ServiceKind>();
        foreach (string raw in processNames ?? Enumerable.Empty<string>())
        {
            string name = System.IO.Path.GetFileNameWithoutExtension(raw ?? "");
            if (name.Equals("Discord", StringComparison.OrdinalIgnoreCase) && !result.Contains(ServiceKind.Discord)) result.Add(ServiceKind.Discord);
            if (name.Equals("Spotify", StringComparison.OrdinalIgnoreCase) && !result.Contains(ServiceKind.Spotify)) result.Add(ServiceKind.Spotify);
            if ((name.StartsWith("EpicGames", StringComparison.OrdinalIgnoreCase) || name.Equals("EpicWebHelper", StringComparison.OrdinalIgnoreCase)) && !result.Contains(ServiceKind.Epic)) result.Add(ServiceKind.Epic);
        }
        return result;
    }
}
