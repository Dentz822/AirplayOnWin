using System.Runtime.InteropServices;
using AirPlay.Core;
using AirPlay.Core.Discovery;
using AirPlay.Core.Identity;
using AirPlay.Core.Rtsp;

// Development host. Usage: AirPlay.Cli [--name "顯示名稱"] [-v] [--no-record]
string? name = null;
var verbose = false;
var record = true;
for (var i = 0; i < args.Length; i++)
{
    if (args[i] == "--name" && i + 1 < args.Length) name = args[++i];
    else if (args[i] is "-v" or "--verbose") verbose = true;
    else if (args[i] == "--no-record") record = false;
}

// Console encoding: in a console window, switch it to UTF-8. When output is piped
// (e.g. "| Tee-Object"), Windows PowerShell 5.1 decodes it with the console's original code page
// (Big5 on Traditional Chinese Windows), so keep writing in that code page or the text is garbled.
if (!Console.IsOutputRedirected) Console.OutputEncoding = System.Text.Encoding.UTF8;

// Every run also writes a log file. UTF-8 *with* BOM, so Notepad, Get-Content and other
// Windows tools all detect the encoding instead of guessing Big5.
var logDir = Path.Combine(Environment.CurrentDirectory, "logs");
Directory.CreateDirectory(logDir);
var logPath = Path.Combine(logDir, $"airplay-{DateTime.Now:yyyyMMdd-HHmmss}.log");
using var logFile = new StreamWriter(logPath, false, new System.Text.UTF8Encoding(true)) { AutoFlush = true };
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

// ---- shutdown handling ----
// Every way of closing must withdraw the mDNS services, or iPhones keep listing this PC:
//   Ctrl+C / Ctrl+Break -> CancelKeyPress
//   window X button      -> SIGHUP (CTRL_CLOSE_EVENT): Windows kills the process ~5 s after the handler returns
//   logoff / shutdown    -> SIGTERM
// The signal handlers block until cleanup below has finished.
using var cts = new CancellationTokenSource();
var cleanupDone = new ManualResetEventSlim();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };
void OnCloseSignal(PosixSignalContext ctx)
{
    ctx.Cancel = true;
    cts.Cancel();
    cleanupDone.Wait(TimeSpan.FromSeconds(4));
}
using var onClose = PosixSignalRegistration.Create(PosixSignal.SIGHUP, OnCloseSignal);
using var onTerm = PosixSignalRegistration.Create(PosixSignal.SIGTERM, OnCloseSignal);

var recordDir = Path.Combine(Environment.CurrentDirectory, "recordings");
var options = new ReceiverOptions
{
    Verbose = verbose,
    MirrorSinkFactory = record ? () => new H264Recorder(recordDir, Log) : null,
};
var server = new RtspServer(AirPlayServices.Port, conn => new AirPlayConnection(id, options, conn, Log), Log);
var serverTask = server.RunAsync(cts.Token);

try
{
    using (var services = await AirPlayServices.StartAsync(id))
    {
        foreach (var s in services.ServiceNames) Log($"mDNS 已廣播: {s}");
        Log("在 iPhone 打開「控制中心 → 螢幕鏡像輸出」，選這台電腦。按 Ctrl+C 或關閉視窗結束。");

        try { await Task.WhenAny(serverTask, Task.Delay(Timeout.Infinite, cts.Token)); }
        catch (OperationCanceledException) { }
        cts.Cancel();
        Log("正在停止…");
    } // services.Dispose(): mDNS goodbye first, so iPhones drop this PC right away
    Log("mDNS 已停止廣播");

    await serverTask; // closes connections, finishes recordings
    Log("已停止。");
}
finally
{
    cleanupDone.Set();
}
