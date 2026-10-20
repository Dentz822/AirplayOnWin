# WindowsAirplay

讓 iPhone 透過 AirPlay「螢幕鏡像輸出」投影到 Windows 11 桌面（含聲音）。C# / .NET 10，個人使用。

## 目前進度

| 階段 | 內容 | 狀態 |
|---|---|---|
| 0 | 方案結構、防火牆腳本 | ✅ |
| 1 | mDNS 廣播 `_airplay._tcp` / `_raop._tcp`（使用 Windows 內建 API，不需 Bonjour） | ✅ |
| 2 | RTSP 伺服器、`GET /info` | ✅ iPhone 15 Pro Max / iOS 26.6 實測通過 |
| 3 | pair-setup / pair-verify | ✅ 實測通過 |
| 4 | FairPlay（playfair.dll） | ✅ 實測通過（mode 2，解出 aeskey） |
| 5 | 時間同步（NTP）、畫面串流接收與解密、存成 .h264 | ✅ 實測通過（約 30 fps） |
| 6 | FFmpeg 解碼並顯示在 WPF 視窗 | ✅ 模擬 iPhone 測試通過 |
| 7 | 聲音：解密、重排、補送、AAC-ELD / AAC / ALAC 解碼、喇叭播放、iPhone 音量 | ✅ 模擬 iPhone 測試通過（AAC-ELD 待實機） |
| 8 | 影音同步（畫面跟著聲音的時間軸顯示） | ✅ 模擬 iPhone 測試：平均誤差 9 ms |
| 9 | 產品化（全螢幕、系統匣、自動啟動） | |
| 10 | 產生可直接執行的 .exe（不需安裝 .NET） | |

## 執行

1. 第一次使用時，以**系統管理員**身分開 PowerShell 設定防火牆：
   ```powershell
   powershell -ExecutionPolicy Bypass -File scripts\setup-firewall.ps1
   ```
   網路設定檔必須是「私人」，腳本會顯示目前的狀態。
2. 啟動視窗版：
   ```powershell
   dotnet run --project src\AirPlay.App
   ```
3. iPhone 和電腦連同一個 Wi-Fi，打開「控制中心 → 螢幕鏡像輸出」，選這台電腦。

視窗操作：**F11** 或**雙擊**切換全螢幕、**Esc** 離開全螢幕、**右鍵**選單（最上層顯示、開啟記錄資料夾、結束）。
關閉視窗會先停止 mDNS 廣播，iPhone 清單裡就不會再出現這台電腦。同一時間只能開一個。

視窗版的記錄檔在 `%LOCALAPPDATA%\WindowsAirplay\logs\`（右鍵選單可開啟）。

### 主控台版（開發用）

```powershell
dotnet run --project src\AirPlay.Cli                    # 記錄印在畫面上，投影內容存成 recordings\mirror-*.h264
dotnet run --project src\AirPlay.Cli -- -v              # 詳細記錄（標頭、plist 內容）
dotnet run --project src\AirPlay.Cli -- --no-record     # 不存錄影檔
dotnet run --project src\AirPlay.Cli -- --name "我的電腦" # 自訂顯示名稱（視窗版也會沿用）
dotnet run --project src\AirPlay.App -- --play recordings\mirror-xxx.h264   # 用視窗版播放錄影
```
主控台版的記錄檔在 `logs\`（UTF-8）。不要用 `| Tee-Object` 存記錄，直接看記錄檔即可。
主控台版和視窗版都使用連接埠 7000，不能同時開。

## 測試

```powershell
dotnet test
```

模擬 iPhone（不需手機，對執行中的接收端跑完整協定，畫面用錄影檔、聲音用 AAC 測試音）：
```powershell
dotnet run --project tools\AirPlay.FakeSender -- recordings\mirror-xxx.h264 --seconds 8 --volume -25
```
測試用模擬的 iPhone 跑完 `/info`、配對、FairPlay、時間同步和畫面串流，用 NIST 測試向量驗證 AES-CTR，並用 FFmpeg 編碼的影片與聲音驗證解碼（ALAC 無損比對、AAC 頻率檢查）。

## 專案結構

```
src/AirPlay.Core/
  Identity/DeviceIdentity.cs    裝置身分（持久化）
  Discovery/                    mDNS 廣播（dnsapi.dll P/Invoke）與 TXT 記錄
  Rtsp/                         RTSP/HTTP 請求解析與 TCP 伺服器
  Pairing/PairingSession.cs     pair-setup / pair-verify（Ed25519 + X25519 + AES-CTR）
  FairPlay/FairPlaySession.cs   FairPlay 握手與 ekey 解密（呼叫 playfair.dll）
  Crypto/AesCtr.cs              連續的 AES-128-CTR（配對與畫面解密共用）
  Timing/NtpClient.cs           與 iPhone 的 NTP 時間同步
  Timing/PlaybackClock.cs       影音同步：把畫面與聲音的時間戳換到同一條時間軸
  Mirroring/MirrorStream.cs     畫面串流：接收、解密、AVCC 轉 Annex-B
  Audio/AudioStream.cs          音訊串流：AES-CBC 解密、依序號重排、遺失時請 iPhone 補送
  Net/PortBinder.cs             以獨占方式綁定連接埠（避免被其他程式搶走封包）
  AirPlayConnection.cs          每個 iPhone 連線的請求處理
  AirPlayReceiver.cs            啟動/停止整個接收端（RTSP + mDNS）
  FileLog.cs                    記錄檔（UTF-8 BOM）
src/AirPlay.Media/
  H264Decoder.cs                FFmpeg H.264 解碼並轉成 BGRA
  AnnexBReader.cs               把錄影檔切回一幀一幀
  AudioDecoder.cs               FFmpeg 解碼 AAC-ELD / AAC / ALAC 成 16-bit PCM
  FrameQueue.cs                 解碼好的畫面排隊，等到該播放的時間才顯示
src/AirPlay.App/                視窗版（WPF）：即時顯示、全螢幕、聲音播放（NAudio WASAPI）
src/AirPlay.Cli/                開發用主控台程式（含 .h264 錄影）
native/playfair/                FairPlay C 程式（取自 UxPlay，GPL）；bin/playfair.dll 為預先編好的版本
tests/AirPlay.Tests/            自動測試
tools/AirPlay.FakeSender/       模擬 iPhone（端對端測試用）
scripts/setup-firewall.ps1      防火牆設定
```

重新編譯 playfair.dll（只有改 C 程式時才需要）：設定 `ZIG=zig.exe 路徑` 或安裝 VS「使用 C++ 的桌面開發」，再執行 `native\playfair\build.cmd`。

## 授權

本專案以 [GPL-3.0](LICENSE) 授權。

- `native/playfair/src/` 的 FairPlay 程式取自 [UxPlay](https://github.com/FDH2/UxPlay)（GPL-3.0），未修改。
- `native/playfair/fairplay_export.c` 改寫自 UxPlay 的 `fairplay_playfair.c`（© 2018 Juho Vähä-Herttua，LGPL-2.1+）。
- AirPlay 協定的流程與參數參考 UxPlay、RPiPlay 的實作，C# 程式為重新撰寫。
- 使用的 NuGet 套件：BouncyCastle.Cryptography（MIT）、plist-cil（MIT）、FFmpeg.AutoGen（LGPL-3.0）、NAudio.Wasapi（MIT）、Sdcb.FFmpeg.runtime.windows-x64（FFmpeg 7.1 DLL，GPL-3.0）、xunit（Apache-2.0）。

## 聲明

本專案與 Apple Inc. 無關，也未經 Apple 授權或認可。AirPlay、FairPlay、iPhone 為 Apple Inc. 的商標。
