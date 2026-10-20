# playfair

FairPlay（SAP，type 0x03）握手與 ekey 解密，給 AirPlay 螢幕鏡像使用。

| 檔案 | 來源 | 授權 |
|---|---|---|
| `src/*` | [UxPlay](https://github.com/FDH2/UxPlay) `lib/playfair/`（commit 4e95a9e），未修改 | GPL-3.0（`src/LICENSE.md`） |
| `fairplay_export.c` | UxPlay `lib/fairplay_playfair.c`，© 2018 Juho Vähä-Herttua，改寫成 DLL 匯出函式 | LGPL-2.1+ |
| `bin/playfair.dll` | 由上面的原始碼以 `build.cmd` 編譯（x64） | GPL-3.0 |

重新編譯：設定 `ZIG=zig.exe 路徑` 或安裝 Visual Studio「使用 C++ 的桌面開發」，再執行 `build.cmd`。
