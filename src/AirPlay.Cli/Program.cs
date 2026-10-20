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

// Every run also writes a UTF-8 log file, so no "| Tee-Object" is needed
// (Windows PowerShell 5.1 re-decodes piped output as Big5 and garbles the Chinese text).
var logDir = Path.Combine(Environment.CurrentDirectory, "logs");
Directory.CreateDirectory(logDir);
var logPath = Path.Combine(logDir, $"airplay-{DateTime.Now:yyyyMMdd-HHmmss}.log");
using var logFile = new StreamWriter(logPath, false, new System.Text.UTF8Encoding(false)) { AutoFlush = true };
var logLock = new object();
void Log(string msg)
{
    var line = $"{DateTime.Now:HH:mm:ss.fff} {msg}";
    lock (logLock)
    {
        Console.WriteLine(line);
        logFile.WriteLine(line);
    }
}

Log($"記錄檔: {logPath}");
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
