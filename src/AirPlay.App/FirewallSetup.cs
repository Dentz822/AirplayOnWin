using System.ComponentModel;
using System.Diagnostics;
using System.IO;

namespace AirPlay.App;

/// <summary>
/// Checks for and creates the inbound firewall rules the receiver needs (same rules as
/// scripts\setup-firewall.ps1), so the window app works without running the script by hand.
/// The rules are created by a second, elevated copy of this exe through the Windows Firewall
/// COM API, not by PowerShell (antivirus software often blocks hidden encoded PowerShell).
/// </summary>
public static class FirewallSetup
{
    /// <summary>Command-line switch for the elevated copy: <c>--setup-firewall "result file"</c>.</summary>
    public const string SetupArgument = "--setup-firewall";

    private static readonly (string Name, int Protocol, string Ports)[] Rules =
    [
        ("WindowsAirplay TCP", 6, "7000,7001,7100"),
        ("WindowsAirplay UDP", 17, "6000-6010,7011"),
    ];

    // INetFwRule constants
    private const int DirectionIn = 1;
    private const int ActionAllow = 1;
    private const int ProfilePrivate = 2;

    /// <summary>True if both rules exist and are enabled (null if the check itself failed).</summary>
    public static bool? RulesPresent()
    {
        try
        {
            dynamic policy = Policy();
            var found = new HashSet<string>();
            foreach (dynamic rule in policy.Rules)
            {
                string name = rule.Name;
                if (Rules.Any(r => r.Name == name) && (bool)rule.Enabled) found.Add(name);
            }
            return found.Count == Rules.Length;
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>
    /// Starts the elevated copy (Windows shows a UAC prompt) and waits for it.
    /// Returns null on success, otherwise a message for the user.
    /// </summary>
    public static string? CreateRules()
    {
        var exe = Environment.ProcessPath;
        if (exe is null) return "找不到程式的路徑。";
        var resultFile = Path.Combine(Path.GetTempPath(), $"WindowsAirplay-firewall-{Guid.NewGuid():N}.txt");
        try
        {
            using var process = Process.Start(new ProcessStartInfo(exe, $"{SetupArgument} \"{resultFile}\"")
            {
                UseShellExecute = true,
                Verb = "runas", // UAC elevation
            });
            if (process is null) return "無法啟動設定程式。";
            if (!process.WaitForExit(120_000)) return "設定逾時（超過 2 分鐘沒有完成）。";

            var detail = File.Exists(resultFile) ? File.ReadAllText(resultFile).Trim() : "";
            if (process.ExitCode != 0)
                return detail.Length > 0 ? detail : $"設定程式結束碼 {process.ExitCode}。";
            return RulesPresent() == false ? "規則建立後檢查不到，可能被防毒軟體或公司政策擋下。" : null;
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            return "已取消（在系統管理員確認視窗按了「否」）。";
        }
        catch (Exception ex)
        {
            return ex.Message;
        }
        finally
        {
            try { File.Delete(resultFile); } catch { /* best effort */ }
        }
    }

    /// <summary>Runs in the elevated copy: creates the rules, writes any error to <paramref name="resultFile"/>.</summary>
    public static int RunElevated(string? resultFile)
    {
        string? error = null;
        try
        {
            dynamic policy = Policy();
            var ruleType = Type.GetTypeFromProgID("HNetCfg.FWRule")
                           ?? throw new InvalidOperationException("Windows 防火牆元件無法使用。");
            foreach (var (name, protocol, ports) in Rules)
            {
                RemoveAll(policy, name);
                dynamic rule = Activator.CreateInstance(ruleType)!;
                rule.Name = name;
                rule.Description = "讓 iPhone 透過 AirPlay 連到 WindowsAirplay";
                rule.Grouping = "WindowsAirplay";
                rule.Protocol = protocol; // must be set before the ports
                rule.LocalPorts = ports;
                rule.Direction = DirectionIn;
                rule.Action = ActionAllow;
                rule.Profiles = ProfilePrivate;
                rule.Enabled = true;
                policy.Rules.Add(rule);
            }
            EnableMdns(policy);
        }
        catch (Exception ex)
        {
            error = $"建立防火牆規則失敗：{ex.Message}";
            if (ex is UnauthorizedAccessException || ex.HResult == unchecked((int)0x80070005))
                error += "\n（權限不足，可能是公司電腦的群組原則或防毒軟體在管理防火牆。）";
        }

        if (resultFile is not null)
        {
            try { File.WriteAllText(resultFile, error ?? "ok"); } catch { /* the exit code still tells */ }
        }
        return error is null ? 0 : 1;
    }

    private static object Policy()
    {
        var policyType = Type.GetTypeFromProgID("HNetCfg.FwPolicy2")
                         ?? throw new InvalidOperationException("Windows 防火牆元件無法使用。");
        return Activator.CreateInstance(policyType)!;
    }

    private static void RemoveAll(dynamic policy, string name)
    {
        // Rules.Remove deletes one rule with that name per call.
        for (var i = 0; i < 20 && Exists(policy, name); i++) policy.Rules.Remove(name);
    }

    private static bool Exists(dynamic policy, string name)
    {
        foreach (dynamic rule in policy.Rules)
            if ((string)rule.Name == name) return true;
        return false;
    }

    /// <summary>Turns on Windows' built-in inbound mDNS rules (UDP 5353) for private networks.</summary>
    private static void EnableMdns(dynamic policy)
    {
        foreach (dynamic rule in policy.Rules)
        {
            try
            {
                if ((int)rule.Direction == DirectionIn && (int)rule.Protocol == 17 &&
                    (string?)rule.LocalPorts == "5353" && ((int)rule.Profiles & ProfilePrivate) != 0 &&
                    !(bool)rule.Enabled)
                    rule.Enabled = true;
            }
            catch { /* some rules can't be read or changed; mDNS still works without this */ }
        }
    }

    /// <summary>Names of connected networks set to "Public" (where the Private-only rules don't apply).</summary>
    public static IReadOnlyList<string> PublicNetworks()
    {
        try
        {
            var nlmType = Type.GetTypeFromCLSID(new Guid("DCB00C01-570F-4A9B-8D69-199FDBA5723B"));
            if (nlmType is null) return [];
            dynamic manager = Activator.CreateInstance(nlmType)!;
            var result = new List<string>();
            // NLM_ENUM_NETWORK_CONNECTED = 1; category 0 = Public
            foreach (dynamic network in manager.GetNetworks(1))
                if ((int)network.GetCategory() == 0) result.Add((string)network.GetName());
            return result;
        }
        catch (Exception)
        {
            return [];
        }
    }
}
