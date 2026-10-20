using System.ComponentModel;
using System.Diagnostics;

namespace AirPlay.App;

/// <summary>
/// Checks for and creates the inbound firewall rules the receiver needs (same rules as
/// scripts\setup-firewall.ps1), so the window app works without running the script by hand.
/// </summary>
public static class FirewallSetup
{
    private static readonly string[] RuleNames = ["WindowsAirplay TCP", "WindowsAirplay UDP"];

    /// <summary>True if both rules exist and are enabled (null if the check itself failed).</summary>
    public static bool? RulesPresent()
    {
        try
        {
            var policyType = Type.GetTypeFromProgID("HNetCfg.FwPolicy2");
            if (policyType is null) return null;
            dynamic policy = Activator.CreateInstance(policyType)!;
            var found = new HashSet<string>();
            foreach (dynamic rule in policy.Rules)
            {
                string name = rule.Name;
                if (RuleNames.Contains(name) && (bool)rule.Enabled) found.Add(name);
            }
            return found.Count == RuleNames.Length;
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>
    /// Creates the rules in an elevated PowerShell (Windows shows a UAC prompt).
    /// Returns false if the user declined or it failed.
    /// </summary>
    public static bool CreateRules()
    {
        const string script = """
            $ErrorActionPreference = 'Stop'
            foreach ($r in @(
                @{ Name = 'WindowsAirplay TCP'; Protocol = 'TCP'; Ports = @('7000', '7001', '7100') },
                @{ Name = 'WindowsAirplay UDP'; Protocol = 'UDP'; Ports = @('6000-6010', '7011') })) {
                Get-NetFirewallRule -DisplayName $r.Name -ErrorAction SilentlyContinue | Remove-NetFirewallRule
                New-NetFirewallRule -DisplayName $r.Name -Direction Inbound -Action Allow -Profile Private `
                    -Protocol $r.Protocol -LocalPort $r.Ports | Out-Null
            }
            Get-NetFirewallRule -DisplayGroup 'mDNS' -ErrorAction SilentlyContinue |
                Where-Object { $_.Profile -match 'Private|Any' } | Enable-NetFirewallRule
            """;
        var encoded = Convert.ToBase64String(System.Text.Encoding.Unicode.GetBytes(script));
        try
        {
            using var process = Process.Start(new ProcessStartInfo("powershell.exe",
                $"-NoProfile -ExecutionPolicy Bypass -EncodedCommand {encoded}")
            {
                UseShellExecute = true,
                Verb = "runas", // UAC elevation
                WindowStyle = ProcessWindowStyle.Hidden,
            });
            process?.WaitForExit(30_000);
            return process?.ExitCode == 0 && RulesPresent() == true;
        }
        catch (Win32Exception)
        {
            return false; // the user clicked "No" on the UAC prompt
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
