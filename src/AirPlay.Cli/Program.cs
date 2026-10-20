using AirPlay.Core;
using AirPlay.Core.Discovery;
using AirPlay.Core.Identity;
using AirPlay.Core.Rtsp;

// Development host. Usage: AirPlay.Cli [--name "顯示名稱"] [-v]
string? name = null;
var verbose = false;
for (var i = 0; i < args.Length; i++)
{
    if (args[i] == "--name" && i + 1 < args.Length) name = args[++i];
    else if (args[i] is "-v" or "--verbose") verbose = true;
}

Console.OutputEncoding = System.Text.Encoding.UTF8;
var logLock = new object();
void Log(string msg) { lock (logLock) Console.WriteLine($"{DateTime.Now:HH:mm:ss.fff} {msg}"); }

var id = DeviceIdentity.LoadOrCreate(name);
Log($"裝置名稱: {id.Name}");
Log($"Device ID: {id.DeviceId}");
Log($"身分檔案: {DeviceIdentity.DefaultPath}");

using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

var options = new ReceiverOptions { Verbose = verbose };
var server = new RtspServer(AirPlayServices.Port, _ => new AirPlayConnection(id, options, Log), Log);
var serverTask = server.RunAsync(cts.Token);

using var services = await AirPlayServices.StartAsync(id);
foreach (var s in services.ServiceNames) Log($"mDNS 已廣播: {s}");
Log("在 iPhone 打開「控制中心 → 螢幕鏡像輸出」，選這台電腦。按 Ctrl+C 結束。");

await serverTask;
Log("已停止。");
