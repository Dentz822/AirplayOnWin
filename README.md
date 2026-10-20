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
| 5 | 畫面串流接收與解密 | |
| 6 | FFmpeg 解碼並顯示在 WPF 視窗 | |
| 7 | 聲音（AAC-ELD / ALAC） | |
| 8 | 影音同步 | |
| 9 | 產品化（全螢幕、系統匣、自動啟動） | |
| 10 | （可選）ESP32 藍牙 HID 遙控手機 | |

## 執行

1. 第一次使用時，以**系統管理員**身分開 PowerShell 設定防火牆：
   ```powershell
   powershell -ExecutionPolicy Bypass -File scripts\setup-firewall.ps1
   ```
   網路設定檔必須是「私人」，腳本會顯示目前的狀態。
2. 啟動：
   ```powershell
   dotnet run --project src\AirPlay.Cli
   dotnet run --project src\AirPlay.Cli -- --name "我的電腦"   # 自訂顯示名稱
   dotnet run --project src\AirPlay.Cli -- -v                  # 詳細記錄（標頭、plist 內容）
   ```
3. iPhone 和電腦連同一個 Wi-Fi，打開「控制中心 → 螢幕鏡像輸出」。

每次執行都會在 `logs\` 產生 UTF-8 記錄檔。不要用 `| Tee-Object` 存記錄：Windows PowerShell 5.1 會把輸出當成 Big5 解讀，中文會變亂碼。

裝置身分（名稱、Device ID、Ed25519 金鑰）存在 `%LOCALAPPDATA%\WindowsAirplay\identity.json`。

## 測試

```powershell
dotnet test
```
測試用模擬的 iPhone 跑完 `/info`、配對和 FairPlay 流程，並用 NIST 測試向量驗證 AES-CTR。

## 專案結構

```
src/AirPlay.Core/
  Identity/DeviceIdentity.cs    裝置身分（持久化）
  Discovery/                    mDNS 廣播（dnsapi.dll P/Invoke）與 TXT 記錄
  Rtsp/                         RTSP/HTTP 請求解析與 TCP 伺服器
  Pairing/PairingSession.cs     pair-setup / pair-verify（Ed25519 + X25519 + AES-CTR）
  FairPlay/FairPlaySession.cs   FairPlay 握手與 ekey 解密（呼叫 playfair.dll）
  Crypto/AesCtr.cs              連續的 AES-128-CTR（配對與畫面解密共用）
  AirPlayConnection.cs          每個 iPhone 連線的請求處理
src/AirPlay.Cli/                開發用主控台程式
native/playfair/                FairPlay C 程式（取自 UxPlay，GPL）；bin/playfair.dll 為預先編好的版本
tests/AirPlay.Tests/            自動測試
scripts/setup-firewall.ps1      防火牆設定
```

重新編譯 playfair.dll（只有改 C 程式時才需要）：設定 `ZIG=zig.exe 路徑` 或安裝 VS「使用 C++ 的桌面開發」，再執行 `native\playfair\build.cmd`。

## 授權

本專案以 [GPL-3.0](LICENSE) 授權。

- `native/playfair/src/` 的 FairPlay 程式取自 [UxPlay](https://github.com/FDH2/UxPlay)（GPL-3.0），未修改。
- `native/playfair/fairplay_export.c` 改寫自 UxPlay 的 `fairplay_playfair.c`（© 2018 Juho Vähä-Herttua，LGPL-2.1+）。
- AirPlay 協定的流程與參數參考 UxPlay、RPiPlay 的實作，C# 程式為重新撰寫。
- 使用的 NuGet 套件：BouncyCastle.Cryptography（MIT）、plist-cil（MIT）、xunit（Apache-2.0）。

## 聲明

本專案與 Apple Inc. 無關，也未經 Apple 授權或認可。AirPlay、FairPlay、iPhone 為 Apple Inc. 的商標。
