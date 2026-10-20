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
| 9 | 產品化：設定、重新命名、開機啟動、系統匣、防火牆一鍵設定、30/60 fps、圖示 | ✅ |
| 10 | 產生可直接執行的 .exe（不需安裝 .NET） | ✅ `dist\WindowsAirplay.exe`，約 106 MB |

## 支援的 AirPlay 方式

| iPhone 上的操作 | 效果 |
|---|---|
| 控制中心 →「螢幕鏡像輸出」 | 整個手機畫面＋聲音（所有 App 都可以） |
| App 裡的 AirPlay 按鈕：**YouTube** | 電腦直接播放影片（可暫停、拖曳進度），最高選 1080p H.264 |
| App 裡的 AirPlay 按鈕：**Twitch**（直播） | 電腦直接播放直播 |
| App 裡的 AirPlay 按鈕：**Safari 網頁影片** | 電腦直接播放影片網址（HLS / MP4） |
| App 裡的 AirPlay 按鈕：**音樂 App** | 播放聲音，視窗顯示封面、歌名、歌手、進度 |
| App 裡的 AirPlay 按鈕：**Netflix、Disney+、Apple TV+ 等** | ✗ 這些影片有 DRM（FairPlay）版權保護，只有 Apple 裝置能播放，請改用「螢幕鏡像輸出」 |

YouTube 直播（沒有結尾的播放清單）目前不支援。

**螢幕鏡像中打開 YouTube / Twitch**：iOS 會自動切換成「影片模式」（電腦直接播放影片，不再鏡像手機畫面）。
這是 iOS 的行為，接收端無法拒絕（拒絕的話 iPhone 仍會結束鏡像）。想永遠鏡像整個畫面，請在右鍵選單關閉「App 影片直接播放」
（代價：App 裡的 AirPlay 按鈕只會有聲音）。

## 執行檔

```powershell
powershell -ExecutionPolicy Bypass -File scripts\publish.ps1
```
產生 `dist\WindowsAirplay.exe`：單一檔案，內含 .NET、FFmpeg 和 FairPlay，Windows 10/11 x64 可直接執行，不需安裝任何東西。
可以複製到任何資料夾或其他電腦。第一次執行會把內含的 DLL 解壓到暫存資料夾（幾秒鐘），之後就很快。

## 執行（開發）

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

視窗操作：**F11** 或**雙擊**切換全螢幕、**Esc** 離開全螢幕。**右鍵**選單：

| 選單 | 說明 |
|---|---|
| 最上層顯示 | 視窗一直在最上面 |
| 重新命名… | 改 iPhone 清單裡顯示的名稱，立即生效 |
| 畫面更新率 | 30 fps（省電）或 60 fps（流暢，預設），下次連線生效 |
| 開機時自動啟動 | 登入 Windows 時自動啟動（縮小） |
| 關閉視窗時縮到系統匣 | 開啟後按 X 只是隱藏，iPhone 仍可連線；從系統匣圖示結束。預設關閉：按 X 就完全結束 |
| App 影片直接播放 | App 的 AirPlay 按鈕（YouTube、Twitch、Safari）由電腦直接播放影片；鏡像中開這些影片會自動切成影片模式。關閉後永遠鏡像整個畫面，App 的 AirPlay 按鈕只有聲音 |
| 設定防火牆… | 一鍵建立防火牆規則（會跳出系統管理員確認）。沒設定時視窗也會提示 |

- 完全結束程式時會先停止 mDNS 廣播，iPhone 清單裡就不會再出現這台電腦。
- 同一時間只能開一個；再開一次會把原本的視窗叫到前面。
- iPhone 開始投影時，視窗會自動跳到前面（即使縮小或在系統匣）。
- 設定檔：`%LOCALAPPDATA%\WindowsAirplay\settings.json`；記錄檔：`%LOCALAPPDATA%\WindowsAirplay\logs\`（右鍵選單可開啟）。

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

模擬 iPhone（不需手機，對執行中的接收端跑完整協定）：
```powershell
dotnet run --project tools\AirPlay.FakeSender -- recordings\mirror-xxx.h264 --seconds 8 --volume -25   # 螢幕鏡像（錄影檔＋測試音）
dotnet run --project tools\AirPlay.FakeSender -- recordings\mirror-xxx.h264 --audio-only --cover a.png # 音樂模式（歌曲資訊＋封面）
dotnet run --project tools\AirPlay.FakeSender -- --video-url --seconds 20   # AirPlay 影片：Safari 網址
dotnet run --project tools\AirPlay.FakeSender -- --youtube --seconds 20     # AirPlay 影片：YouTube（FCUP＋壓縮清單）
dotnet run --project tools\AirPlay.FakeSender -- --twitch --seconds 20      # AirPlay 影片：Twitch（多畫質主清單＋插入/移除播放項目）
#   影片模式可加：--1080p60（1080p 60fps 測試影片）、--no-stop（不送 /stop，直接斷線）
```
影片模式會自己產生測試影片並開一個小網頁伺服器，然後測試播放、暫停、繼續、拖曳、停止。
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
  Metadata/NowPlaying.cs        音樂模式的歌曲資訊（DMAP）與進度
  Video/AirPlayVideoService.cs  AirPlay 影片：/play、FCUP、/playback-info、暫停/拖曳/停止
  Video/HlsPlaylist.cs          HLS 清單：選畫質與音軌、展開 YouTube 壓縮清單
  Video/ReverseChannel.cs       反向 HTTP（PTTH）連線
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
  UrlPlayer.cs                  FFmpeg 播放影片網址（HLS / MP4，HTTPS）：解碼、暫停、精準跳轉
src/AirPlay.App/                視窗版（WPF，輸出 WindowsAirplay.exe）：即時顯示、全螢幕、聲音播放（NAudio WASAPI）、
                                設定、系統匣、開機啟動、防火牆設定
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
