<div align="center">

<img src="assets/icon_preview.png" alt="LingoLens Logo" width="110" />

# LingoLens

**Real-time screen & game OCR overlay translator for Windows.**  
Dibuat murni dengan C# (.NET Framework + WinRT OCR) tanpa Python atau dependensi berat.

[![Release](https://img.shields.io/github/v/release/danzyyy19/LingoLens?style=flat-square&color=d97757)](https://github.com/danzyyy19/LingoLens/releases/latest)
[![Platform](https://img.shields.io/badge/platform-Windows%2010%2F11%20x64-blue?style=flat-square)](https://github.com/danzyyy19/LingoLens/releases/latest)
[![License](https://img.shields.io/badge/license-MIT-green?style=flat-square)](LICENSE)
[![Author](https://img.shields.io/badge/by-Danzyyy-orange?style=flat-square)](https://github.com/danzyyy19)

[Download](https://github.com/danzyyy19/LingoLens/releases/latest) · [Fitur Utama](#-fitur-utama) · [Cara Pakai](#-cara-pakai) · [Build dari Source](#-build-dari-source)

</div>

---

## Tentang LingoLens

**LingoLens** adalah aplikasi penerjemah layar *real-time* ringan untuk Windows yang dirancang khusus untuk bermain game (RPG, Visual Novel, JRPG, dll.) maupun membaca teks di layar secara langsung. Teks diambil secara instan menggunakan **Windows WinRT OCR** dan ditampilkan di atas game dalam bentuk overlay yang bersih, tidak mengganggu gameplay (*click-through*), dan dapat dikustomisasi per area.

> Aplikasi ini **100% native C#** (.NET Framework 4.8 bawaan Windows) — **tidak membutuhkan Python, runtime tambahan, atau hak Administrator.**

---

## Download

Unduh versi siap pakai melalui halaman [**GitHub Releases**](https://github.com/danzyyy19/LingoLens/releases/latest):

| Versi | File | Keterangan |
| :--- | :--- | :--- |
| **Portable** | `LingoLens-Portable.exe` | Langsung jalankan tanpa install; konfigurasi & profil game tersimpan portabel. |
| **Installer** | `Setup-LingoLens.exe` | Pasang ke Windows dengan shortcut Start Menu, Desktop, dan Uninstaller. |

---

## Fitur Utama

- **WinRT OCR Cepat & Akurat** — Memanfaatkan OCR bawaan Windows 10/11 secara *in-process* (ditambah fallback OCR online untuk bahasa yang belum terpasang).
- **Mode Dual-Area (Dialog & Pilihan)** — Pisahkan Area 1 untuk kotak dialog dan Area 2 untuk opsi pilihan game agar pilihan diterjemahkan rapi per baris tanpa tercampur menjadi satu kalimat.
- **Terjemahan Natural Sehari-hari & Opsi Gemini AI** — Pra-pemrosesan idiom game dan gaya bahasa Indonesia sehari-hari yang sopan (`NaturalTone`) agar hasil tidak kaku, dengan dukungan **Google Translate**, **MyMemory**, serta opsional **Gemini AI**.
- **Glosarium & Kamus Nama Karakter** — Lindungi nama karakter, lokasi, atau istilah khusus per game (mis. *Hyrule*, *Saria*, *Triforce*, atau `Master Sword=Pedang Master`) agar tidak diterjemahkan secara harfiah.
- **Deteksi Nama Pembicara (Speaker Badge)** — Memisahkan otomatis tag nama pembicara (mis. `Saria:`) dan menampilkannya sebagai badge warna tersendiri di atas kotak dialog.
- **Pengaturan Tampilan Per-Area** — Atur font, ukuran teks, perataan, warna, opasitas, outline, dan *Auto-Fit* secara independen untuk setiap area (mis. teks Pilihan lebih kecil daripada Dialog).
- **Profil / Template per Judul Game** — Simpan dan muat ulang posisi area OCR, kotak hasil, serta gaya overlay berdasarkan judul game.
- **UI Minimalis Dark & Light Mode** — Desain bersih dengan rendering teks native Windows ClearType, Zoom 75%–150%, Fullscreen (`F11`), dan System Tray.
- **Auto-Blank Idle** — Overlay otomatis menghilang saat tidak ada dialog baru agar layar tetap bersih.

---

## Cara Pakai

1. Jalankan **`LingoLens-Portable.exe`** (atau install lewat **`Setup-LingoLens.exe`**).
2. Buka game kamu dalam mode **Borderless Windowed** atau **Windowed**.
3. Klik **`Blok Area OCR`** (`Alt+Q`) untuk menandai area teks asli pada layar, lalu **`Blok Kotak Hasil`** (`Alt+A`) untuk menentukan posisi & ukuran hasil terjemahan. *(Tekan `ESC` kapan saja untuk membatalkan).*
4. Tekan **`Mulai Live`** (`Alt+E`) dan mainkan game seperti biasa.
5. Gunakan tombol **`Simpan`** di bar atas untuk menyimpan template profil berdasarkan judul game.

### Pintasan Keyboard (Global Hotkey)
- `Alt + E` — Mulai / Hentikan Live Translation
- `Alt + Q` — Blok Area OCR di layar
- `Alt + A` — Blok Kotak Hasil di layar
- `Alt + W` — Mode Edit / Geser Kotak langsung di atas layar
- `F11` — Fullscreen jendela aplikasi

---

## Build dari Source

Tidak perlu Visual Studio atau .NET SDK — cukup gunakan compiler `csc.exe` bawaan Windows via PowerShell:

```powershell

```

Hasil build akan otomatis dibuat di folder `dist/`:
- `dist\LingoLens-Portable.exe`
- `dist\Setup-LingoLens.exe`

---

## Lisensi

Dirilis di bawah lisensi [MIT](LICENSE) — Dibuat oleh **Danzyyy**.
