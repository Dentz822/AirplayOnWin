# 以「系統管理員」身分執行：
#   powershell -ExecutionPolicy Bypass -File scripts\setup-firewall.ps1
# 開放 AirPlay 需要的連接埠（只限「私人」網路），並啟用 Windows 內建的 mDNS 規則。

$ErrorActionPreference = 'Stop'

$principal = New-Object Security.Principal.WindowsPrincipal([Security.Principal.WindowsIdentity]::GetCurrent())
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    Write-Error '請用系統管理員身分執行這個腳本。'
}

$rules = @(
    @{ Name = 'WindowsAirplay TCP'; Protocol = 'TCP'; Ports = @('7000', '7001', '7100') },
    @{ Name = 'WindowsAirplay UDP'; Protocol = 'UDP'; Ports = @('6000-6010', '7011') }
)

foreach ($r in $rules) {
    Get-NetFirewallRule -DisplayName $r.Name -ErrorAction SilentlyContinue | Remove-NetFirewallRule
    New-NetFirewallRule -DisplayName $r.Name -Direction Inbound -Action Allow -Profile Private `
        -Protocol $r.Protocol -LocalPort $r.Ports | Out-Null
    Write-Host "已新增規則: $($r.Name) ($($r.Protocol) $($r.Ports -join ', '))"
}

# mDNS (UDP 5353) — Windows 內建規則，確保在私人網路已啟用
Get-NetFirewallRule -DisplayGroup 'mDNS' -ErrorAction SilentlyContinue |
    Where-Object { $_.Profile -match 'Private|Any' } | Enable-NetFirewallRule
Write-Host '已啟用 mDNS 內建規則'

# 提醒：網路設定成「公用」時，上面的規則都不會生效
Get-NetConnectionProfile | ForEach-Object {
    $flag = if ($_.NetworkCategory -eq 'Public') { '  <-- 目前是「公用」，請改成「私人」' } else { '' }
    Write-Host "網路 '$($_.Name)': $($_.NetworkCategory)$flag"
}
